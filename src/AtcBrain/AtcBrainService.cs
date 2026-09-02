using Microsoft.Extensions.Logging;
using MsfsAiAtc.SimBridge;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MsfsAiAtc.AtcBrain;

public record ChatMessage(string Role, string Content);

/// <summary>
/// Core ATC brain — calls Groq LLM to produce ATC phraseology from live SimState.
///
/// ── Models (verified live from /v1/models endpoint, August 2026) ──────────────
///  PRIMARY:  openai/gpt-oss-120b    — best available (131k ctx, 65k max output)
///  FALLBACK: qwen/qwen3.8-27b       — strong 27B, 131k ctx, fast
///  (No Llama models on these keys — they are not in the model list)
///
/// ── Token management ─────────────────────────────────────────────────────────
///  max_tokens:  100  (ATC transmissions ≈ 15-25 words = ~30 tokens, 100 is generous)
///  History:     last 6 turns (12 messages) — keeps prompt lean
///  Prompt:      ~180 tokens
///  Per-request budget: ~500 tokens total — very low, safe at any free-tier limit
///
/// ── Dual-key rotation on 429 ──────────────────────────────────────────────────
///  1. Try Key 1. If 429 → try Key 2.
///  2. If Key 2 also 429 → show "Rate limit hit — wait a moment" in overlay.
///  3. Both keys auto-reset to Key 1 after 60 s (aligns with TPM window).
///  4. Non-429 errors (auth fail, bad request) don't trigger rotation.
/// </summary>
public class AtcBrainService
{
    private readonly ILogger<AtcBrainService> _logger;
    private readonly HttpClient _http;
    private readonly string[] _apiKeys;

    private const string EndpointUrl = "https://api.groq.com/openai/v1/chat/completions";

    // Best available model — verified from live /v1/models call
    private const string PrimaryModel  = "openai/gpt-oss-120b";
    private const string FallbackModel = "qwen/qwen3.8-27b"; // used if primary is rate-limited

    // ATC responses are short — "N631UA, runway 25L, cleared for takeoff" is ~12 tokens.
    // 100 is a generous ceiling that keeps token spend tiny.
    private const int MaxResponseTokens = 100;

    // Keep only the last 6 back-and-forth exchanges in history.
    // Older context is not needed — ATC doesn't reference conversations from 10 minutes ago.
    private const int MaxHistoryTurns = 6;

    private readonly List<ChatMessage> _history = new();

    // Key rotation state
    private int _activeKeyIndex = 0;
    private string _activeModel = PrimaryModel;
    private DateTime _lastRateLimitHit = DateTime.MinValue;
    private const int KeyResetSeconds = 60; // Groq TPM window is per-minute

