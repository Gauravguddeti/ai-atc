using DotNetEnv;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows.Input;

namespace MsfsAiAtc.Config;

/// <summary>
/// Loads and validates app configuration from .env file.
/// The .env file is searched in: app directory, then working directory.
/// </summary>
public static class ConfigLoader
{
    public static AppConfig Load(ILogger? logger = null)
    {
        // Find .env — check app dir first, then CWD
        var appDir = AppContext.BaseDirectory;
        var envPath = Path.Combine(appDir, ".env");
        if (!File.Exists(envPath))
            envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (!File.Exists(envPath))
            envPath = Path.Combine(AppContext.BaseDirectory, "..", ".env");

        if (File.Exists(envPath))
        {
            Env.Load(envPath);
            logger?.LogInformation("Loaded .env from {Path}", envPath);
        }
        else
        {
            logger?.LogWarning(".env file not found — using environment variables only");
        }

        var config = new AppConfig
        {
            GroqApiKey = Env.GetString("GROQ_API_KEY", string.Empty),
            GroqApiKey2 = Env.GetString("GROQ_API_KEY_2", string.Empty),
            PushToTalkKey = ParseKey(Env.GetString("PUSH_TO_TALK_KEY", "CapsLock")),
            MicDeviceId = Env.GetString("MIC_DEVICE_ID", string.Empty),
            OutputDeviceId = Env.GetString("OUTPUT_DEVICE_ID", string.Empty),
            PiperVoice = Env.GetString("PIPER_VOICE", "en_US-lessac-medium"),
            LogLevel = Env.GetString("LOG_LEVEL", "info"),
            PiperExePath = Path.Combine(appDir, "piper", "piper", "piper.exe"),
            PiperModelPath = Path.Combine(appDir, "piper", "models", Env.GetString("PIPER_VOICE", "en_US-lessac-medium") + ".onnx"),
        };

        if (string.IsNullOrWhiteSpace(config.GroqApiKey))
            logger?.LogWarning("GROQ_API_KEY is not set — STT and LLM calls will fail");

        var keyCount = new[] { config.GroqApiKey, config.GroqApiKey2 }.Count(k => !string.IsNullOrWhiteSpace(k));
        logger?.LogInformation("{Count} Groq API key(s) loaded", keyCount);

        return config;
    }

    private static Key ParseKey(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return Key.CapsLock;
        if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var k)) return k;
        return Key.CapsLock;
    }
}
