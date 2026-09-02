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
    /// Transcribes WAV audio bytes. Tries all configured API keys before giving up.
    /// Returns null if silent/empty or if all keys fail.
    /// </summary>
    public async Task<string?> TranscribeAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0) return null;
        if (_apiKeys.Length == 0)
        {
            _logger.LogWarning("Whisper: No API keys configured.");
            return null;
        }

        // Try every key starting from _keyIndex
        for (int attempt = 0; attempt < _apiKeys.Length; attempt++)
        {
            var idx = (_keyIndex + attempt) % _apiKeys.Length;
            var key = _apiKeys[idx];

            try
            {
                // Each attempt needs a fresh form (content streams can only be sent once)
                using var form = new MultipartFormDataContent();
                var audioContent = new ByteArrayContent(wavBytes);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(audioContent, "file", "audio.wav");
                form.Add(new StringContent(Model), "model");
                form.Add(new StringContent("en"), "language");
                form.Add(new StringContent("json"), "response_format");

                using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = form;

                using var response = await _http.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Whisper key#{K} error {Status} — trying next key. Body: {B}",
                        idx + 1, response.StatusCode, errBody);
                    continue; // try next key
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Whisper API error {Status}: {Body}", response.StatusCode, errBody);
                    return null; // hard error, stop
                }

                // Success — advance key index for next call (round-robin)
                _keyIndex = (idx + 1) % _apiKeys.Length;

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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whisper transcription failed on key#{K}", idx + 1);
                return null;
            }
        }

        _logger.LogError("Whisper: all {N} API keys failed.", _apiKeys.Length);
        return null;
    }
}
