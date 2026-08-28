using Microsoft.Extensions.Logging;
using MsfsAiAtc.Audio;
using MsfsAiAtc.AtcBrain;
using MsfsAiAtc.Config;
using MsfsAiAtc.Handoff;
using MsfsAiAtc.Overlay;
using MsfsAiAtc.SimBridge;
using MsfsAiAtc.Speech;
using MsfsAiAtc.Triggers;
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

    private HttpClient _httpClient = null!;
    private CancellationTokenSource _appCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Logging
        _loggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
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
            _loggerFactory.CreateLogger<GroqWhisperClient>(), _httpClient, _config.GroqApiKey);

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
        _simBridge.Connected += () => _overlay.AddSystemMessage("SimConnect: Connected to MSFS ✓");
        _simBridge.Disconnected += () => _overlay.AddSystemMessage("SimConnect: Disconnected — retrying...");

        // Initialize after overlay is rendered (to get HWND)
        _overlay.ContentRendered += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_overlay).Handle;
            _simBridge.Initialize(hwnd);
            _hotkeyHook.Initialize(hwnd);
        };

        _overlay.AddSystemMessage($"PTT key: [{_config.PushToTalkKey}]  |  Piper: {(_piperTts.IsAvailable ? "Ready" : "Not found — install via setup")}");

        // Set main window
        MainWindow = _overlay;
    }

    // ─── Event handlers ───────────────────────────────────────────────────────

    private void OnSimStateUpdated(SimState state)
    {
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
                // Piper not available — log and continue without audio
                _overlay.AddSystemMessage("[TTS unavailable — install Piper via setup wizard]");
                await Task.Delay(1000, ct); // brief pause to simulate speaking time
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
