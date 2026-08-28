using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MsfsAiAtc.SimBridge;

// SimConnect data definitions (struct layout must match exact SimConnect spec)
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct AircraftData
{
    public double Latitude;
    public double Longitude;
    public double AltitudeMsl;
    public double AltitudeAgl;
    public double HeadingTrue;
    public double GroundSpeed;
    public double SimOnGround;
    public double Com1Freq;
    public double WindSpeed;
    public double WindDirection;
}

/// <summary>
/// Wraps the SimConnect connection. Designed to work with or without SimConnect DLL present
/// (graceful degradation when running without MSFS / without the SDK).
/// </summary>
public class SimConnectBridge : IDisposable
{
    private const int WM_USER_SIMCONNECT = 0x0402;
    private const string APP_NAME = "MSFS_AI_ATC";

    private readonly ILogger<SimConnectBridge> _logger;
    private readonly SimState _state = new();
    private dynamic? _simConnect; // dynamic to allow compilation without SimConnect DLL
    private HwndSource? _hwndSource;
    private System.Threading.Timer? _retryTimer;
    private bool _connected;
    private bool _disposed;

    // Events
    public event Action<SimState>? StateUpdated;
    public event Action? Connected;
    public event Action? Disconnected;

    // Expose current state (read-only snapshot)
    public SimState CurrentState => _state;

    // SimConnect definition IDs
    private enum DefId { AircraftData = 1 }
    private enum RequestId { Aircraft = 1, Facilities = 2 }
    private enum EventId { OneSecond = 1 }
    private enum GroupId { Standard = 1 }

    public SimConnectBridge(ILogger<SimConnectBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Call once the main window handle is available. Starts connection attempt loop.
    /// </summary>
    public void Initialize(IntPtr hwnd)
    {
        SetupMessageHook(hwnd);
        TryConnect(hwnd);
        // Retry every 10 s if not connected
        _retryTimer = new System.Threading.Timer(_ =>
        {
            if (!_connected)
                Application.Current?.Dispatcher.Invoke(() => TryConnect(hwnd));
        }, null, 10_000, 10_000);
    }

    private void SetupMessageHook(IntPtr hwnd)
    {
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private void TryConnect(IntPtr hwnd)
    {
        if (_connected || _disposed) return;
        try
        {
            // Load SimConnect via reflection so the project compiles without the DLL
            var simConnectType = Type.GetType(
                "Microsoft.FlightSimulator.SimConnect.SimConnect, Microsoft.FlightSimulator.SimConnect")
                ?? FindSimConnectType();

            if (simConnectType == null)
            {
                _logger.LogWarning("SimConnect DLL not found — running in simulation-less mode");
                SetSimulatedState();
                return;
            }

            _simConnect = Activator.CreateInstance(simConnectType,
                APP_NAME, hwnd, WM_USER_SIMCONNECT, null, 0);

            RegisterDataDefinitions();
            SubscribeToEvents();
            _connected = true;
            _state.IsConnected = true;
            _logger.LogInformation("SimConnect connected");
            Connected?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SimConnect connection attempt failed: {Msg}", ex.Message);
        }
    }

    private static Type? FindSimConnectType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("Microsoft.FlightSimulator.SimConnect.SimConnect");
            if (t != null) return t;
        }
        return null;
    }

    private void RegisterDataDefinitions()
    {
        if (_simConnect == null) return;
        try
        {
            // Use reflection to call AddToDataDefinition for each field
            var sc = _simConnect;
            void AddDef(string name, string units, int type)
            {
                // SIMCONNECT_DATATYPE.FLOAT64 = 8
                sc.AddToDataDefinition(DefId.AircraftData, name, units, type, 0.0f, -1);
            }

            AddDef("PLANE LATITUDE", "degrees", 8);
            AddDef("PLANE LONGITUDE", "degrees", 8);
            AddDef("PLANE ALTITUDE", "feet", 8);
            AddDef("PLANE ALT ABOVE GROUND", "feet", 8);
            AddDef("PLANE HEADING DEGREES TRUE", "degrees", 8);
            AddDef("GROUND VELOCITY", "knots", 8);
            AddDef("SIM ON GROUND", "bool", 8);
            AddDef("COM ACTIVE FREQUENCY:1", "MHz", 8);
            AddDef("AMBIENT WIND VELOCITY", "knots", 8);
            AddDef("AMBIENT WIND DIRECTION", "degrees", 8);

            sc.RegisterDataDefineStruct<AircraftData>(DefId.AircraftData);

            // Request data on 1-second intervals via sim event
            sc.SubscribeToSystemEvent(EventId.OneSecond, "1sec");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to register SimConnect data definitions: {Msg}", ex.Message);
        }
    }

