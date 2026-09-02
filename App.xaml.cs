using Microsoft.Extensions.Logging;
using MsfsAiAtc.Airspace;
using MsfsAiAtc.Audio;
using MsfsAiAtc.AtcBrain;
using MsfsAiAtc.Config;
using MsfsAiAtc.Handoff;
using MsfsAiAtc.Overlay;
using MsfsAiAtc.SimBridge;
using MsfsAiAtc.Speech;
using MsfsAiAtc.Traffic;
using MsfsAiAtc.Triggers;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;

namespace MsfsAiAtc;

/// <summary>
/// Application entry point and service orchestrator.
/// Wires together all components and manages their lifecycle.
/// </summary>
public partial class App : Application
{
    // Services
    private AppConfig _config = null!;
    private ILoggerFactory _loggerFactory = null!;

    private SimConnectBridge _simBridge = null!;
    private VoicePipeline _voicePipeline = null!;
    private GlobalHotkeyHook _hotkeyHook = null!;
    private AudioPlayer _audioPlayer = null!;
    private GroqWhisperClient _whisperClient = null!;
    private PiperTtsWrapper _piperTts = null!;
    private AtcBrainService _atcBrain = null!;
    private HandoffStateMachine _handoff = null!;
    private TriggerScheduler _triggers = null!;
    private OverlayWindow _overlay = null!;

    // Phase 3 & 4
    private AirspaceLookup _airspaceLookup = null!;
    private TrafficTracker _trafficTracker = null!;
    private OurAirportsDb _ourAirportsDb = null!;

