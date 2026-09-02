using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;

namespace MsfsAiAtc.Audio;

/// <summary>
/// Manages the half-duplex PTT voice state machine and mic capture.
///
/// STATE MACHINE (per spec):
///   IDLE → (PTT pressed) → RECORDING → (PTT released) → PROCESSING → SPEAKING → IDLE
///
/// Rules enforced here:
///  - Mic is ONLY open between PTT-press and PTT-release.
///  - PTT ignored while PROCESSING or SPEAKING.
///  - Debounce: repeat key-down while RECORDING is ignored.
///  - Fresh WasapiCapture created on PTT-press, fully disposed on PTT-release.
///  - Buffer discarded after sending to Whisper.
///  - This is the ONLY place that calls capture start/stop — no other component may do so.
/// </summary>
public class VoicePipeline : IDisposable
{
    private readonly ILogger<VoicePipeline> _logger;
    private readonly string _preferredMicDeviceId;

    private VoiceState _state = VoiceState.Idle;
    private readonly object _stateLock = new();

    private WasapiCapture? _capture;
    private MemoryStream? _audioBuffer;
    private WaveFileWriter? _waveWriter;
    private TaskCompletionSource<bool>? _recordingStoppedTcs;
    private DateTime _recordingStartedAt;
    private bool _disposed;
    private const int MinRecordingMs = 400; // ignore taps shorter than this

    // Events published to the rest of the app
    public event Action<VoiceState>? StateChanged;
    public event Func<byte[], Task>? AudioCaptured; // raw WAV bytes ready for STT

    public VoiceState State
    {
        get { lock (_stateLock) return _state; }
    }

    public VoicePipeline(ILogger<VoicePipeline> logger, string preferredMicDeviceId)
    {
        _logger = logger;
        _preferredMicDeviceId = preferredMicDeviceId;
    }

    // ─── Called by the global hotkey hook ────────────────────────────────────

    /// <summary>
    /// Called when PTT key goes down. Only acts when state is IDLE.
    /// Debounce: subsequent key-down repeats while RECORDING are ignored.
    /// </summary>
    public void OnPttKeyDown()
    {
        lock (_stateLock)
        {
            if (_state != VoiceState.Idle)
            {
                _logger.LogDebug("PTT key-down ignored — state is {State}", _state);
                return; // already recording, processing, or speaking
            }
            TransitionTo(VoiceState.Recording);
        }
        StartCapture();
    }

    /// <summary>
    /// Called when PTT key goes up. Only acts when state is RECORDING.
    /// </summary>
    public void OnPttKeyUp()
    {
        lock (_stateLock)
        {
            if (_state != VoiceState.Recording)
            {
                _logger.LogDebug("PTT key-up ignored — state is {State}", _state);
                return;
            }
            TransitionTo(VoiceState.Processing);
        }
        StopCaptureAndSend();
    }

    // ─── Capture lifecycle ────────────────────────────────────────────────────

