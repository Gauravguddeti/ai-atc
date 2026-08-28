namespace MsfsAiAtc.Traffic;

/// <summary>
/// Phase 4: AI and multiplayer traffic tracker via SimConnect.
/// Stub implementation for Phase 1-3 — returns empty traffic list.
/// </summary>
public class TrafficTracker
{
    private readonly List<TrafficTarget> _targets = new();

    /// <summary>
    /// Returns nearby traffic sorted by distance.
    /// Returns empty list during Phases 1-3.
    /// </summary>
    public IReadOnlyList<TrafficTarget> GetNearbyTraffic() => _targets;

    /// <summary>
    /// Phase 4: called when SimConnect returns AI/multiplayer aircraft data.
    /// </summary>
    public void UpdateTraffic(IEnumerable<TrafficTarget> targets)
    {
        _targets.Clear();
        _targets.AddRange(targets);
    }

    /// <summary>
    /// Formats a brief traffic summary suitable for LLM context injection.
    /// Returns empty string when no traffic (Phases 1-3).
    /// </summary>
    public string GetContextSummary()
    {
        if (_targets.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== NEARBY TRAFFIC ===");
        foreach (var t in _targets.Take(5)) // limit to 5 for context size
            sb.AppendLine($"  {t.Callsign}: {t.DistanceNm:F1}nm, {t.AltitudeFt:F0}ft, hdg {t.HeadingDeg:F0}°");
        sb.AppendLine("=== END TRAFFIC ===");
        return sb.ToString();
    }
}

public class TrafficTarget
{
    public string Callsign { get; set; } = "UNKNOWN";
    public double LatitudeDeg { get; set; }
    public double LongitudeDeg { get; set; }
    public double AltitudeFt { get; set; }
    public double HeadingDeg { get; set; }
    public double GroundSpeedKts { get; set; }
    public double DistanceNm { get; set; }
    public bool IsOnGround { get; set; }
}
