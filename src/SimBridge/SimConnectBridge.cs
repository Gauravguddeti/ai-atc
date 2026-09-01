using Microsoft.Extensions.Logging;
using MsfsAiAtc.Traffic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
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
    // Airport ICAO from SimConnect — replaces BGL parsing for airport identification
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
    public string DepartureAirportIcao;   // GPS FLIGHT PLAN DEPARTURE AIRPORT
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
    public string CurrentAirportIcao;     // ATC RUNWAY AIRPORT NAME
}

/// <summary>
/// Wraps the SimConnect connection. Designed to work with or without SimConnect DLL present
/// (graceful degradation when running without MSFS / without the SDK).
/// </summary>
public class SimConnectBridge : IDisposable
{
    private const uint WM_USER_SIMCONNECT = 0x0402;
    private const string APP_NAME = "MSFS_AI_ATC";

    private readonly ILogger<SimConnectBridge> _logger;
    private readonly SimState _state = new();
    private dynamic? _simConnect; // dynamic to allow compilation without SimConnect DLL
    private HwndSource? _hwndSource;
    private System.Threading.Timer? _retryTimer;
    private bool _connected;
    private bool _disposed;

    // Set to true once the managed SimConnect DLL has been successfully loaded into the AppDomain.
    // Prevents re-running the expensive file search on every 10-second retry.
    private static bool _dllLoaded = false;

    // Events
    public event Action<SimState>? StateUpdated;
    public event Action? Connected;
    public event Action? Disconnected;

    /// <summary>Wire in the TrafficTracker so SimConnect can feed it AI object data.</summary>
    public void SetTrafficTracker(TrafficTracker tracker) => _trafficTracker = tracker;

    // Expose current state (read-only snapshot)
    public SimState CurrentState => _state;

    // SimConnect definition IDs
    private enum DefId     { AircraftData = 1, TrafficData = 2 }
    private enum RequestId { Aircraft = 1, Facilities = 2, Traffic = 3 }
    private enum EventId   { OneSecond = 1, FiveSecond = 2 }
    private enum GroupId   { Standard = 1 }

    // Traffic tracking
    private TrafficTracker? _trafficTracker;

    // SIMCONNECT_SIMOBJECT_TYPE_AIRCRAFT = 1
    private const uint SIMOBJECT_TYPE_AIRCRAFT = 1;

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
            // Step 1: Load the managed SimConnect DLL (only searches disk once)
            if (!_dllLoaded)
                TryLoadSimConnectDll();

            // Step 2: Get the SimConnect type from the loaded assembly
            var simConnectType = Type.GetType(
                "Microsoft.FlightSimulator.SimConnect.SimConnect, Microsoft.FlightSimulator.SimConnect")
                ?? FindSimConnectType();

            if (simConnectType == null)
            {
                _logger.LogWarning("SimConnect DLL not found. " +
                    "Copy Microsoft.FlightSimulator.SimConnect.dll into the dist\\ folder, " +
                    "or install the MSFS SDK from Developer Mode → Help → SDK Installer.");
                SetSimulatedState();
                return;
            }

            // Activator.CreateInstance is STRICT about types. The SimConnect constructor expects:
            // (string szName, IntPtr hWnd, uint UserEventWin32, EventHandle hEventHandle, uint ConfigIndex)
            // Passing 'int' where 'uint' is expected causes "Constructor not found" exception.
            _simConnect = Activator.CreateInstance(simConnectType,
                APP_NAME, hwnd, WM_USER_SIMCONNECT, null, 0u);

