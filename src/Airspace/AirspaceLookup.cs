namespace MsfsAiAtc.Airspace;

/// <summary>
/// Phase 3: ARTCC/sector boundary lookup using point-in-polygon.
/// Stub implementation for now — will be populated when Phase 3 is active.
/// </summary>
public class AirspaceLookup
{
    /// <summary>
    /// Returns the ARTCC center name and frequency for a given lat/lon position.
    /// Returns null if no data is available (Phase 1 and 2 behavior).
    /// </summary>
    public ArtccSector? GetSectorForPosition(double latDeg, double lonDeg)
    {
        // Phase 3: implement point-in-polygon against FAA open airspace data
        return null;
    }
}

public class ArtccSector
{
    public string Name { get; set; } = string.Empty;
    public double FreqMhz { get; set; }
    public string Identifier { get; set; } = string.Empty;
}
