using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;

namespace MsfsAiAtc.Speech;

/// <summary>
/// Wraps the Piper TTS binary to generate speech audio from text.
///
/// Piper is invoked as a subprocess:
///   piper.exe --model <model.onnx> --output-raw
/// Text is sent via stdin, raw PCM audio is read from stdout.
/// The raw PCM is then wrapped into a WAV container before returning.
/// </summary>
public class PiperTtsWrapper
{
    private readonly ILogger<PiperTtsWrapper> _logger;
    private readonly string _piperExePath;
    private readonly string _modelPath;
    private const int SampleRate = 22050;
    private const int Channels = 1;
    private const int BitDepth = 16;

    public PiperTtsWrapper(ILogger<PiperTtsWrapper> logger, string piperExePath, string modelPath)
    {
        _logger = logger;
        _piperExePath = piperExePath;
        _modelPath = modelPath;
    }

    /// <summary>
    /// Returns true if the Piper binary and model file exist.
    /// </summary>
    public bool IsAvailable =>
        File.Exists(_piperExePath) && File.Exists(_modelPath);

    /// <summary>
    /// Synthesizes <paramref name="text"/> and returns WAV bytes ready for the radio filter and playback.
    /// Returns null if Piper is unavailable or synthesis fails.
    /// </summary>
    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning("Piper not available — TTS skipped. Exe={Exe} Model={Model}", _piperExePath, _modelPath);
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _piperExePath,
                Arguments = $"--model \"{_modelPath}\" --output-raw",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // Write text to stdin
            await proc.StandardInput.WriteLineAsync(text);
            proc.StandardInput.Close();

            // Read raw PCM from stdout
            using var rawStream = new MemoryStream();
            var readTask = proc.StandardOutput.BaseStream.CopyToAsync(rawStream, ct);

            // Also drain stderr to prevent deadlock
            var errTask = proc.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(readTask, errTask);

            var exitCode = proc.HasExited ? proc.ExitCode : -1;
            if (!proc.HasExited)
            {
                await proc.WaitForExitAsync(ct);
                exitCode = proc.ExitCode;
            }

            var stderr = await errTask;
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogDebug("Piper stderr: {Err}", stderr);

            if (exitCode != 0)
            {
                _logger.LogWarning("Piper exited with code {Code}", exitCode);
                return null;
            }

            var rawPcm = rawStream.ToArray();
            if (rawPcm.Length == 0) return null;

            return WrapInWav(rawPcm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Piper TTS synthesis failed");
            return null;
        }
    }

    /// <summary>
    /// Wraps raw 16-bit PCM audio in a standard WAV container header.
    /// </summary>
    private static byte[] WrapInWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int dataLen = pcm.Length;
        int byteRate = SampleRate * Channels * BitDepth / 8;
        int blockAlign = Channels * BitDepth / 8;

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen); // chunk size
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);           // subchunk1 size (PCM)
        bw.Write((short)1);     // audio format (PCM = 1)
        bw.Write((short)Channels);
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)BitDepth);

        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm);

        return ms.ToArray();
    }
}