    private void StartCapture()
    {
        _logger.LogInformation("PTT pressed — opening microphone");
        try
        {
            var device = FindMicDevice();
            _capture = device != null
                ? new WasapiCapture(device)
                : new WasapiCapture();

            _capture.WaveFormat = new WaveFormat(16000, 16, 1); // 16kHz mono 16-bit
            _audioBuffer = new MemoryStream();
            _waveWriter = new WaveFileWriter(_audioBuffer, _capture.WaveFormat);
            _recordingStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordingStartedAt = DateTime.UtcNow;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start microphone capture");
            lock (_stateLock) TransitionTo(VoiceState.Idle);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void StopCaptureAndSend()
    {
        _logger.LogInformation("PTT released — stopping capture");

        // Enforce minimum recording duration — avoids "too short" errors from Whisper
        var elapsed = (DateTime.UtcNow - _recordingStartedAt).TotalMilliseconds;
        if (elapsed < MinRecordingMs)
        {
            var wait = (int)(MinRecordingMs - elapsed) + 50;
            _logger.LogDebug("Recording too short ({Ms}ms), padding {Wait}ms", (int)elapsed, wait);
            Thread.Sleep(wait);
        }

        // Signal capture to stop — OnRecordingStopped will be called by NAudio on its thread
        _capture?.StopRecording();
        // Actual send happens in OnRecordingStopped after all data is flushed
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        byte[]? wavBytes = null;

        try
        {
            // IMPORTANT: flush BEFORE reading from the MemoryStream
            _waveWriter?.Flush();
            _waveWriter?.Dispose();
            _waveWriter = null;

            if (_audioBuffer != null)
            {
                wavBytes = _audioBuffer.ToArray();
                _logger.LogDebug("Captured {Bytes} bytes of audio", wavBytes.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing audio buffer");
        }
        finally
        {
            // Dispose only capture and buffer — writer already disposed above
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.Dispose();
                    _capture = null;
                }
                _audioBuffer?.Dispose();
                _audioBuffer = null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error disposing capture: {Msg}", ex.Message);
            }
        }

        // Must have more than a WAV header (44 bytes) of actual audio data
        if (wavBytes != null && wavBytes.Length > 1000)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (AudioCaptured != null)
                        await AudioCaptured(wavBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in audio processing pipeline");
                }
            });
        }
        else
        {
            _logger.LogInformation("Captured audio too short ({Bytes} bytes) — skipping",
                wavBytes?.Length ?? 0);
            lock (_stateLock) TransitionTo(VoiceState.Idle);
        }
    }

    private void DisposeCapture()
    {
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }
            _waveWriter?.Dispose();
            _waveWriter = null;
            _audioBuffer?.Dispose();
            _audioBuffer = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error disposing capture: {Msg}", ex.Message);
        }
    }

    // ─── State management ─────────────────────────────────────────────────────

    private void TransitionTo(VoiceState next)
    {
        // Must be called with _stateLock held
        var prev = _state;
        _state = next;
        _logger.LogInformation("VoiceState: {Prev} → {Next}", prev, next);
        // Fire on UI thread
        Task.Run(() => StateChanged?.Invoke(next));
    }

    /// <summary>
    /// Called by the pipeline after LLM processing begins.
    /// </summary>
    public void SetProcessing()
    {
        lock (_stateLock)
        {
            if (_state == VoiceState.Processing) return;
            TransitionTo(VoiceState.Processing);
        }
    }

    /// <summary>
    /// Called by the TTS player when it starts speaking.
    /// </summary>
    public void SetSpeaking()
    {
        lock (_stateLock) TransitionTo(VoiceState.Speaking);
    }

    /// <summary>
    /// Called by the TTS player when it finishes speaking.
    /// Returns the state machine to IDLE.
    /// </summary>
    public void SetIdle()
    {
        lock (_stateLock) TransitionTo(VoiceState.Idle);
    }

    // ─── Device enumeration ───────────────────────────────────────────────────

    private MMDevice? FindMicDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            if (!string.IsNullOrEmpty(_preferredMicDeviceId))
            {
                var found = devices.FirstOrDefault(d => d.ID == _preferredMicDeviceId);
                if (found != null) return found;
                _logger.LogWarning("Saved mic device ID not found — using system default");
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not find mic device, using system default");
            return null;
        }
    }

    /// <summary>
    /// Enumerates all capture (input) devices. Stores by ID, not index.
    /// </summary>
    public static List<AudioDevice> EnumerateMicDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDevice { Id = dev.ID, FriendlyName = dev.FriendlyName });
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Enumerates all render (output) devices. Stores by ID, not index.
    /// </summary>
    public static List<AudioDevice> EnumerateOutputDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice { Id = dev.ID, FriendlyName = dev.FriendlyName });
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCapture();
    }
}

public class AudioDevice
{
    public string Id { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
}
