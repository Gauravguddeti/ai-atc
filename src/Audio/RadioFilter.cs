namespace MsfsAiAtc.Audio;

/// <summary>
/// Applies a fixed DSP chain to Piper TTS audio to simulate a VHF aviation radio transmission.
///
/// Chain (in order):
///  1. Bandpass filter  — ~300 Hz to 3000 Hz (telephone/radio band)
///  2. Dynamic compression — reduces dynamic range, increases perceived loudness
///  3. Soft clipping / saturation — adds subtle harmonic distortion
///  4. Low-level static noise bed — the "hiss" of an open squelch
///
/// Per spec: this is a FIXED chain, not adaptive. It only ever touches the AI's outgoing audio.
/// Isolated here as one clearly-named function for later tuning.
/// </summary>
public static class RadioFilter
{
    // Bandpass butterworth state
    private static readonly double SampleRate = 22050.0; // Piper default output rate

    /// <summary>
    /// Apply the full radio DSP chain to a buffer of float samples.
    /// Input: raw Piper TTS float samples at <paramref name="sampleRate"/> Hz, mono.
    /// Output: processed samples ready for playback.
    /// </summary>
    public static float[] Apply(float[] samples, int sampleRate)
    {
        var output = (float[])samples.Clone();
        ApplyBandpass(output, sampleRate, lowCutHz: 300.0, highCutHz: 3000.0);
        ApplyCompression(output, threshold: 0.3f, ratio: 4.0f, makeupGain: 2.0f);
        ApplySoftClip(output, drive: 1.8f);
        AddStaticBed(output, level: 0.008f);
        return output;
    }

    // ─── 1. Bandpass ──────────────────────────────────────────────────────────

    /// <summary>
    /// Two-pole butterworth bandpass via cascaded high-pass + low-pass biquads.
    /// </summary>
    private static void ApplyBandpass(float[] buf, int fs, double lowCutHz, double highCutHz)
    {
        // High-pass at lowCutHz
        ApplyBiquad(buf, BiquadHighPass(lowCutHz, fs));
        // Low-pass at highCutHz
        ApplyBiquad(buf, BiquadLowPass(highCutHz, fs));
    }

    private static (double b0, double b1, double b2, double a1, double a2) BiquadHighPass(double freq, int fs)
    {
        double w0 = 2 * Math.PI * freq / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2 * 0.707); // Q = 0.707 (butterworth)
        double b0 = (1 + cosW0) / 2;
        double b1 = -(1 + cosW0);
        double b2 = (1 + cosW0) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cosW0;
        double a2 = 1 - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private static (double b0, double b1, double b2, double a1, double a2) BiquadLowPass(double freq, int fs)
    {
        double w0 = 2 * Math.PI * freq / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2 * 0.707);
        double b0 = (1 - cosW0) / 2;
        double b1 = 1 - cosW0;
        double b2 = (1 - cosW0) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cosW0;
        double a2 = 1 - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private static void ApplyBiquad(
        float[] buf,
        (double b0, double b1, double b2, double a1, double a2) c)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            double x0 = buf[i];
            double y0 = c.b0 * x0 + c.b1 * x1 + c.b2 * x2 - c.a1 * y1 - c.a2 * y2;
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
            buf[i] = (float)y0;
        }
    }

    // ─── 2. Compression ───────────────────────────────────────────────────────

    private static void ApplyCompression(float[] buf, float threshold, float ratio, float makeupGain)
    {
        float envelope = 0f;
        const float attack = 0.003f;   // seconds per sample at 22050 Hz ≈ 66 samples
        const float release = 0.1f;
        float attackCoeff = (float)Math.Exp(-1.0 / (attack * SampleRate));
        float releaseCoeff = (float)Math.Exp(-1.0 / (release * SampleRate));

        for (int i = 0; i < buf.Length; i++)
        {
            float abs = Math.Abs(buf[i]);
            envelope = abs > envelope
                ? attackCoeff * envelope + (1 - attackCoeff) * abs
                : releaseCoeff * envelope + (1 - releaseCoeff) * abs;

            float gain = 1.0f;
            if (envelope > threshold)
                gain = threshold + (envelope - threshold) / ratio;
            gain = Math.Min(gain / Math.Max(envelope, 1e-10f), 1.0f);

            buf[i] = Math.Clamp(buf[i] * gain * makeupGain, -1f, 1f);
        }
    }

    // ─── 3. Soft clip / saturation ────────────────────────────────────────────

    private static void ApplySoftClip(float[] buf, float drive)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i] * drive;
            // Cubic soft clipper: y = x - x³/3   (only applied for |x| < 1)
            buf[i] = Math.Clamp(
                Math.Abs(x) < 1f ? x - (x * x * x) / 3f : Math.Sign(x) * 0.667f,
                -1f, 1f);
        }
    }

    // ─── 4. Static noise bed ─────────────────────────────────────────────────

    private static readonly Random _rng = new();

    private static void AddStaticBed(float[] buf, float level)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            float noise = (float)(_rng.NextDouble() * 2.0 - 1.0) * level;
            buf[i] = Math.Clamp(buf[i] + noise, -1f, 1f);
        }
    }
}
