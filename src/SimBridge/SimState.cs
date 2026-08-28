namespace MsfsAiAtc.SimBridge;

/// <summary>
/// Snapshot of all relevant simulator state used to ground every LLM call.
/// This is the single source of truth for "what is true right now in the sim."
/// </summary>
public class SimState
{
    // Aircraft position
    public double LatitudeDeg { get; set; }
    public double LongitudeDeg { get; set; }
    public double AltitudeMslFt { get; set; }
    public double AltitudeAglFt { get; set; }
    public double HeadingDegTrue { get; set; }
    public double GroundSpeedKts { get; set; }
    public bool OnGround { get; set; }

    // Radios
    public double Com1FreqMhz { get; set; }

    // Nearest airport facility
    public string? NearestAirportIcao { get; set; }
    public string? NearestAirportName { get; set; }
    public double NearestAirportDistanceNm { get; set; }
    public double NearestAirportElevationFt { get; set; }

    // Frequencies at nearest airport
    public double TowerFreqMhz { get; set; }
    public double GroundFreqMhz { get; set; }
    public double AtisFreqMhz { get; set; }

    // Runways (simplified: active runway name)
    public string? ActiveRunway { get; set; }
    public List<RunwayInfo> Runways { get; set; } = new();

    // Wind (from sim)
    public double WindSpeedKts { get; set; }
    public double WindDirectionDeg { get; set; }

    // Simulated time
    public DateTime SimTime { get; set; } = DateTime.UtcNow;

    // Connection health
    public bool IsConnected { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Serialises to a compact, structured text block injected into every LLM prompt.
    /// Only facts present here should ever be referenced by the ATC brain.
    /// </summary>
    public string ToContextString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== LIVE SIM STATE ===");
        sb.AppendLine($"Connected: {IsConnected}");
        if (!IsConnected)
        {
            sb.AppendLine("(No sim data available)");
            return sb.ToString();
        }
        sb.AppendLine($"Aircraft: LAT={LatitudeDeg:F4} LON={LongitudeDeg:F4} HDG={HeadingDegTrue:F0}°");
        sb.AppendLine($"Altitude: {AltitudeMslFt:F0} ft MSL / {AltitudeAglFt:F0} ft AGL");
        sb.AppendLine($"Ground Speed: {GroundSpeedKts:F0} kts | On Ground: {OnGround}");
        sb.AppendLine($"COM1: {Com1FreqMhz:F3} MHz");
        sb.AppendLine($"Wind: {WindDirectionDeg:F0}° @ {WindSpeedKts:F0} kts");
        sb.AppendLine();
        if (NearestAirportIcao != null)
        {
            sb.AppendLine($"Nearest Airport: {NearestAirportIcao} ({NearestAirportName}) — {NearestAirportDistanceNm:F1} nm away, elev {NearestAirportElevationFt:F0} ft");
            sb.AppendLine($"Frequencies — TWR: {FormatFreq(TowerFreqMhz)} GND: {FormatFreq(GroundFreqMhz)} ATIS: {FormatFreq(AtisFreqMhz)}");
            if (Runways.Count > 0)
            {
                sb.AppendLine($"Runways: {string.Join(", ", Runways.Select(r => r.Designation))}");
            }
            if (ActiveRunway != null)
                sb.AppendLine($"Active Runway: {ActiveRunway}");
        }
        sb.AppendLine($"Sim Time (UTC): {SimTime:HH:mm}");
        sb.AppendLine("=== END SIM STATE ===");
        return sb.ToString();
    }

    private static string FormatFreq(double freq) =>
        freq > 0 ? $"{freq:F3}" : "N/A";
}

public class RunwayInfo
{
    public string Designation { get; set; } = string.Empty;
    public double HeadingDeg { get; set; }
    public double LengthFt { get; set; }
}
