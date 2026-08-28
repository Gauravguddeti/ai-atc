using Microsoft.Extensions.Logging;

namespace MsfsAiAtc.Traffic;

/// <summary>
/// Phase 4: Tracks FSLTL-injected and default MSFS AI traffic via SimConnect.
///
/// SimConnect periodically calls RequestDataOnSimObjectType for SIMCONNECT_SIMOBJECT_TYPE_AIRCRAFT.
/// The bridge invokes UpdateTraffic() with a fresh list every ~5 seconds.
///
/// Only aircraft within 15 NM and below FL180 (18 000 ft) are included to keep
/// the LLM context small and focused on traffic that actually affects the pilot.
///
/// Context injected to LLM:
///   [TRAFFIC] Boeing 737 (AA123) on final RWY25L, 3.2nm, 800ft, on ground: false
///   [TRAFFIC] Cessna 172 (N631UA) taxiing, 0.3nm, 234ft, on ground: true
/// </summary>
public class TrafficTracker
{
    private readonly ILogger<TrafficTracker> _logger;
    private readonly List<TrafficTarget> _targets = new();
    private readonly object _lock = new();

    private const double MaxRadiusNm   = 15.0;
    private const double MaxAltitudeFt = 18_000;
    private const int    MaxTargets    = 8; // LLM context budget

    public TrafficTracker(ILogger<TrafficTracker> logger)
    {
        _logger = logger;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public IReadOnlyList<TrafficTarget> GetNearbyTraffic()
    {
        lock (_lock) return _targets.ToList();
    }

    /// <summary>
    /// Called by SimConnectBridge when fresh AI object data arrives.
    /// Filters by radius/altitude and sorts by distance.
    /// </summary>
    public void UpdateFromSimConnect(
        IEnumerable<TrafficObjectData> rawData,
        double ownLatDeg, double ownLonDeg, double ownAltFt)
    {
        var fresh = new List<TrafficTarget>();

        foreach (var d in rawData)
        {
            if (d.Latitude == 0 && d.Longitude == 0) continue;

            double distNm = HaversineNm(ownLatDeg, ownLonDeg, d.Latitude, d.Longitude);
            if (distNm > MaxRadiusNm) continue;
            if (d.AltitudeMsl > MaxAltitudeFt) continue;

            // Build a readable callsign: prefer ATC ID, fallback to airline+flight
            string callsign = BuildCallsign(d);

            fresh.Add(new TrafficTarget
            {
                Callsign      = callsign,
                ModelTitle    = d.Title.Length > 40 ? d.Title[..40] : d.Title,
                LatitudeDeg   = d.Latitude,
                LongitudeDeg  = d.Longitude,
                AltitudeFt    = d.AltitudeMsl,
                HeadingDeg    = d.HeadingTrue,
                GroundSpeedKts= d.GroundSpeed,
                IsOnGround    = d.SimOnGround > 0.5,
                DistanceNm    = distNm,
            });
        }

        var sorted = fresh
            .OrderBy(t => t.DistanceNm)
            .Take(MaxTargets)
            .ToList();

        lock (_lock)
        {
            _targets.Clear();
            _targets.AddRange(sorted);
        }

        if (sorted.Count > 0)
            _logger.LogDebug("Traffic update: {Count} targets within {Nm}nm", sorted.Count, MaxRadiusNm);
    }

    // Keep old stub API working
    public void UpdateTraffic(IEnumerable<TrafficTarget> targets)
    {
        lock (_lock)
        {
            _targets.Clear();
            _targets.AddRange(targets);
        }
    }

    /// <summary>
    /// Compact text block injected into the LLM system prompt.
    /// Empty string when no traffic — doesn't waste tokens.
    /// </summary>
    public string GetContextSummary()
    {
        List<TrafficTarget> snapshot;
        lock (_lock) snapshot = _targets.ToList();

        if (snapshot.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[NEARBY TRAFFIC — use this to give hold-short, sequencing, or traffic advisories]");

        foreach (var t in snapshot)
        {
            string phase = DeterminePhase(t);
            sb.AppendLine($"  {t.Callsign}: {phase}, {t.DistanceNm:F1}nm, {t.AltitudeFt:F0}ft, hdg {t.HeadingDeg:F0}°");
        }

        return sb.ToString();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildCallsign(TrafficObjectData d)
    {
        if (!string.IsNullOrWhiteSpace(d.AtcId) && d.AtcId.Trim() != "0")
            return d.AtcId.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(d.AtcAirline) && !string.IsNullOrWhiteSpace(d.AtcFlightNum))
            return $"{d.AtcAirline.Trim().ToUpperInvariant()}{d.AtcFlightNum.Trim()}";

        if (!string.IsNullOrWhiteSpace(d.AtcAirline))
            return d.AtcAirline.Trim().ToUpperInvariant();

        // Use model title as last resort
        var parts = d.Title.Split(' ');
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : d.Title;
    }

    private static string DeterminePhase(TrafficTarget t)
    {
        if (t.IsOnGround)
            return t.GroundSpeedKts > 5 ? "taxiing" : "stopped on ground";

        if (t.AltitudeFt < 3000 && t.GroundSpeedKts < 200)
            return t.GroundSpeedKts < 80 ? "on short final" : "on approach";

        if (t.AltitudeFt < 6000)
            return "departing / low altitude";

        return $"en-route at {t.AltitudeFt:F0}ft";
    }

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                    + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                    * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public class TrafficTarget
{
    public string Callsign       { get; set; } = "UNKNOWN";
    public string ModelTitle     { get; set; } = string.Empty;
    public double LatitudeDeg    { get; set; }
    public double LongitudeDeg   { get; set; }
    public double AltitudeFt     { get; set; }
    public double HeadingDeg     { get; set; }
    public double GroundSpeedKts { get; set; }
    public double DistanceNm     { get; set; }
    public bool   IsOnGround     { get; set; }
}