    public AtcBrainService(ILogger<AtcBrainService> logger, HttpClient http, string[] apiKeys)
    {
        _logger = logger;
        _http = http;
        _apiKeys = apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();

        _logger.LogInformation("AtcBrain ready — {Count} key(s), primary model: {Model}",
            _apiKeys.Length, PrimaryModel);
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<string?> GetResponseAsync(
        string pilotTranscript,
        SimState simState,
        string controllerRole = "Ground/Tower",
        CancellationToken ct = default)
    {
        _history.Add(new ChatMessage("user", pilotTranscript));
        var result = await CallWithRotationAsync(simState, controllerRole, ct);
        if (result == null)
            _history.RemoveAt(_history.Count - 1);
        return result;
    }

    public async Task<string?> GetUnpromptedTransmissionAsync(
        string trigger, SimState simState, string controllerRole,
        CancellationToken ct = default)
    {
        _history.Add(new ChatMessage("user", $"[TRIGGER: {trigger}]"));
        var result = await CallWithRotationAsync(simState, controllerRole, ct);
        if (result == null)
            _history.RemoveAt(_history.Count - 1);
        return result;
    }

    public void ClearHistory() => _history.Clear();

    // ─── Key + model rotation ─────────────────────────────────────────────────

    private async Task<string?> CallWithRotationAsync(
        SimState simState, string controllerRole, CancellationToken ct)
    {
        if (_apiKeys.Length == 0)
        {
            _logger.LogError("No Groq API keys configured");
            return null;
        }

        // After 60 s, reset to primary key/model (TPM window has reset)
        if ((DateTime.UtcNow - _lastRateLimitHit).TotalSeconds > KeyResetSeconds)
        {
            _activeKeyIndex = 0;
            _activeModel = PrimaryModel;
        }

        // Build a rotation list: start from active key, wrap around
        var keyOrder = Enumerable
            .Range(0, _apiKeys.Length)
            .Select(i => (_activeKeyIndex + i) % _apiKeys.Length)
            .ToList();

        // Try primary model first, then fallback model on same key, then next key
        var attempts = new List<(int keyIdx, string model)>();
        foreach (var k in keyOrder)
        {
            attempts.Add((k, PrimaryModel));
            attempts.Add((k, FallbackModel));
        }

        foreach (var (keyIdx, model) in attempts)
        {
            var result = await TryCallAsync(_apiKeys[keyIdx], model, simState, controllerRole, ct);

            if (result.Success)
            {
                _activeKeyIndex = keyIdx;
                _activeModel = model;

                if (!string.IsNullOrWhiteSpace(result.Content))
                {
                    _history.Add(new ChatMessage("assistant", result.Content!));
                    TrimHistory();
                    _logger.LogInformation("[{Model}] key#{K}: {Text}",
                        model.Split('/').Last(), keyIdx + 1, result.Content);
                }
                return result.Content;
            }

            if (result.IsRateLimit)
            {
                _lastRateLimitHit = DateTime.UtcNow;
                _logger.LogWarning("Rate limit: key#{K} model={M} — trying next", keyIdx + 1, model);
                continue;
            }

            // Auth failure (401) — try next key rather than giving up immediately.
            // This handles the common case where key#1 is stale/empty but key#2 is valid.
            if (result.IsAuthFailure)
            {
                _logger.LogWarning("Auth failure on key#{K} — trying next key", keyIdx + 1);
                continue;
            }

            // Other hard error (bad request, server error) — stop
            _logger.LogWarning("API error on key#{K}: {Err}", keyIdx + 1, result.ErrorMessage);
            return null;
        }

        // Every key + model combination returned 429
        _logger.LogError("All API keys rate-limited. Both keys exhausted for this minute. " +
                         "Will auto-retry in ~{Sec}s", KeyResetSeconds);
        return "<<RATE_LIMIT>>"; // App.xaml.cs converts this to an overlay warning message
    }

    private async Task<LlmResult> TryCallAsync(
        string apiKey, string model, SimState simState, string controllerRole, CancellationToken ct)
    {
        try
        {
            var messages = BuildMessages(simState, controllerRole);

            var body = new
            {
                model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                max_tokens = MaxResponseTokens,
                temperature = 0.3,   // low = consistent ATC tone
                stream = false
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return LlmResult.RateLimit();

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return LlmResult.AuthFail($"HTTP 401: {err[..Math.Min(200, err.Length)]}");
            }

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return LlmResult.Error($"HTTP {(int)resp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim();

            return LlmResult.Ok(content);
        }
        catch (OperationCanceledException) { return LlmResult.Error("Cancelled"); }
        catch (Exception ex) { return LlmResult.Error(ex.Message); }
    }

    // ─── Prompt building ──────────────────────────────────────────────────────

    private List<ChatMessage> BuildMessages(SimState simState, string controllerRole)
    {
        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(simState, controllerRole))
        };
        int start = Math.Max(0, _history.Count - MaxHistoryTurns * 2);
        messages.AddRange(_history.Skip(start));
        return messages;
    }

    private static string BuildSystemPrompt(SimState state, string controllerRole)
    {
        return $"""
            You are a {controllerRole} ATC controller. Reply with ONE short radio transmission only.
            Use standard ICAO phraseology. Never invent callsigns, runways, or frequencies.
            Only reference facts in the SIM STATE. No markdown. No preamble. Plain text only.
            
            {state.ToContextString()}
            """;
    }

    private void TrimHistory()
    {
        int max = MaxHistoryTurns * 2;
        if (_history.Count > max)
            _history.RemoveRange(0, _history.Count - max);
    }

    // ─── Result ───────────────────────────────────────────────────────────────

    private record LlmResult(bool Success, bool IsRateLimit, bool IsAuthFailure, string? Content, string? ErrorMessage)
    {
        public static LlmResult Ok(string? c)      => new(true,  false, false, c,    null);
        public static LlmResult RateLimit()         => new(false, true,  false, null, "429");
        public static LlmResult AuthFail(string m)  => new(false, false, true,  null, m);
        public static LlmResult Error(string msg)   => new(false, false, false, null, msg);
    }
}
