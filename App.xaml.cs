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
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MsfsAiAtc;

/// <summary>
/// Application entry point and service orchestrator.
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

    // PTT click WAV — generated once at startup
    private byte[]? _pttClickWav;

    // Flight transcript log — written to disk on exit
    private readonly StringBuilder _flightLog = new();
    private bool _atisGenerated = false;

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
        _voicePipeline.StateChanged   += OnVoiceStateChanged;
        _voicePipeline.AudioCaptured  += OnAudioCaptured;

        // PTT click sound — generated once at startup (no external file needed)
        _pttClickWav = PiperTtsWrapper.GenerateClickWav();
        _voicePipeline.PttPressed  += () => _ = _audioPlayer.PlayRawAsync(_pttClickWav!, CancellationToken.None);
        _voicePipeline.PttReleased += () => _ = _audioPlayer.PlayRawAsync(_pttClickWav!, CancellationToken.None);

        _hotkeyHook = new GlobalHotkeyHook(
            _loggerFactory.CreateLogger<GlobalHotkeyHook>(), _config.PushToTalkKey);
        _hotkeyHook.PttKeyDown += _voicePipeline.OnPttKeyDown;
        _hotkeyHook.PttKeyUp += _voicePipeline.OnPttKeyUp;

        // 6. SimConnect — needs HWND, wait for overlay window to initialize
        _simBridge = new SimConnectBridge(_loggerFactory.CreateLogger<SimConnectBridge>());
        _simBridge.StateUpdated += OnSimStateUpdated;
        _simBridge.Connected += () =>
        {
            _overlay.UpdateSimState(_simBridge.CurrentState);
            if (!_atisGenerated)
            {
                _atisGenerated = true;
                // Wait for SimConnect Facilities API to populate airport data (takes 3-10s).
                // Poll every second for up to 15s, then fire with whatever we have.
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 15; i++)
                    {
                        await Task.Delay(1000);
                        var s = _simBridge.CurrentState;
                        if (!string.IsNullOrWhiteSpace(s.NearestAirportIcao)) break;
                    }
                    GenerateAtis(_simBridge.CurrentState);
                });
            }
        };
        _simBridge.Disconnected += () =>
            _loggerFactory.CreateLogger<App>().LogInformation("SimConnect disconnected — retrying...");

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

            // Case 2: check CWD first — dotnet run is executed FROM the project folder
            var cwd = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Combine(cwd, "piper")) ||
                File.Exists(Path.Combine(cwd, ".env")))
                return cwd;

            // Case 3: walk up from exe dir until we find piper\ or .env
            var dir = exeDir;
            for (int i = 0; i < 8; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "piper")) ||
                    File.Exists(Path.Combine(dir, ".env"))        ||
                    Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }

            // Case 4: fallback
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
        var appDataDir = Path.Combine(AppRootDir, "data");   // ← fixed: AppRootDir not AppContext.BaseDirectory
        var airportsCsv = Path.Combine(appDataDir, "airports.csv");
        var runwaysCsv  = Path.Combine(appDataDir, "runways.csv");

        _ourAirportsDb = new OurAirportsDb(_loggerFactory.CreateLogger<OurAirportsDb>());

        if (File.Exists(airportsCsv))
        {
            Task.Run(() =>
            {
                _ourAirportsDb.Load(airportsCsv, runwaysCsv);
                logger.LogInformation("Airport DB ready — {Count:N0} airports", _ourAirportsDb.AirportCount);
            });
        }
        else
        {
            logger.LogWarning("Airport DB not found at {Path} — run SETUP.bat", airportsCsv);
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

    // Throttle the airport DB lookup — expensive-ish, run every 5 ticks (5 seconds)
    private int _simUpdateTick = 0;

    private void OnSimStateUpdated(SimState state)
    {
        // Update airspace context (flight plan + BGL layout) on every tick
        state.AirspaceContext = _airspaceLookup.BuildContextString(state);

        // ── Nearest airport lookup (every 5 seconds) ────────────────────────
        // OurAirportsDb.NearestTo() is the ONLY code that populates NearestAirportIcao.
        // Without this, ATIS always says "unknown" and the hallucination guard fires.
        if (++_simUpdateTick % 5 == 0 &&
            state.IsConnected &&
            state.LatitudeDeg != 0 &&
            state.LongitudeDeg != 0)
        {
            if (_ourAirportsDb.IsLoaded)
            {
                var nearest = _ourAirportsDb.NearestTo(state.LatitudeDeg, state.LongitudeDeg, 25);
                if (nearest != null)
                {
                    state.NearestAirportIcao        = nearest.Icao;
                    state.NearestAirportName        = nearest.Name;
                    state.NearestAirportElevationFt = nearest.ElevFt;

                    // Populate runway list from OurAirports data
                    state.Runways = nearest.Runways
                        .Select(r => new MsfsAiAtc.SimBridge.RunwayInfo
                        {
                            Designation = r.Designation,
                            HeadingDeg  = r.HeadingDeg,
                            LengthFt    = r.LengthFt
                        }).ToList();

                    // Pick active runway: choose the one most into-wind
                    if (state.Runways.Count > 0 && state.WindSpeedKts > 2)
                    {
                        var best = state.Runways
                            .OrderBy(r => Math.Abs(
                                ((r.HeadingDeg - state.WindDirectionDeg + 540) % 360) - 180))
                            .First();
                        state.ActiveRunway = best.Designation;
                    }
                    else if (state.Runways.Count > 0)
                    {
                        state.ActiveRunway ??= state.Runways[0].Designation;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(state.GpsDestinationIcao))
            {
                // Airport DB not installed — use GPS flight plan ICAO as best-effort
                state.NearestAirportIcao ??= state.GpsDestinationIcao;
            }

            // Auto-set callsign from SimConnect ATC SimVars (more reliable than Whisper)
            // Only if Whisper hasn't already detected a callsign
            if (!_atcBrain.FlightCtx.HasCallsign &&
                !string.IsNullOrWhiteSpace(state.AircraftId))
            {
                var simCallsign = !string.IsNullOrWhiteSpace(state.AircraftAirline) &&
                                  !string.IsNullOrWhiteSpace(state.AircraftFlightNumber)
                    ? $"{state.AircraftAirline} {state.AircraftFlightNumber}"
                    : state.AircraftId;
                _atcBrain.FlightCtx.Callsign = simCallsign;
                _loggerFactory.CreateLogger<App>().LogInformation(
                    "Callsign from SimConnect: {C}", simCallsign);
            }
        }

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

            if (IsWhisperHallucination(transcript))
            {
                _loggerFactory.CreateLogger<App>().LogWarning("Whisper hallucination: {T}", transcript);
                _voicePipeline.SetIdle();
                return;
            }

            _overlay.AddPilotMessage(transcript);
            AppendFlightLog("YOU", transcript);
            _triggers.NotifyPilotSpoke();   // unlock auto-triggers after first real radio call

            // Callsign detection
            _atcBrain.TryExtractCallsign(transcript);
            if (_atcBrain.FlightCtx.HasCallsign)
                _overlay.SetCallsign(_atcBrain.FlightCtx.Callsign);

            // Show typing indicator while LLM works
            _overlay.ShowTypingIndicator(true);

            // Step 2: LLM
            var simState = _simBridge.CurrentState;
            var role = _handoff.ControllerRoleLabel;
            var atcResponse = await _atcBrain.GetResponseAsync(transcript, simState, role, ct);

            _overlay.ShowTypingIndicator(false);

            if (string.IsNullOrWhiteSpace(atcResponse))
            {
                _loggerFactory.CreateLogger<App>().LogWarning("LLM empty response for: {T}", transcript);
                _voicePipeline.SetIdle();
                return;
            }

            // Extract squawk and altitude from response
            var squawk = _atcBrain.TryExtractSquawk(atcResponse);
            if (squawk != null)
            {
                _overlay.SetSquawk(squawk);
                _atcBrain.FlightCtx.SquawkCode = squawk;
            }
            var assignedAlt = _atcBrain.TryExtractAltitude(atcResponse);
            if (assignedAlt > 0)
                _triggers.SetAssignedAltitude(assignedAlt);

            _overlay.AddAtcMessage(atcResponse, role.Split('/')[0]);
            AppendFlightLog(role.Split('/')[0].ToUpper(), atcResponse);

            // Step 3: 0.6s pause (natural controller hesitation) then TTS
            await Task.Delay(600, ct);
            await SpeakAtcResponse(atcResponse, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _overlay.ShowTypingIndicator(false);
            _loggerFactory.CreateLogger<App>().LogError(ex, "Pipeline error");
            _voicePipeline.SetIdle();
        }
    }

    private static bool IsWhisperHallucination(string transcript)
    {
        var t = transcript.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ',');
        var hallucinations = new[]
        {
            "thank you for watching",
            "thanks for watching",
            "please subscribe",
            "like and subscribe",
            "don't forget to subscribe",
            "see you next time",
            "see you in the next video",
            "subtitles by",
            "transcribed by",
            "you",
            "check",       // single-word Whisper artifact on short audio
            "okay",        // Whisper hallucinates this on breath sounds
            "hmm",
            "uh",
            "um",
        };
        foreach (var h in hallucinations)
            if (t == h || t.StartsWith(h + " ")) return true;
        if (transcript.Trim().Length < 4) return true;
        return false;
    }

    private async Task SpeakAtcResponse(string text, CancellationToken ct)
    {
        _voicePipeline.SetSpeaking();
        try
        {
            var ttsText  = ToAviationSpeech(text);
            var wavBytes = await _piperTts.SynthesizeAsync(ttsText, ct);
            if (wavBytes != null)
                await _audioPlayer.PlayWithRadioFilterAsync(wavBytes, ct);
            else
                await Task.Delay(800, ct);
        }
        finally
        {
            _voicePipeline.SetIdle();
        }
    }

    // ─── ATIS generation ──────────────────────────────────────────────────────
    /// <summary>
    /// Generates a synthetic ATIS from live SimConnect data and shows it as an
    /// ATC message. Pilots should say "with information Alpha" on first contact.
    /// </summary>
    private void GenerateAtis(SimState state)
    {
        try
        {
            // Prefer real ICAO from DB lookup; fall back to GPS dest; then "unknown"
            var icao = state.NearestAirportIcao
                    ?? state.GpsDestinationIcao
                    ?? "unknown";

            // Pick best runway: most into wind, or first runway in DB
            var rwy = state.ActiveRunway
                   ?? (state.Runways.Count > 0 ? state.Runways[0].Designation : null)
                   ?? "unknown";

            var wind = state.WindSpeedKts < 1
                ? "calm"
                : $"{state.WindDirectionDeg:F0} at {state.WindSpeedKts:F0} knots";

            // Use real QNH from SimConnect (KOHLSMAN SETTING MB SimVar)
            var qnh = state.QnhHpa > 900 ? state.QnhHpa : 1013;

            var info = "Alpha";

            var atis = $"{icao} Information {info}. Wind {wind}. Runway {rwy} in use. " +
                       $"QNH {qnh}. Report information {info} on initial contact.";

            Application.Current?.Dispatcher.Invoke(() =>
                _overlay.AddAtcMessage(atis, "ATIS"));
            AppendFlightLog("ATIS", atis);
            _loggerFactory.CreateLogger<App>().LogInformation("ATIS generated: {A}", atis);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<App>().LogWarning(ex, "ATIS generation failed");
        }
    }

    // ─── Flight log ───────────────────────────────────────────────────────────
    private void AppendFlightLog(string speaker, string text)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _flightLog.AppendLine($"[{ts}] {speaker.ToUpperInvariant()}: {text}");
    }

    private void SaveFlightLog()
    {
        try
        {
            if (_flightLog.Length == 0) return;
            var path = Path.Combine(AppRootDir, $"flight_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, _flightLog.ToString(), System.Text.Encoding.UTF8);
            _loggerFactory?.CreateLogger<App>().LogInformation("Flight log saved: {P}", path);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Converts ATC LLM output to Piper-friendly ICAO phonetic speech.
    /// - Flight levels: "FL280" → "flight level two eight zero"
    /// - Squawk: "1200" → "one two zero zero"
    /// - General digit sequences: "309" → "tree zero niner"
    /// - Frequencies: "121.905" preserved as-is (Piper handles decimals fine)
    /// </summary>
    private static string ToAviationSpeech(string text)
    {
        var s = text;

        // Flight Level: "FL280" or "FL 280"
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\bFL\s?(\d{2,3})\b",
            m => "flight level " + DigitsToPhonetic(m.Groups[1].Value),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Squawk code (exactly 4 digits after "squawk")
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?i)\bsquawk\s+(\d{4})\b",
            m => "squawk " + DigitsToPhonetic(m.Groups[1].Value));

        // Callsign numbers (2-4 digits not part of a frequency like 121.9)
        // Only convert isolated digit groups: e.g. "302" in "Air India 302"
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?<![.\d])(\d{2,4})(?![.\d])",
            m => DigitsToPhonetic(m.Groups[1].Value));

        return s;
    }

    private static string DigitsToPhonetic(string digits)
    {
        var phonetics = new Dictionary<char, string>
        {
            ['0'] = "zero",
            ['1'] = "one",
            ['2'] = "two",
            ['3'] = "tree",
            ['4'] = "fower",
            ['5'] = "fife",
            ['6'] = "six",
            ['7'] = "seven",
            ['8'] = "eight",
            ['9'] = "niner",
        };
        return string.Join(" ", digits.Select(c => phonetics.TryGetValue(c, out var p) ? p : c.ToString()));
    }

    private void OnPhaseChanged(ControllerPhase from, ControllerPhase to)
    {
        var state = _simBridge.CurrentState;
        double freq = to switch
        {
            ControllerPhase.Ground            => state.GroundFreqMhz,
            ControllerPhase.Tower             => state.TowerFreqMhz,
            ControllerPhase.ClearanceDelivery => state.GroundFreqMhz,
            _ => 0
        };
        _overlay.UpdateControllerPhase(to, freq);
        _atcBrain.ClearHistoryForPhaseChange(from.ToString(), to.ToString());
        AppendFlightLog("SYSTEM", $"Phase: {from} → {to}");
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
        SaveFlightLog();          // write transcript before shutdown
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