    private void SubscribeToEvents()
    {
        if (_simConnect == null) return;
        try
        {
            _simConnect.OnRecvSimobjectData += new Action<dynamic, dynamic>(OnReceiveSimObjectData);
            _simConnect.OnRecvEvent += new Action<dynamic, dynamic>(OnRecvEvent);
            _simConnect.OnRecvQuit += new Action<dynamic, dynamic>(OnRecvQuit);
            _simConnect.OnRecvException += new Action<dynamic, dynamic>(OnRecvException);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to subscribe to SimConnect events: {Msg}", ex.Message);
        }
    }

    private void OnRecvEvent(dynamic sender, dynamic data)
    {
        try
        {
            if ((uint)data.uEventID == (uint)EventId.OneSecond)
            {
                // Request aircraft data update
                _simConnect?.RequestDataOnSimObject(
                    RequestId.Aircraft, DefId.AircraftData, 0,
                    0 /* SIMCONNECT_PERIOD_ONCE */, 0, 0, 0, 0);
            }
        }
        catch { }
    }

    private void OnReceiveSimObjectData(dynamic sender, dynamic data)
    {
        try
        {
            if (data.dwRequestID == (uint)RequestId.Aircraft)
            {
                var d = (AircraftData)data.dwData[0];
                _state.LatitudeDeg = d.Latitude;
                _state.LongitudeDeg = d.Longitude;
                _state.AltitudeMslFt = d.AltitudeMsl;
                _state.AltitudeAglFt = d.AltitudeAgl;
                _state.HeadingDegTrue = d.HeadingTrue;
                _state.GroundSpeedKts = d.GroundSpeed;
                _state.OnGround = d.SimOnGround > 0.5;
                _state.Com1FreqMhz = d.Com1Freq;
                _state.WindSpeedKts = d.WindSpeed;
                _state.WindDirectionDeg = d.WindDirection;
                _state.SimTime = DateTime.UtcNow;
                _state.LastUpdated = DateTime.UtcNow;
                StateUpdated?.Invoke(_state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error processing SimConnect data: {Msg}", ex.Message);
        }
    }

    private void OnRecvQuit(dynamic sender, dynamic data)
    {
        _logger.LogInformation("SimConnect: sim quit");
        HandleDisconnect();
    }

    private void OnRecvException(dynamic sender, dynamic data)
    {
        _logger.LogDebug("SimConnect exception: {Code}", (object)data.dwException);

    }

    private void HandleDisconnect()
    {
        _connected = false;
        _state.IsConnected = false;
        _simConnect = null;
        Disconnected?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_USER_SIMCONNECT && _simConnect != null)
        {
            try { ((dynamic)_simConnect!).ReceiveMessage(); }
            catch (Exception ex)
            {
                _logger.LogDebug("SimConnect receive error: {Msg}", ex.Message);
                HandleDisconnect();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Fills SimState with plausible demo data when SimConnect is unavailable.
    /// This lets the rest of the app run and be tested without MSFS.
    /// </summary>
    private void SetSimulatedState()
    {
        _state.IsConnected = false;
        _state.NearestAirportIcao = null;
        StateUpdated?.Invoke(_state);
    }

    /// <summary>
    /// Updates nearest airport data. Called by the facility lookup background task.
    /// </summary>
    public void UpdateNearestAirport(
        string icao, string name, double distNm, double elevFt,
        double twr, double gnd, double atis,
        List<RunwayInfo> runways)
    {
        _state.NearestAirportIcao = icao;
        _state.NearestAirportName = name;
        _state.NearestAirportDistanceNm = distNm;
        _state.NearestAirportElevationFt = elevFt;
        _state.TowerFreqMhz = twr;
        _state.GroundFreqMhz = gnd;
        _state.AtisFreqMhz = atis;
        _state.Runways = runways;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer?.Dispose();
        _hwndSource?.RemoveHook(WndProc);
        try { _simConnect?.Dispose(); } catch { }
        _simConnect = null;
    }
}
