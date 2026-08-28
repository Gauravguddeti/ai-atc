using System.Windows.Input;

namespace MsfsAiAtc.Config;

/// <summary>
/// Strongly-typed application configuration loaded from .env
/// </summary>
public class AppConfig
{
    public string GroqApiKey { get; init; } = string.Empty;
    /// <summary>Fallback key — used when primary hits rate limit (429)</summary>
    public string GroqApiKey2 { get; init; } = string.Empty;
    public Key PushToTalkKey { get; init; } = Key.CapsLock;
    public string MicDeviceId { get; init; } = string.Empty;
    public string OutputDeviceId { get; init; } = string.Empty;
    public string PiperVoice { get; init; } = "en_US-lessac-medium";
    public string LogLevel { get; init; } = "info";

    /// <summary>
    /// Path to the Piper executable (relative to app directory)
    /// </summary>
    public string PiperExePath { get; init; } = "piper/piper/piper.exe";


    /// <summary>
    /// Path to the Piper voice model .onnx file
    /// </summary>
    public string PiperModelPath { get; init; } = "piper/models/en_US-lessac-medium.onnx";
}
