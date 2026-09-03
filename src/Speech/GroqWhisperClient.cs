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
    // whisper-large-v3 has significantly better accuracy than turbo for domain-specific speech
    private const string Model = "whisper-large-v3";

    // Priming prompt — Whisper uses this to bias recognition toward aviation vocabulary.
    // This single string dramatically reduces errors like "Poneground" -> "Pune Ground",
    // "three zero two" being recognised correctly, and ICAO phrases being preserved.
    private const string AviationPrompt =
        "ATC radio communication. ICAO phraseology. " +
        "Phonetics: Alpha Bravo Charlie Delta Echo Foxtrot Golf Hotel India Juliet Kilo Lima Mike November Oscar Papa Quebec Romeo Sierra Tango Uniform Victor Whiskey Xray Yankee Zulu. " +
        "Numbers: zero one two tree(3) fower(4) fife(5) six seven eight niner(9). Niner=9, tree=3, fower=4, fife=5. " +
        "Airports: Pune VAPO, Mumbai VABB, Delhi VIDP, Hyderabad VOHS, Bangalore VOBL. " +
        "Airlines: Air India, IndiGo, SpiceJet, Vistara, Emirates, Speedbird. " +
        "Phrases: request taxi, cleared for takeoff, cleared to land, hold short, line up and wait, " +
        "go around, wilco, roger, affirm, negative, say again, radio check, request IFR clearance.";

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
                form.Add(new StringContent(AviationPrompt), "prompt");

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
