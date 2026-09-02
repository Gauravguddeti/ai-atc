using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MsfsAiAtc.Speech;

/// <summary>
/// Sends captured audio to Groq's Whisper endpoint and returns the transcript.
/// Model: whisper-large-v3-turbo (fast, free tier friendly)
/// </summary>
public class GroqWhisperClient
{
    private readonly ILogger<GroqWhisperClient> _logger;
    private readonly HttpClient _http;
    private readonly string[] _apiKeys;
    private int _keyIndex = 0;
    private const string EndpointUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string Model = "whisper-large-v3-turbo";

    public GroqWhisperClient(ILogger<GroqWhisperClient> logger, HttpClient http, string[] apiKeys)
    {
        _logger = logger;
        _http = http;
        _apiKeys = apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
    }

    private string GetNextKey()
    {
        if (_apiKeys.Length == 0) return string.Empty;
        var key = _apiKeys[_keyIndex];
        _keyIndex = (_keyIndex + 1) % _apiKeys.Length;
        return key;
    }

    /// <summary>
    /// Transcribes WAV audio bytes. Returns null or empty string if silent/empty.
    /// </summary>
    public async Task<string?> TranscribeAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0) return null;

        try
        {
            using var form = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(wavBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "audio.wav");
            form.Add(new StringContent(Model), "model");
            form.Add(new StringContent("en"), "language");
            form.Add(new StringContent("json"), "response_format");

            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            var keyToUse = GetNextKey();
            if (string.IsNullOrEmpty(keyToUse))
            {
                _logger.LogWarning("Whisper API error: No API keys configured.");
                return null;
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyToUse);
            request.Content = form;

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Whisper API error {Status}: {Body}", response.StatusCode, errBody);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString()?.Trim();
                _logger.LogInformation("Whisper transcript: {Text}", text);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper transcription failed");
            return null;
        }
    }
}
