namespace MsfsAiAtc.SimBridge;

/// <summary>
/// Snapshot of all relevant simulator state used to ground every LLM call.
/// This is the single source of truth for "what is true right now in the sim."
///
/// Phase 3 additions: AirspaceContext (flight plan + taxiway layout from BGL)
/// Phase 4 additions: TrafficContext (nearby FSLTL/AI aircraft)
/// </summary>
public class SimState
{
    // Aircraft position
    public double LatitudeDeg    { get; set; }
    public double LongitudeDeg   { get; set; }
    public double AltitudeMslFt  { get; set; }
    public double AltitudeAglFt  { get; set; }
    public double HeadingDegTrue { get; set; }
    public double GroundSpeedKts { get; set; }
    public bool   OnGround       { get; set; }

    // Radios
    public double Com1FreqMhz { get; set; }
    public double Com2FreqMhz { get; set; }

    // Aircraft identity (from SimConnect ATC SimVars — no guessing needed)
    public string? AircraftId           { get; set; }  // e.g. "VT-ABC"
    public string? AircraftAirline      { get; set; }  // e.g. "Air India"
    public string? AircraftFlightNumber { get; set; }  // e.g. "302"
    public string? AircraftType         { get; set; }  // e.g. "B738"

    /// <summary>Full ATC callsign built from airline + flight number, or just AircraftId.</summary>
    public string AtcCallsign =>
        !string.IsNullOrWhiteSpace(AircraftAirline) && !string.IsNullOrWhiteSpace(AircraftFlightNumber)
            ? $"{AircraftAirline} {AircraftFlightNumber}"
            : AircraftId ?? "unknown";

    // Nearest airport facility (from SimConnect or BGL)
    public string? NearestAirportIcao     { get; set; }
    public string? NearestAirportName     { get; set; }
    public double  NearestAirportDistanceNm { get; set; }
    public double  NearestAirportElevationFt{ get; set; }

    // Frequencies at nearest airport
    public double TowerFreqMhz  { get; set; }
    public double GroundFreqMhz { get; set; }
    public double AtisFreqMhz   { get; set; }

    // Runways (populated by SimConnect and/or BGL parser)
    public string?          ActiveRunway { get; set; }
    public List<RunwayInfo> Runways      { get; set; } = new();

    // Wind (from sim)
    public double WindSpeedKts     { get; set; }
    public double WindDirectionDeg { get; set; }

    // Simulated time
    public DateTime SimTime { get; set; } = DateTime.UtcNow;

    // Connection health
    public bool     IsConnected  { get; set; }
    public DateTime LastUpdated  { get; set; } = DateTime.MinValue;

    // GPS / flight plan destination
    public string? GpsDestinationIcao { get; set; }  // e.g. "VABB" (from GPS FLIGHT PLAN WP IDENT)

    // QNH / altimeter
    public int QnhHpa { get; set; } = 1013;

    // ── Phase 3: Airspace context (flight plan + BGL airport layout) ──────────
    /// <summary>
    /// Set by AirspaceLookup. Contains flight plan route and airport taxiway/runway layout
    /// parsed from MSFS BGL files. Injected into every LLM system prompt.
    /// </summary>
    public string AirspaceContext { get; set; } = string.Empty;

    // ── Phase 4: Traffic context (FSLTL injected + AI planes) ────────────────
    /// <summary>
    /// Set by TrafficTracker. Contains a brief list of nearby aircraft.
    /// Injected into the LLM prompt so ATC can give hold-short and traffic advisories.
    /// </summary>
    public string TrafficContext { get; set; } = string.Empty;

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

        sb.AppendLine($"Aircraft: {AtcCallsign} ({AircraftType ?? "unknown type"})");
        sb.AppendLine($"  Reg: {AircraftId ?? "unknown"} | Position: LAT={LatitudeDeg:F4} LON={LongitudeDeg:F4} HDG={HeadingDegTrue:F0}°");
        sb.AppendLine($"  Altitude: {AltitudeMslFt:F0} ft MSL / {AltitudeAglFt:F0} ft AGL");
        sb.AppendLine($"  Ground Speed: {GroundSpeedKts:F0} kts | On Ground: {OnGround}");
        sb.AppendLine($"  COM1: {Com1FreqMhz:F3} MHz | COM2: {Com2FreqMhz:F3} MHz");
        sb.AppendLine($"  Wind: {WindDirectionDeg:F0}° @ {WindSpeedKts:F0} kts");
        sb.AppendLine($"  QNH: {QnhHpa} hPa");

        if (!string.IsNullOrWhiteSpace(GpsDestinationIcao))
            sb.AppendLine($"  GPS Destination: {GpsDestinationIcao}");

        if (NearestAirportIcao != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Nearest Airport: {NearestAirportIcao} ({NearestAirportName}) — {NearestAirportDistanceNm:F1} nm away, elev {NearestAirportElevationFt:F0} ft");
            sb.AppendLine($"  Frequencies — TWR: {FormatFreq(TowerFreqMhz)} GND: {FormatFreq(GroundFreqMhz)} ATIS: {FormatFreq(AtisFreqMhz)}");
            if (Runways.Count > 0)
                sb.AppendLine($"  Runways: {string.Join(", ", Runways.Select(r => r.Designation))}");
            if (ActiveRunway != null)
                sb.AppendLine($"  Active Runway: {ActiveRunway}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("⚠ Nearest airport ICAO not yet resolved. " +
                "Use aircraft COM1 frequency and pilot-stated airport to determine location.");
            sb.AppendLine($"  COM1 tuned to: {Com1FreqMhz:F3} MHz (use this to infer current ATC freq)");
        }

        // Phase 3: Flight plan + airport layout
        if (!string.IsNullOrWhiteSpace(AirspaceContext))
        {
            sb.AppendLine();
            sb.Append(AirspaceContext);
        }

        // Phase 4: Traffic
        if (!string.IsNullOrWhiteSpace(TrafficContext))
        {
            sb.AppendLine();
            sb.Append(TrafficContext);
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
    public double HeadingDeg  { get; set; }
    public double LengthFt    { get; set; }
}
