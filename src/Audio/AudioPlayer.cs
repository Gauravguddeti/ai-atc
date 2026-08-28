using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;

namespace MsfsAiAtc.Audio;

/// <summary>
/// Plays back audio through the configured output device.
/// Applies the radio DSP filter before playback.
/// </summary>
public class AudioPlayer : IDisposable
{
    private readonly ILogger<AudioPlayer> _logger;
    private readonly string _preferredOutputDeviceId;

    public AudioPlayer(ILogger<AudioPlayer> logger, string preferredOutputDeviceId)
    {
        _logger = logger;
        _preferredOutputDeviceId = preferredOutputDeviceId;
    }

    /// <summary>
    /// Plays WAV audio bytes through the output device with the radio filter applied.
    /// Blocks until playback is complete.
    /// </summary>
    public async Task PlayWithRadioFilterAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0) return;

        try
        {
            // Read WAV into float samples
            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);

            var sampleProvider = reader.ToSampleProvider();
            var allSamples = ReadAllSamples(sampleProvider);
            var sampleRate = reader.WaveFormat.SampleRate;

            // Apply radio DSP filter
            var filtered = RadioFilter.Apply(allSamples, sampleRate);

            // Convert back to WAV bytes for WaveOut
            var filteredWav = ToWavBytes(filtered, sampleRate, reader.WaveFormat.Channels);

            await PlayWavBytesAsync(filteredWav, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during radio filter audio playback");
        }
    }

    /// <summary>
    /// Plays raw WAV bytes without any filter (used for system sounds).
    /// </summary>
    public async Task PlayRawAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        await PlayWavBytesAsync(wavBytes, ct);
    }

    private async Task PlayWavBytesAsync(byte[] wavBytes, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        WasapiOut? waveOut = null;

        try
        {
            var device = FindOutputDevice();
            waveOut = device != null
                ? new WasapiOut(device, AudioClientShareMode.Shared, true, 200)
                : new WasapiOut();

            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);

            waveOut.PlaybackStopped += (s, e) =>
            {
                tcs.TrySetResult(true);
            };

            waveOut.Init(reader);
            waveOut.Play();

            using var ctReg = ct.Register(() => { waveOut.Stop(); tcs.TrySetResult(false); });
            await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playback failed");
        }
        finally
        {
            waveOut?.Dispose();
        }
    }

    private static float[] ReadAllSamples(ISampleProvider provider)
    {
        var buffer = new List<float>();
        var chunk = new float[4096];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            buffer.AddRange(chunk.Take(read));
        return buffer.ToArray();
    }

    private static byte[] ToWavBytes(float[] samples, int sampleRate, int channels)
    {
        using var ms = new MemoryStream();
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(ms, fmt);
        writer.WriteSamples(samples, 0, samples.Length);
        writer.Flush();
        return ms.ToArray();
    }

    private MMDevice? FindOutputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(_preferredOutputDeviceId))
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var found = devices.FirstOrDefault(d => d.ID == _preferredOutputDeviceId);
                if (found != null) return found;
                _logger.LogWarning("Saved output device ID not found — using system default");
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() { }
}
