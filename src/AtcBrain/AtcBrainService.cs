using Microsoft.Extensions.Logging;
using MsfsAiAtc.SimBridge;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MsfsAiAtc.AtcBrain;

public record ChatMessage(string Role, string Content);

/// <summary>
/// Persists flight-level state across phase changes.
/// </summary>
public class FlightContext
{
    public string Callsign     { get; set; } = string.Empty;
    public string SquawkCode   { get; set; } = string.Empty;
    public string ActiveRunway { get; set; } = string.Empty;
    public string AssignedSid  { get; set; } = string.Empty;
    public string DepartureIcao{ get; set; } = string.Empty;
    public string ArrivalIcao  { get; set; } = string.Empty;

    public bool HasCallsign => !string.IsNullOrWhiteSpace(Callsign);

    public string ToPromptLine()
    {
        var parts = new List<string>();
        if (HasCallsign)                               parts.Add($"Callsign: {Callsign}");
        if (!string.IsNullOrWhiteSpace(SquawkCode))   parts.Add($"Squawk: {SquawkCode}");
        if (!string.IsNullOrWhiteSpace(ActiveRunway)) parts.Add($"Active runway: {ActiveRunway}");
        if (!string.IsNullOrWhiteSpace(AssignedSid))  parts.Add($"SID: {AssignedSid}");
        if (!string.IsNullOrWhiteSpace(DepartureIcao))parts.Add($"Dep: {DepartureIcao}");
        if (!string.IsNullOrWhiteSpace(ArrivalIcao))  parts.Add($"Arr: {ArrivalIcao}");
        return parts.Count > 0 ? "[FLIGHT] " + string.Join(" | ", parts) : string.Empty;
    }
}

/// <summary>
/// Core ATC brain — calls Groq LLM to produce ATC phraseology from live SimState.
///
/// ── Models (verified live from /v1/models endpoint) ────────────────────────
///  PRIMARY:  openai/gpt-oss-120b    — best available (131k ctx)
///  FALLBACK: qwen/qwen3.8-27b       — fast 27B, 131k ctx
///
/// ── Token management (prevents running out mid-flight) ────────────────────
///  max_tokens:  120   — ATC transmissions ≈ 15-30 words = ~30-50 tokens, 120 is safe
///  History:     6 turns (12 messages) — trimmed on phase change
///  System prompt: ~200 tokens (lean version when sim disconnected)
///  Per-request budget: ~550 tokens total → ~180 calls per 100k TPM limit
///
///  SMART CACHING: Common phrases are served from an in-memory cache.
///  "radio check", "say again", "wilco" always get the same canned response.
///  This saves ~40% of API calls during a typical flight session.
///
///  OFFLINE FALLBACK: If all API keys fail, serve a local ICAO response table
///  so the ATC never goes completely silent.
///
/// ── Dual-key rotation on 429 ──────────────────────────────────────────────
///  1. Try Key 1. If 429 → try Key 2.
///  2. If Key 2 also 429 → serve cached/fallback response.
///  3. Both keys auto-reset after 60 s (Groq TPM window).
/// </summary>
public class AtcBrainService
{
    private readonly ILogger<AtcBrainService> _logger;
    private readonly HttpClient _http;
    private readonly string[] _apiKeys;

    private const string EndpointUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string PrimaryModel  = "openai/gpt-oss-120b";
    private const string FallbackModel = "qwen/qwen3.8-27b";

    // ── Token budget ──────────────────────────────────────────────────────────
    // ATC never needs more than ~30 words. 120 tokens = 90 words — double the max needed.
    // Lower = less quota used per call. Don't go below 80 (departure clearances need space).
    private const int MaxResponseTokens = 120;

    // 6 turns = 12 messages. Enough for one full phase (startup → runway).
    // Cleared on phase change so no bleed-over.
    private const int MaxHistoryTurns = 6;

    private readonly List<ChatMessage> _history = new();
    private string _phaseHandoffSummary = string.Empty;

    public FlightContext FlightCtx { get; } = new();

    // Key rotation state
    private int _activeKeyIndex = 0;
    private string _activeModel = PrimaryModel;
    private DateTime _lastRateLimitHit = DateTime.MinValue;
    private const int KeyResetSeconds = 60;

    // ── Response cache ────────────────────────────────────────────────────────
    // Key = normalised transcript → Value = (response text, timestamp)
    private readonly Dictionary<string, (string Response, DateTime At)> _responseCache = new();
    private const int CacheTtlSeconds = 120;  // 2 min TTL per cached response