            RegisterDataDefinitions();
            SubscribeToEvents();
            _connected = true;
            _state.IsConnected = true;
            _logger.LogInformation("SimConnect connected successfully");
            Connected?.Invoke();
        }
        catch (Exception ex)
        {
            // Log at Warning (not Debug) so the error appears in airatc.log
            // This tells us exactly WHY SimConnect isn't connecting
            _logger.LogWarning("SimConnect connection attempt failed: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Tries to find and load Microsoft.FlightSimulator.SimConnect.dll (the .NET managed wrapper).
    ///
    /// IMPORTANT: We ONLY look for "Microsoft.FlightSimulator.SimConnect.dll".
    /// "SimConnect.dll" is the native C++ DLL — trying to load it as a .NET assembly
    /// always fails with "Bad IL format". FitGirl repacks include SimConnect.dll (native)
    /// but NOT the managed wrapper. The managed wrapper must be found separately.
    ///
    /// Search order:
    ///   1. dist/ folder (app next to exe) — fastest, works if user copies it there
    ///   2. App root folder
    ///   3. Game root derived from MSFS_PACKAGES_PATH
    ///   4. Common MSFS install paths
    ///   5. Recursive search inside the MSFS Packages folder (slow but thorough)
    ///
    /// If found, copies the DLL to dist/ so future launches skip the search.
    /// </summary>
    private void TryLoadSimConnectDll()
    {
        const string ManagedDllName = "Microsoft.FlightSimulator.SimConnect.dll";
        var appRoot = MsfsAiAtc.App.AppRootDir;
        var distDir = AppContext.BaseDirectory;

        // ── Priority list (fast, direct path checks) ─────────────────────────
        var candidates = new List<string>
        {
            Path.Combine(distDir, ManagedDllName),          // next to exe
            Path.Combine(appRoot, ManagedDllName),          // app root
        };

        // Derive game root from MSFS_PACKAGES_PATH env var
        var packagesPath = Environment.GetEnvironmentVariable("MSFS_PACKAGES_PATH");
        string? gameRoot = null;
        if (!string.IsNullOrWhiteSpace(packagesPath))
        {
            gameRoot = Path.GetDirectoryName(packagesPath.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(gameRoot))
            {
                candidates.Add(Path.Combine(gameRoot, ManagedDllName));
                candidates.Add(Path.Combine(packagesPath, ManagedDllName));
            }
        }

        // Standard MSFS install locations
        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86, @"C:\", @"D:\", @"E:\" })
        {
            candidates.Add(Path.Combine(root, "Microsoft Games", "Microsoft Flight Simulator", ManagedDllName));
            candidates.Add(Path.Combine(root, "Microsoft Flight Simulator", ManagedDllName));
            candidates.Add(Path.Combine(root, "MSFS", ManagedDllName));
            candidates.Add(Path.Combine(root, "MSFS 2020", ManagedDllName));
        }

        // Try each candidate path first (fast)
        foreach (var path in candidates.Distinct())
        {
            if (TryLoadManagedSimConnect(path, distDir)) return;
        }

        // ── Slow fallback: recursive search inside MSFS Packages ─────────────
        // The managed DLL may be bundled deep inside Official\OneStore\fs-base\...
        if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
        {
            _logger.LogInformation("Fast search failed — doing recursive search in Packages folder...");
            try
            {
                var found = Directory.EnumerateFiles(packagesPath, ManagedDllName,
                                SearchOption.AllDirectories)
                            .FirstOrDefault();
                if (found != null)
                {
                    if (TryLoadManagedSimConnect(found, distDir)) return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Recursive packages search failed: {Err}", ex.Message);
            }
        }

        _logger.LogWarning(
            "Microsoft.FlightSimulator.SimConnect.dll not found anywhere. " +
            "To fix: In MSFS, turn on Developer Mode → Help → SDK Installer → run it. " +
            "Then restart AI ATC. Alternatively, copy Microsoft.FlightSimulator.SimConnect.dll " +
            "manually into the ai-atc-main\\dist\\ folder.");
    }

    private bool TryLoadManagedSimConnect(string path, string distDir)
    {
        if (!File.Exists(path))
        {
            _logger.LogDebug("SimConnect managed DLL not at: {Path}", path);
            return false;
        }

        // Before trying to load the managed DLL, copy the NATIVE SimConnect.dll from the
        // same source folder into distDir. The managed DLL P/Invokes into the native one —
        // without the native DLL in the same folder as our exe, the managed load will fail
        // with "The specified module could not be found" (missing dependency).
        var sourceDir  = Path.GetDirectoryName(path)!;
        var nativeSrc  = Path.Combine(sourceDir, "SimConnect.dll");
        var nativeDest = Path.Combine(distDir, "SimConnect.dll");
        if (File.Exists(nativeSrc) && !File.Exists(nativeDest))
        {
            try
            {
                File.Copy(nativeSrc, nativeDest);
                _logger.LogInformation("Copied native SimConnect.dll to dist/ (required dependency)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not copy native SimConnect.dll: {Err}", ex.Message);
            }
        }

        try
        {
            System.Reflection.Assembly.LoadFrom(path);
            _logger.LogInformation("Loaded managed SimConnect from: {Path}", path);

            // Copy managed DLL to dist/ so future launches find it instantly without searching
            var dest = Path.Combine(distDir, "Microsoft.FlightSimulator.SimConnect.dll");
            if (!string.Equals(path, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                try { File.Copy(path, dest); _logger.LogInformation("Copied managed SimConnect to dist/"); }
                catch { /* non-fatal */ }
            }

            _dllLoaded = true; // Don't search again this session
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Found {Path} but failed to load: {Err}", path, ex.Message);
            return false;
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
            var sc = _simConnect;
            var asm = ((object)sc).GetType().Assembly;
            var dataTypeEnum = asm.GetType("Microsoft.FlightSimulator.SimConnect.SIMCONNECT_DATATYPE")!;

            object float64 = Enum.Parse(dataTypeEnum, "FLOAT64");
            object string8 = Enum.Parse(dataTypeEnum, "STRING8");
            object string32 = Enum.Parse(dataTypeEnum, "STRING32");
            object string64 = Enum.Parse(dataTypeEnum, "STRING64");
            uint unused = unchecked((uint)-1);

            void AddDef(string name, string units, object type) =>
                sc.AddToDataDefinition(DefId.AircraftData, name, units, type, 0.0f, unused);

            AddDef("PLANE LATITUDE",                   "degrees", float64);
            AddDef("PLANE LONGITUDE",                  "degrees", float64);
            AddDef("PLANE ALTITUDE",                   "feet",    float64);
            AddDef("PLANE ALT ABOVE GROUND",           "feet",    float64);
            AddDef("PLANE HEADING DEGREES TRUE",       "degrees", float64);
            AddDef("GROUND VELOCITY",                  "knots",   float64);
            AddDef("SIM ON GROUND",                    "bool",    float64);
            AddDef("COM ACTIVE FREQUENCY:1",           "MHz",     float64);
            AddDef("AMBIENT WIND VELOCITY",            "knots",   float64);
            AddDef("AMBIENT WIND DIRECTION",           "degrees", float64);
            // Airport ICAO — this is the key field that replaces BGL parsing!
            // "GPS FLIGHT PLAN DEPARTURE AIRPORT" gives the departure airport ICAO as a string.
            AddDef("GPS FLIGHT PLAN DEPARTURE AIRPORT", string.Empty, string8);
            AddDef("ATC RUNWAY AIRPORT NAME",           string.Empty, string8); // current airport

            sc.RegisterDataDefineStruct<AircraftData>(DefId.AircraftData);

            // Traffic definition: position + callsign for each AI aircraft
            void AddTrafficDef(string name, string units, object type) =>
                sc.AddToDataDefinition(DefId.TrafficData, name, units, type, 0.0f, unused);

            AddTrafficDef("PLANE LATITUDE",               "degrees",  float64);
            AddTrafficDef("PLANE LONGITUDE",              "degrees",  float64);
            AddTrafficDef("PLANE ALTITUDE",               "feet",     float64);
            AddTrafficDef("PLANE HEADING DEGREES TRUE",   "degrees",  float64);
            AddTrafficDef("GROUND VELOCITY",              "knots",    float64);
            AddTrafficDef("SIM ON GROUND",                "bool",     float64);
            AddTrafficDef("TITLE",             string.Empty, string64);
            AddTrafficDef("ATC ID",             string.Empty, string32);
            AddTrafficDef("ATC AIRLINE",        string.Empty, string32);
            AddTrafficDef("ATC FLIGHT NUMBER",  string.Empty, string8);

            sc.RegisterDataDefineStruct<TrafficObjectData>(DefId.TrafficData);

            // Request own-ship data on 1-second intervals
            sc.SubscribeToSystemEvent(EventId.OneSecond, "1sec");
            // Traffic scan on 5-second intervals (heavier call)
            sc.SubscribeToSystemEvent(EventId.FiveSecond, "5sec");
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
            SubscribeDynamic("OnRecvSimobjectData",       nameof(OnReceiveSimObjectData));
            SubscribeDynamic("OnRecvSimobjectDataBytype", nameof(OnReceiveSimObjectDataBytype));
            SubscribeDynamic("OnRecvEvent",               nameof(OnRecvEvent));
            SubscribeDynamic("OnRecvQuit",                nameof(OnRecvQuit));
            SubscribeDynamic("OnRecvException",           nameof(OnRecvException));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to subscribe to SimConnect events: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Subscribes to a SimConnect event dynamically using Expression trees.
    /// This prevents "Cannot implicitly convert type System.Action" errors because it builds
    /// a delegate of the exact type SimConnect expects at runtime.
    /// </summary>
    private void SubscribeDynamic(string eventName, string methodName)
    {
        var scType = ((object)_simConnect!).GetType();
        var ev = scType.GetEvent(eventName);
        if (ev == null) return;

        var delegateType = ev.EventHandlerType!;
        var invokeParams = delegateType.GetMethod("Invoke")!.GetParameters();

        var p1 = System.Linq.Expressions.Expression.Parameter(invokeParams[0].ParameterType, "sender");
        var p2 = System.Linq.Expressions.Expression.Parameter(invokeParams[1].ParameterType, "data");

        var methodInfo = this.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Call our internal method, casting the strongly-typed parameters to 'object'
        var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(this), methodInfo,
            System.Linq.Expressions.Expression.Convert(p1, typeof(object)),
            System.Linq.Expressions.Expression.Convert(p2, typeof(object)));

        var lambda = System.Linq.Expressions.Expression.Lambda(delegateType, call, p1, p2);
        ev.AddEventHandler(_simConnect, lambda.Compile());
    }

    private void OnRecvEvent(object sender, object dataObj)
    {
        dynamic data = dataObj;

        try
        {
            uint evId = (uint)data.uEventID;

            if (evId == (uint)EventId.OneSecond)
            {
                // Request own-ship aircraft data
                _simConnect?.RequestDataOnSimObject(
                    RequestId.Aircraft, DefId.AircraftData, 0,
                    0 /* SIMCONNECT_PERIOD_ONCE */, 0, 0, 0, 0);
            }
            else if (evId == (uint)EventId.FiveSecond)
            {
                // Request all nearby AI aircraft (radius = 40 km = 40000 m)
                // SIMCONNECT_SIMOBJECT_TYPE_AIRCRAFT = 1
                _simConnect?.RequestDataOnSimObjectType(
                    RequestId.Traffic, DefId.TrafficData,
                    40000u /* radius metres */, SIMOBJECT_TYPE_AIRCRAFT);
            }
        }
        catch { }
    }

    private void OnReceiveSimObjectData(object sender, object dataObj)
    {
        dynamic data = dataObj;
        try
        {
            uint reqId = (uint)data.dwRequestID;

            if (reqId == (uint)RequestId.Aircraft)
            {
                var d = (AircraftData)data.dwData[0];
                _state.LatitudeDeg    = d.Latitude;
                _state.LongitudeDeg   = d.Longitude;
                _state.AltitudeMslFt  = d.AltitudeMsl;
                _state.AltitudeAglFt  = d.AltitudeAgl;
                _state.HeadingDegTrue = d.HeadingTrue;
                _state.GroundSpeedKts = d.GroundSpeed;
                _state.OnGround       = d.SimOnGround > 0.5;
                _state.Com1FreqMhz    = d.Com1Freq;
                _state.WindSpeedKts   = d.WindSpeed;
                _state.WindDirectionDeg = d.WindDirection;
                _state.SimTime        = DateTime.UtcNow;
                _state.LastUpdated    = DateTime.UtcNow;

                // Use SimConnect's own airport ICAO — works without BGL parsing.
                // CurrentAirportIcao (ATC RUNWAY AIRPORT NAME) is non-empty when on the ground at an airport.
                // DepartureAirportIcao (GPS FLIGHT PLAN DEPARTURE AIRPORT) is set once a flight plan is loaded.
                var currentIcao = d.CurrentAirportIcao?.Trim();
                var departIcao  = d.DepartureAirportIcao?.Trim();
                var bestIcao    = (!string.IsNullOrWhiteSpace(currentIcao)) ? currentIcao : departIcao;

                if (!string.IsNullOrWhiteSpace(bestIcao) && bestIcao != _state.NearestAirportIcao)
                {
                    _state.NearestAirportIcao = bestIcao;
                    _logger.LogInformation("Airport from SimConnect: {Icao} (current={C} depart={D})",
                        bestIcao, currentIcao, departIcao);
                }

                StateUpdated?.Invoke(_state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error processing SimConnect data: {Msg}", ex.Message);
        }
    }

    // Traffic is returned by RequestDataOnSimObjectType — different callback
    private readonly List<TrafficObjectData> _trafficBuffer = new();

    private void OnReceiveSimObjectDataBytype(object sender, object dataObj)
    {
        dynamic data = dataObj;
        try
        {
            uint reqId     = (uint)data.dwRequestID;
            uint entryNum  = (uint)data.dwentrynumber;
            uint outOf     = (uint)data.dwoutof;

            if (reqId != (uint)RequestId.Traffic) return;

            if (entryNum == 1) _trafficBuffer.Clear(); // first entry = start of batch

            var t = (TrafficObjectData)data.dwData[0];
            _trafficBuffer.Add(t);

            if (entryNum == outOf && _trafficTracker != null)
            {
                // Full batch received — update tracker
                _trafficTracker.UpdateFromSimConnect(
                    _trafficBuffer,
                    _state.LatitudeDeg, _state.LongitudeDeg, _state.AltitudeMslFt);

                _state.TrafficContext = _trafficTracker.GetContextSummary();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error processing traffic data: {Msg}", ex.Message);
        }
    }

    private void OnRecvQuit(object sender, object data)
    {
        _logger.LogInformation("SimConnect: sim quit");
        HandleDisconnect();
    }

    private void OnRecvException(object sender, object dataObj)
    {
        dynamic data = dataObj;
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