    private HttpClient _httpClient = null!;
    private CancellationTokenSource _appCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hook unhandled exceptions FIRST so any crash gets logged to disk
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 1. Logging — write to both console and a log file in the app root
        var rootDir = AppRootDir;
        var logFile = Path.Combine(rootDir, "airatc.log");
        _loggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .AddProvider(new FileLoggerProvider(logFile))
            .SetMinimumLevel(LogLevel.Debug));

        var logger = _loggerFactory.CreateLogger<App>();
        logger.LogInformation("MSFS AI ATC starting up");

        // 2. Config
        _config = ConfigLoader.Load(_loggerFactory.CreateLogger<object>() as ILogger);

        // 3. HTTP client (shared, long-lived)
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // 4. Create overlay first (needs to show before blocking waits)
        _overlay = new OverlayWindow();
        _overlay.Show();
        _overlay.SetPttKeyHint(_config.PushToTalkKey.ToString());
        _overlay.AddSystemMessage("MSFS AI ATC v0.1 starting...");

        // 5. Services
        _whisperClient = new GroqWhisperClient(
            _loggerFactory.CreateLogger<GroqWhisperClient>(), _httpClient, 
            new[] { _config.GroqApiKey, _config.GroqApiKey2 });

        _piperTts = new PiperTtsWrapper(
            _loggerFactory.CreateLogger<PiperTtsWrapper>(), _config.PiperExePath, _config.PiperModelPath);

        _atcBrain = new AtcBrainService(
            _loggerFactory.CreateLogger<AtcBrainService>(), _httpClient,
            new[] { _config.GroqApiKey, _config.GroqApiKey2 });

        _audioPlayer = new AudioPlayer(
            _loggerFactory.CreateLogger<AudioPlayer>(), _config.OutputDeviceId);

        _handoff = new HandoffStateMachine();
        _handoff.PhaseChanged += OnPhaseChanged;

        _triggers = new TriggerScheduler(_loggerFactory.CreateLogger<TriggerScheduler>());
        _triggers.TriggerFired += OnTriggerFired;
        _triggers.Start();

        _voicePipeline = new VoicePipeline(
            _loggerFactory.CreateLogger<VoicePipeline>(), _config.MicDeviceId);
        _voicePipeline.StateChanged += OnVoiceStateChanged;
        _voicePipeline.AudioCaptured += OnAudioCaptured;

        _hotkeyHook = new GlobalHotkeyHook(
            _loggerFactory.CreateLogger<GlobalHotkeyHook>(), _config.PushToTalkKey);
        _hotkeyHook.PttKeyDown += _voicePipeline.OnPttKeyDown;
        _hotkeyHook.PttKeyUp += _voicePipeline.OnPttKeyUp;

        // 6. SimConnect — needs HWND, wait for overlay window to initialize
        _simBridge = new SimConnectBridge(_loggerFactory.CreateLogger<SimConnectBridge>());
        _simBridge.StateUpdated += OnSimStateUpdated;
        _simBridge.Connected += () =>
        {
            _overlay.AddSystemMessage("SimConnect: Connected to MSFS ✓");
            // Immediately update the overlay header (don't wait for first data packet)
            _overlay.UpdateSimState(_simBridge.CurrentState);
        };
        _simBridge.Disconnected += () => _overlay.AddSystemMessage("SimConnect: Disconnected — retrying...");

        // Initialize after overlay is rendered (to get HWND)
        _overlay.ContentRendered += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_overlay).Handle;
            _simBridge.Initialize(hwnd);
            _hotkeyHook.Initialize(hwnd);
        };

        _overlay.AddSystemMessage($"PTT key: [{_config.PushToTalkKey}]  |  Piper: {(_piperTts.IsAvailable ? "Ready" : "Not found — install via setup")}");

        // ── Phase 3 & 4: Airport database, flight plan, traffic ───────────────
        InitialisePhase3And4();

        // Set main window
        MainWindow = _overlay;
    }

    /// <summary>
    /// Resolves the "app root" — the folder containing .env, piper/, etc.
    /// 
    /// Resolution order:
    ///   1. If exe is inside dist/, root is the parent (normal installed run).
    ///   2. Walk up from the exe dir until we find a folder containing piper/ or .env
    ///      (handles 'dotnet run' which puts the exe deep in bin/Release/.../win-x64/).
    ///   3. Fall back to the exe dir itself.
    /// </summary>
    public static string AppRootDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            // Case 1: running from dist\ — parent is the real root
            if (Path.GetFileName(exeDir).Equals("dist", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(exeDir) ?? exeDir;

            // Case 2: walk up until we find piper\ or .env (dev / dotnet run)
            var dir = exeDir;
            for (int i = 0; i < 8; i++) // max 8 levels up
            {
                if (Directory.Exists(Path.Combine(dir, "piper")) ||
                    File.Exists(Path.Combine(dir, ".env"))        ||
                    Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }

            // Case 3: fallback
            return exeDir;
        }
    }


    // ─── Crash handlers ───────────────────────────────────────────────────────

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.ExceptionObject?.ToString() ?? "Unknown error");
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception.ToString());
        e.Handled = true; // prevent instant crash so the log is visible
        MessageBox.Show(
            $"MSFS AI ATC crashed.\n\nError: {e.Exception.Message}\n\nFull details saved to:\nairatc.log\n\nSend that file to Gaurav.",
            "MSFS AI ATC — Crash",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private static void WriteCrashLog(string content)
    {
        try
        {
            var logPath = Path.Combine(AppRootDir, "airatc.log");
            var entry = $"[CRASH {DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{content}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { /* can't log the logger failing */ }
    }

    // ─── Phase 3 & 4 initialisation ───────────────────────────────────────────

    private void InitialisePhase3And4()
    {
        var logger = _loggerFactory.CreateLogger<App>();

        // Traffic tracker (Phase 4)
        _trafficTracker = new TrafficTracker(_loggerFactory.CreateLogger<TrafficTracker>());
        _simBridge.SetTrafficTracker(_trafficTracker);

        // ── OurAirports database (replaces BGL parser for MSFS 2020) ──────────
        // MSFS 2020 Official BGLs are in Asobo's proprietary format — our FSX
        // parser gets 0 airports from 6000+ files every time. OurAirports gives
        // us 80k+ worldwide airports loaded in <2 seconds.
        var appDataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var airportsCsv = Path.Combine(appDataDir, "airports.csv");
        var runwaysCsv  = Path.Combine(appDataDir, "runways.csv");

        _ourAirportsDb = new OurAirportsDb(_loggerFactory.CreateLogger<OurAirportsDb>());

        if (File.Exists(airportsCsv))
        {
            _overlay.AddSystemMessage("Loading airport database...");
            Task.Run(() =>
            {
                _ourAirportsDb.Load(airportsCsv, runwaysCsv);
                Application.Current?.Dispatcher.Invoke(() =>
                    _overlay.AddSystemMessage(
                        $"✓ Airport DB ready — {_ourAirportsDb.AirportCount:N0} airports"));
            });
        }
        else
        {
            _overlay.AddSystemMessage(
                "⚠ Airport database not found. " +
                "Run 'SETUP (Run This First).bat' to download it.");
        }

        // Discover MSFS installation
        var pathFinder = new MsfsPathFinder(_loggerFactory.CreateLogger<MsfsPathFinder>());
        var paths = pathFinder.Discover();

        if (paths == null)
        {
            _overlay.AddSystemMessage("⚠ MSFS install not found — flight plan unavailable");
            _airspaceLookup = new AirspaceLookup(
                _loggerFactory.CreateLogger<AirspaceLookup>(), null, null, _ourAirportsDb);
            logger.LogInformation("Phase 3 & 4 services initialised");
            return;
        }

        // Flight plan reader (Phase 3)
        var flightPlanReader = new FlightPlanReader(
            _loggerFactory.CreateLogger<FlightPlanReader>(), paths.LocalStateFolder);

        _airspaceLookup = new AirspaceLookup(
            _loggerFactory.CreateLogger<AirspaceLookup>(),
            null,            // BGL cache not used (MSFS 2020 format not parseable)
            flightPlanReader,
            _ourAirportsDb);

        logger.LogInformation("Phase 3 & 4 services initialised");
    }

    // ─── Event handlers ───────────────────────────────────────────────────────

    private void OnSimStateUpdated(SimState state)
    {
        // Update airspace context (flight plan + BGL layout) on every tick
        // This is cheap — data is already in memory from the cache
        state.AirspaceContext = _airspaceLookup.BuildContextString(state);

        _overlay.UpdateSimState(state);
        _handoff.Update(state);
        _triggers.UpdateState(state);
    }

    private void OnVoiceStateChanged(VoiceState state)
    {
        _overlay.UpdateVoiceState(state);
    }

    private async Task OnAudioCaptured(byte[] wavBytes)
    {
        var ct = _appCts.Token;

        try
        {
            // Step 1: Transcribe
            var transcript = await _whisperClient.TranscribeAsync(wavBytes, ct);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                _voicePipeline.SetIdle();
                return;
            }

            _overlay.AddPilotMessage(transcript);

            // Step 2: LLM
            var simState = _simBridge.CurrentState;
            var role = _handoff.ControllerRoleLabel;
            var atcResponse = await _atcBrain.GetResponseAsync(transcript, simState, role, ct);

            if (string.IsNullOrWhiteSpace(atcResponse))
            {
                _voicePipeline.SetIdle();
                return;
            }

            // Handle rate-limit sentinel (all keys exhausted)
            if (atcResponse == "<<RATE_LIMIT>>")
            {
                _overlay.AddSystemMessage("⚠ Rate limit hit on all keys — wait ~60s and try again");
                _voicePipeline.SetIdle();
                return;
            }

            _overlay.AddAtcMessage(atcResponse, role.Split('/')[0]);

            // Step 3: TTS + playback
            await SpeakAtcResponse(atcResponse, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var logger = _loggerFactory.CreateLogger<App>();
            logger.LogError(ex, "Pipeline error");
            _overlay.AddSystemMessage($"Error: {ex.Message}");
            _voicePipeline.SetIdle();
        }
    }

    private async Task SpeakAtcResponse(string text, CancellationToken ct)
    {
        _voicePipeline.SetSpeaking();
        try
        {
            var wavBytes = await _piperTts.SynthesizeAsync(text, ct);
            if (wavBytes != null)
            {
                await _audioPlayer.PlayWithRadioFilterAsync(wavBytes, ct);
            }
            else
            {
                // Piper binary or model not found at expected path — silent fallback
                _loggerFactory.CreateLogger<App>().LogWarning("TTS skipped — Piper not available at configured path");
                await Task.Delay(800, ct); // brief pause to simulate speaking time
            }
        }
        finally
        {
            _voicePipeline.SetIdle();
        }
    }

    private void OnPhaseChanged(ControllerPhase from, ControllerPhase to)
    {
        var state = _simBridge.CurrentState;
        double freq = to switch
        {
            ControllerPhase.Ground => state.GroundFreqMhz,
            ControllerPhase.Tower => state.TowerFreqMhz,
            ControllerPhase.ClearanceDelivery => state.GroundFreqMhz,
            _ => 0
        };
        _overlay.UpdateControllerPhase(to, freq);
        _overlay.AddSystemMessage($"Controller: {from} → {to}");
    }

    private async Task OnTriggerFired(string triggerLabel)
    {
        var ct = _appCts.Token;

        // Only speak if currently IDLE — don't interrupt
        if (_voicePipeline.State != VoiceState.Idle) return;

        var simState = _simBridge.CurrentState;
        var role = _handoff.ControllerRoleLabel;

        var atcResponse = await _atcBrain.GetUnpromptedTransmissionAsync(triggerLabel, simState, role, ct);
        if (!string.IsNullOrWhiteSpace(atcResponse) && atcResponse != "<<RATE_LIMIT>>")
        {
            _overlay.AddAtcMessage(atcResponse, role.Split('/')[0]);
            await SpeakAtcResponse(atcResponse, ct);
        }
        else if (atcResponse == "<<RATE_LIMIT>>")
        {
            _overlay.AddSystemMessage("⚠ Rate limit — trigger skipped, will retry next event");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appCts.Cancel();
        _hotkeyHook?.Dispose();
        _voicePipeline?.Dispose();
        _simBridge?.Dispose();
        _triggers?.Dispose();
        _audioPlayer?.Dispose();
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