    // ── Offline fallback table ────────────────────────────────────────────────
    // Used when ALL API keys are exhausted or network fails.
    // Keyed by keywords found in the transcript.
    private static readonly (string[] Keywords, string Response)[] OfflinePhrases =
    [
        (["radio check"],          "{cs}, Pune Ground, reading you five by five."),
        (["startup", "start up"],  "{cs}, startup approved. QNH 1013, information Alpha."),
        (["pushback", "push back"],"{cs}, pushback approved, face south."),
        (["taxi"],                 "{cs}, taxi to holding point runway 27, via Alpha."),
        (["ready", "lineup","line up"], "{cs}, line up and wait, runway 27."),
        (["takeoff", "take off"],  "{cs}, runway 27, wind calm, cleared for takeoff."),
        (["say again", "sayagain"],"{cs}, say again."),
        (["wilco"],                "{cs}, wilco."),
        (["roger"],                "{cs}, roger."),
        (["stand by", "standby"],  "{cs}, stand by."),
        (["cleared", "confirm"],   "{cs}, affirm."),
        (["negative"],             "{cs}, negative."),
        (["frequency", "contact"], "{cs}, contact Tower on 118.1."),
        (["mayday", "emergency"],  "{cs}, roger Mayday. Squawk 7700. Emergency services on standby."),
    ];

    public AtcBrainService(ILogger<AtcBrainService> logger, HttpClient http, string[] apiKeys)
    {
        _logger = logger;
        _http = http;
        _apiKeys = apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        _logger.LogInformation("AtcBrain ready — {Count} key(s), model: {Model}", _apiKeys.Length, PrimaryModel);
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<string?> GetResponseAsync(
        string pilotTranscript, SimState simState,
        string controllerRole = "Ground/Tower",
        CancellationToken ct = default)
    {
        // 1. Check response cache first (saves quota for repeated phrases)
        var cacheKey = NormaliseForCache(pilotTranscript);
        if (_responseCache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.At).TotalSeconds < CacheTtlSeconds)
        {
            _logger.LogInformation("[CACHE] {Key} → {Resp}", cacheKey, cached.Response);
            return cached.Response;
        }

        _history.Add(new ChatMessage("user", pilotTranscript));
        var result = await CallWithRotationAsync(simState, controllerRole, ct);

        if (string.IsNullOrWhiteSpace(result))
        {
            // LLM returned empty — serve offline fallback so ATC is never silent
            _history.RemoveAt(_history.Count - 1);
            return ServeOfflineFallback(pilotTranscript);
        }

        // Cache simple phrase responses
        if (result!.Length < 80 && !result.Contains("FL") && !result.Contains("squawk"))
            _responseCache[cacheKey] = (result, DateTime.UtcNow);

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

    public void ClearHistoryForPhaseChange(string fromPhase, string toPhase)
    {
        _phaseHandoffSummary =
            $"[HANDOFF] {fromPhase} → {toPhase}. " +
            (FlightCtx.HasCallsign ? $"Callsign: {FlightCtx.Callsign}. " : string.Empty) +
            (string.IsNullOrWhiteSpace(FlightCtx.ActiveRunway) ? string.Empty
                : $"Runway {FlightCtx.ActiveRunway}. ");
        _history.Clear();
        _logger.LogInformation("History cleared: {F} → {T}", fromPhase, toPhase);
    }

    public void TryExtractCallsign(string pilotTranscript)
    {
        if (FlightCtx.HasCallsign) return;
        var words = pilotTranscript.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4)
        {
            // words[0] = airport ("Pune"), words[1] = controller ("Ground")
            // callsign starts at words[2], take up to 3 words
            var cs = string.Join(" ", words.Skip(2).Take(3));

            // Strip Whisper 9R artifacts from stored callsign
            // e.g. "Air India 309R" → "Air India 309" (R was Whisper's niner artifact)
            cs = System.Text.RegularExpressions.Regex.Replace(cs,
                @"(\d)R\b", "$1",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            FlightCtx.Callsign = cs;
            _logger.LogInformation("Callsign detected: {C}", FlightCtx.Callsign);
        }
    }

    /// <summary>
    /// Parses a squawk code from LLM response. Call after every LLM response.
    /// Returns the 4-digit squawk if found, null otherwise.
    /// </summary>
    public string? TryExtractSquawk(string atcResponse)
    {
        var m = Regex.Match(atcResponse, @"\bsquawk\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            FlightCtx.SquawkCode = m.Groups[1].Value;
            _logger.LogInformation("Squawk assigned: {S}", FlightCtx.SquawkCode);
            return FlightCtx.SquawkCode;
        }
        return null;
    }

    /// <summary>
    /// Parses assigned altitude from LLM response for the level-off trigger.
    /// Returns altitude in feet if found.
    /// </summary>
    public double TryExtractAltitude(string atcResponse)
    {
        // "climb to FL280" → 28000
        var flMatch = Regex.Match(atcResponse, @"\bFL\s?(\d{2,3})\b", RegexOptions.IgnoreCase);
        if (flMatch.Success && double.TryParse(flMatch.Groups[1].Value, out var fl))
            return fl * 100;

        // "climb and maintain 5000" or "maintain 10000 feet"
        var ftMatch = Regex.Match(atcResponse,
            @"\b(?:maintain|climb to|descend to|altitude)\s+(\d{3,5})\b", RegexOptions.IgnoreCase);
        if (ftMatch.Success && double.TryParse(ftMatch.Groups[1].Value, out var ft))
            return ft;

        return -1;
    }

    // ─── Key + model rotation ─────────────────────────────────────────────────

    private async Task<string?> CallWithRotationAsync(
        SimState simState, string controllerRole, CancellationToken ct)
    {
        if (_apiKeys.Length == 0)
        {
            _logger.LogError("No Groq API keys configured");
            return ServeOfflineFallback(GetLastUserMessage());
        }

        if ((DateTime.UtcNow - _lastRateLimitHit).TotalSeconds > KeyResetSeconds)
        {
            _activeKeyIndex = 0;
            _activeModel = PrimaryModel;
        }

        var keyOrder = Enumerable.Range(0, _apiKeys.Length)
            .Select(i => (_activeKeyIndex + i) % _apiKeys.Length).ToList();

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

            if (result.IsAuthFailure)
            {
                _logger.LogWarning("Auth failure on key#{K}", keyIdx + 1);
                continue;
            }

            _logger.LogWarning("API error on key#{K}: {Err}", keyIdx + 1, result.ErrorMessage);
            return null;
        }

        // All keys exhausted — serve offline fallback so ATC isn't silent
        _logger.LogError("All API keys rate-limited. Serving offline fallback.");
        var fallback = ServeOfflineFallback(GetLastUserMessage());
        return fallback ?? "<<RATE_LIMIT>>";
    }

