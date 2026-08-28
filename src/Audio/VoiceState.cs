namespace MsfsAiAtc.Audio;

/// <summary>
/// The one and only voice pipeline state.
/// This is the single source of truth for "who can talk right now."
/// Per spec: IDLE → RECORDING → PROCESSING → SPEAKING → IDLE
/// </summary>
public enum VoiceState
{
    /// <summary>PTT can be pressed; app is waiting for pilot input or idle.</summary>
    Idle,

    /// <summary>Mic is open and capturing audio. PTT is currently held.</summary>
    Recording,

    /// <summary>Audio sent to Whisper; waiting for transcription + LLM response. PTT ignored.</summary>
    Processing,

    /// <summary>ATC is speaking. PTT ignored until this completes.</summary>
    Speaking
}
