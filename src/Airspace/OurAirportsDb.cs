using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Reads the OurAirports public dataset (airports.csv + runways.csv) to provide
/// worldwide airport and runway data without any BGL file parsing.
///
/// Why: MSFS 2020 Official BGLs use Asobo's proprietary binary format, which is
/// completely different from the FSX BGL format our parser expects. This gives
/// us 0 airports from 6000+ files. OurAirports is the clean solution:
///   - 80k+ airports worldwide
///   - Free public domain dataset (ourairports.com)
///   - Downloaded once by SETUP.bat (~5MB total)
///   - Loaded in under 2 seconds
///
/// Usage:
///   1. SETUP.bat downloads airports.csv and runways.csv to data\
///   2. App loads this DB on startup
///   3. When SimConnect gives lat/lon, call NearestTo() to find the airport
/// </summary>
public class OurAirportsDb
{
    private readonly ILogger<OurAirportsDb> _logger;
    private readonly Dictionary<string, OurAirport> _byIcao = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OurAirport> _all = new();

    public bool IsLoaded { get; private set; }
    public int AirportCount => _all.Count;

    public OurAirportsDb(ILogger<OurAirportsDb> logger) => _logger = logger;

    /// <summary>Load from CSV files downloaded by SETUP.bat.</summary>
    public void Load(string airportsCsvPath, string runwaysCsvPath)
    {
        if (!File.Exists(airportsCsvPath))
        {
            _logger.LogWarning(
                "OurAirports airports.csv not found at {Path}. " +
                "Run 'SETUP (Run This First).bat' to download it.", airportsCsvPath);
            return;
        }

        try
        {
            LoadAirports(airportsCsvPath);
            if (File.Exists(runwaysCsvPath))
                LoadRunways(runwaysCsvPath);

            IsLoaded = _all.Count > 0;
            _logger.LogInformation("OurAirports DB loaded: {Count} airports ({Rwys} with runways)",
                _all.Count, _all.Count(a => a.Runways.Count > 0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("OurAirports DB load failed: {Err}", ex.Message);
        }
    }

    // ── CSV loaders ────────────────────────────────────────────────────────────

    private void LoadAirports(string path)
    {
        // airports.csv columns:
        // id, ident, type, name, latitude_deg, longitude_deg, elevation_ft,
        // continent, iso_country, iso_region, municipality, scheduled_service,
        // gps_code, iata_code, local_code, home_link, wikipedia_link, keywords
        bool first = true;
        foreach (var row in ReadCsvRows(path))
        {
            if (first) { first = false; continue; }
            if (row.Length < 7) continue;

            var icao = Unquote(row[1]);
            var type = Unquote(row[2]);

            if (string.IsNullOrWhiteSpace(icao) || icao.Length < 3) continue;
            if (type == "closed") continue;

            if (!TryDouble(row[4], out var lat)) continue;
            if (!TryDouble(row[5], out var lon)) continue;
            TryDouble(row[6], out var elev);

            var airport = new OurAirport
            {
                Icao     = icao.ToUpperInvariant(),
                Name     = Unquote(row[3]),
                LatDeg   = lat,
                LonDeg   = lon,
                ElevFt   = elev,
            };

            _byIcao[airport.Icao] = airport;
            _all.Add(airport);
        }
    }

    private void LoadRunways(string path)
    {
        // runways.csv columns (0-indexed):
        // 0=id, 1=airport_ref, 2=airport_ident, 3=length_ft, 4=width_ft, 5=surface,
        // 6=lighted, 7=closed,
        // 8=le_ident, 9=le_lat, 10=le_lon, 11=le_elev, 12=le_heading, 13=le_displaced,
        // 14=he_ident, 15=he_lat, 16=he_lon, 17=he_elev, 18=he_heading, 19=he_displaced
        bool first = true;
        foreach (var row in ReadCsvRows(path))
        {
            if (first) { first = false; continue; }
            if (row.Length < 14) continue;

            var closedStr = Unquote(row[7]);
            if (closedStr == "1") continue;

            var airportIcao = Unquote(row[2]).ToUpperInvariant();
            if (!_byIcao.TryGetValue(airportIcao, out var airport)) continue;

            TryDouble(row[3], out var length);
            TryDouble(row[12], out var leHdg);

            var leIdent = Unquote(row[8]);
            var heIdent = row.Length > 14 ? Unquote(row[14]) : string.Empty;
            double heHdg = (leHdg + 180) % 360;

            if (!string.IsNullOrWhiteSpace(leIdent))
                airport.Runways.Add(new OurRunway(leIdent, length, leHdg));
            if (!string.IsNullOrWhiteSpace(heIdent))
                airport.Runways.Add(new OurRunway(heIdent, length, heHdg));
        }
    }

    // ── Public query API ───────────────────────────────────────────────────────

    /// <summary>Look up airport by exact ICAO code (case-insensitive).</summary>
    public OurAirport? Lookup(string icao) =>
        string.IsNullOrWhiteSpace(icao) ? null :
        _byIcao.TryGetValue(icao.Trim().ToUpperInvariant(), out var a) ? a : null;

    /// <summary>
    /// Find the nearest airport within maxDistNm nautical miles of the given position.
    /// Uses a bounding-box pre-filter for performance (typically &lt;1ms even with 80k airports).
    /// </summary>
    public OurAirport? NearestTo(double lat, double lon, double maxDistNm = 25)
    {
        if (!IsLoaded || _all.Count == 0) return null;

        // Bounding-box pre-filter: 1 degree lat ≈ 60nm, lon correction for latitude
        double latDelta = maxDistNm / 60.0;
        double lonDelta = maxDistNm / Math.Max(1.0, 60.0 * Math.Cos(lat * Math.PI / 180.0));

        OurAirport? best = null;
        double bestDist = maxDistNm;

        foreach (var a in _all)
        {
            // Fast bounding box check first
            if (Math.Abs(a.LatDeg - lat) > latDelta) continue;
            if (Math.Abs(a.LonDeg - lon) > lonDelta) continue;

            // Full haversine for candidates inside the bounding box
            var d = HaversineNm(lat, lon, a.LatDeg, a.LonDeg);
            if (d < bestDist) { bestDist = d; best = a; }
        }

        return best;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in nm
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static IEnumerable<string[]> ReadCsvRows(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return SplitCsvLine(line);
        }
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts  = new List<string>();
        var cur    = new System.Text.StringBuilder();
        bool inQ   = false;
        foreach (char c in line)
        {
            if (c == '"')       inQ = !inQ;
            else if (c == ',' && !inQ) { parts.Add(cur.ToString()); cur.Clear(); }
            else                cur.Append(c);
        }
        parts.Add(cur.ToString());
        return parts.ToArray();
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static bool TryDouble(string s, out double value) =>
        double.TryParse(Unquote(s), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

// ── Data models ────────────────────────────────────────────────────────────────

public class OurAirport
{
    public string       Icao    { get; init; } = string.Empty;
    public string       Name    { get; init; } = string.Empty;
    public double       LatDeg  { get; init; }
    public double       LonDeg  { get; init; }
    public double       ElevFt  { get; init; }
    public List<OurRunway> Runways { get; } = new();

    /// <summary>Compact layout string for the LLM prompt.</summary>
    public string ToLayoutString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Airport {Icao} ({Name})");
        if (ElevFt > 0) sb.Append($", elev {ElevFt:F0}ft");
        if (Runways.Count > 0)
        {
            var rwys = Runways.Select(r => $"{r.Designation}({r.LengthFt:F0}ft/{r.HeadingDeg:F0}°)");
            sb.Append($" | Runways: {string.Join(", ", rwys)}");
        }
        return sb.ToString();
    }
}

public record OurRunway(string Designation, double LengthFt, double HeadingDeg);