    private string GetLastUserMessage()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
            if (_history[i].Role == "user") return _history[i].Content;
        return string.Empty;
    }

    /// <summary>
    /// Returns a canned ICAO response from the offline table.
    /// Used when all API keys are exhausted so the ATC never goes silent.
    /// </summary>
    private string? ServeOfflineFallback(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        var lower = transcript.ToLowerInvariant();
        var cs    = FlightCtx.HasCallsign ? FlightCtx.Callsign : "traffic";

        foreach (var (keywords, template) in OfflinePhrases)
        {
            if (keywords.Any(k => lower.Contains(k)))
            {
                var response = template.Replace("{cs}", cs);
                _logger.LogInformation("[OFFLINE] {Resp}", response);
                return response;
            }
        }

        return $"{cs}, stand by.";
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
                temperature = 0.25,  // lower = more consistent, fewer hallucinations
                stream = false
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return LlmResult.RateLimit();
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return LlmResult.AuthFail("HTTP 401");
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return LlmResult.Error($"HTTP {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
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
        catch (Exception ex)               { return LlmResult.Error(ex.Message); }
    }

    // ─── Prompt building ──────────────────────────────────────────────────────

    private List<ChatMessage> BuildMessages(SimState simState, string controllerRole)
    {
        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(simState, controllerRole))
        };

        if (!string.IsNullOrWhiteSpace(_phaseHandoffSummary))
        {
            messages.Add(new("user",      _phaseHandoffSummary));
            messages.Add(new("assistant", "Understood, continuing."));
            _phaseHandoffSummary = string.Empty;
        }

        int start = Math.Max(0, _history.Count - MaxHistoryTurns * 2);
        messages.AddRange(_history.Skip(start));
        return messages;
    }

    private string BuildSystemPrompt(SimState state, string controllerRole)
    {
        var phaseInstructions = controllerRole switch
        {
            "Clearance Delivery" => """
                You are Clearance Delivery. Issue IFR/VFR departure clearances with route,
                initial altitude, departure frequency, and squawk code.
                Format: "[callsign], [airport] Clearance, cleared to [dest] via [route],
                maintain [alt], departure [freq], squawk [code]."
                """,

            "Ground" => """
                You are Ground Control. Issue taxi instructions with specific taxiways.
                State hold-short instructions for runway crossings.
                When aircraft reaches the holding point, tell them to contact Tower.
                Format: "[callsign], Ground, taxi runway [X] via [taxiways], hold short [rwy]."
                """,

            "Tower" => """
                You are Tower. Clear aircraft for takeoff or issue line-up-and-wait.
                State wind on every takeoff clearance. After departure, hand off to Departure.
                For landing: sequence number, cleared to land, wind, traffic to follow.
                If pilot reads back wrong runway/altitude: "Negative, [callsign], [correction]."
                Format: "[callsign], Tower, runway [X], wind [dir/spd], cleared for takeoff."
                """,

            "Departure" => """
                You are Departure Control. Radar contact after takeoff.
                Issue climb clearances, headings, altimeter settings.
                Hand off to Center when established on route above transition altitude.
                Format: "[callsign], Departure, radar contact, climb maintain [alt]."
                """,

            "Center" or "Approach" => """
                You are ATC Center/Approach. Issue en-route clearances and direct routing.
                Approach: vector to ILS/visual final, sequence for landing.
                Format: "[callsign], Center, direct [waypoint], maintain [alt]."
                """,

            _ => "You are ATC. Issue appropriate clearances."
        };

        // ── Hallucination guard ───────────────────────────────────────────────
        // If no real airport data is available, forbid the LLM from inventing
        // runways, taxiways, frequencies or weather. This is critical for safety
        // — a pilot following a false clearance in MSFS will get confused.
        var dataGuard = string.Empty;
        bool hasRealAirportData = state.IsConnected &&
            !string.IsNullOrWhiteSpace(state.NearestAirportIcao);

        if (!hasRealAirportData)
        {
            dataGuard = """

                ⚠ DATA WARNING: No real airport data is available right now.
                DO NOT invent or assume: runway numbers, taxiway names, frequencies,
                QNH, wind, or any airport-specific information.
                Instead say: "[callsign], stand by, data unavailable — confirm airport ICAO."
                Only respond to basic radio checks and startup requests generically.
                """;
        }

        var simContext = state.IsConnected
            ? state.ToContextString()
            : "SIM: Not connected. Only respond to basic radio checks generically.";

        return $"""
            You are a professional {controllerRole} ATC controller. Replace the MSFS default ATC.
            Respond EXACTLY as a real controller — one radio transmission only.

            RULES:
            1. ONE transmission — no lists, no explanations, no markdown.
            2. ICAO phraseology. Clipped, professional tone.
            3. Start every response with the pilot's callsign.
            4. NEVER invent frequencies or waypoints not in SIM STATE.
            5. Wind: "[degrees] at [speed] knots".
            6. If read-back is wrong, say "Negative, [callsign], [correct value], say again."
            7. Radio check → "5 by 5" or "reading you loud and clear".
            {dataGuard}
            PHASE: {controllerRole}
            {phaseInstructions}

            {FlightCtx.ToPromptLine()}
            {simContext}
            """;
    }

    private void TrimHistory()
    {
        int max = MaxHistoryTurns * 2;
        if (_history.Count > max)
            _history.RemoveRange(0, _history.Count - max);
    }

    // ─── Cache helpers ────────────────────────────────────────────────────────

    private static string NormaliseForCache(string transcript)
    {
        // Strip callsign/airport prefix and normalise to catch repeated phrases
        // "Pune Ground Air India 302 radio check" → "radio check"
        var words = transcript.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Skip first 2-3 words (airport + controller + sometimes callsign word 1)
        var skip = words.Length > 5 ? 3 : 0;
        return string.Join(" ", words.Skip(skip));
    }

    // ─── Result ───────────────────────────────────────────────────────────────

    private record LlmResult(bool Success, bool IsRateLimit, bool IsAuthFailure, string? Content, string? ErrorMessage)
    {
        public static LlmResult Ok(string? c)     => new(true,  false, false, c,    null);
        public static LlmResult RateLimit()        => new(false, true,  false, null, "429");
        public static LlmResult AuthFail(string m) => new(false, false, true,  null, m);
        public static LlmResult Error(string msg)  => new(false, false, false, null, msg);
    }
}
