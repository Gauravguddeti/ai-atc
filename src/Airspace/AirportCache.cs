using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Caches BGL-parsed airport data to a local JSON file so subsequent app launches
/// are instant (no re-scanning). Cache is invalidated automatically if the Community
/// folder modification time changes (new scenery installed).
///
/// On first run OR when new scenery is detected: shows a progress bar in the overlay
/// and scans in the background. Lookup returns null until the scan completes.
/// On subsequent runs: loads from cache in &lt;1 second.
/// </summary>
public class AirportCache
{
    private readonly ILogger<AirportCache> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _cacheFilePath;
    private readonly MsfsInstallPaths _paths;

    private Dictionary<string, BglAirportData> _airports = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    // Progress reporting to the overlay
    public event Action<string>? ScanProgressUpdate; // e.g. "Scanning: airport_klax.bgl"
    public event Action<int>? ScanComplete;           // arg = total airports found

    public AirportCache(ILogger<AirportCache> logger, ILoggerFactory loggerFactory, MsfsInstallPaths paths, string appDataDir)
    {
        _logger        = logger;
        _loggerFactory = loggerFactory;
        _paths         = paths;
        _cacheFilePath = Path.Combine(appDataDir, "airport_cache.json");
    }

    public bool IsLoaded => _loaded;

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the cache asynchronously. Loads from disk if fresh, rescans if stale.
    /// Designed to be called once at app startup in the background.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        await Task.Run(() => Initialise(ct), ct);
    }

    /// <summary>
    /// Returns airport data for the given ICAO, or null if not yet scanned / not found.
    /// </summary>
    public BglAirportData? Lookup(string icao)
    {
        if (!_loaded) return null;
        _airports.TryGetValue(icao.ToUpperInvariant(), out var result);
        return result;
    }

    /// <summary>
    /// Finds the nearest airport to given coordinates. Used as a fallback
    /// when SimConnect doesn't provide airport ID.
    /// </summary>
    public BglAirportData? NearestAirport(double latDeg, double lonDeg, double maxRadiusNm = 20)
    {
        if (!_loaded || _airports.Count == 0) return null;

        BglAirportData? best = null;
        double bestDist = double.MaxValue;

        foreach (var ap in _airports.Values)
        {
            double d = HaversineNm(latDeg, lonDeg, ap.LatitudeDeg, ap.LongitudeDeg);
            if (d < maxRadiusNm && d < bestDist)
            {
                bestDist = d;
                best = ap;
            }
        }
        return best;
    }

    // ─── Initialise / cache logic ─────────────────────────────────────────────

    private void Initialise(CancellationToken ct)
    {
        try
        {
            var communityModified = GetFolderLastModified(_paths.CommunityFolder);

            if (TryLoadCache(communityModified))
            {
                _logger.LogInformation("Airport cache loaded from disk ({Count} airports)", _airports.Count);
                _loaded = true;
                ScanComplete?.Invoke(_airports.Count);
                return;
            }

            // Cache is stale or missing — do a full BGL scan
            _logger.LogInformation("Airport cache is stale or missing — starting BGL scan...");
            ScanProgressUpdate?.Invoke("First-time setup: scanning airport data (30-60 sec)...");

            var parser = new BglAirportParser(_loggerFactory.CreateLogger<BglAirportParser>());
            var progress = new Progress<string>(fileName =>
            {
                ct.ThrowIfCancellationRequested();
                ScanProgressUpdate?.Invoke($"Scanning: {fileName}");
            });

            _airports = parser.ScanFolders(_paths.OfficialFolder, _paths.CommunityFolder, progress);
            _loaded = true;

            SaveCache(communityModified);
            ScanComplete?.Invoke(_airports.Count);
            _logger.LogInformation("BGL scan done — {Count} airports cached", _airports.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Airport cache initialisation failed — taxiway data unavailable");
            _loaded = true; // mark loaded so lookups return null gracefully
        }
    }

    // ─── Cache persistence ────────────────────────────────────────────────────

    private bool TryLoadCache(DateTime communityModified)
    {
        if (!File.Exists(_cacheFilePath)) return false;

        try
        {
            var cacheWritten = File.GetLastWriteTimeUtc(_cacheFilePath);

            // Cache is stale if Community folder is newer than the cache
            if (communityModified > cacheWritten)
            {
                _logger.LogInformation("Community folder changed — cache invalidated");
                return false;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, BglAirportData>>(json);

            if (loaded == null || loaded.Count == 0) return false;

            _airports = new Dictionary<string, BglAirportData>(loaded, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load airport cache — will rescan");
            return false;
        }
    }

    private void SaveCache(DateTime communityModified)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_airports, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_cacheFilePath, json);
            _logger.LogInformation("Airport cache saved to {Path}", _cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save airport cache");
        }
    }

    private static DateTime GetFolderLastModified(string folder)
    {
        if (!Directory.Exists(folder)) return DateTime.MinValue;
        try
        {
            return Directory
                .EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(f =>
                {
                    try { return File.GetLastWriteTimeUtc(f); }
                    catch { return DateTime.MinValue; }
                })
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch { return DateTime.MinValue; }
    }

    // ─── Haversine distance ───────────────────────────────────────────────────

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in nautical miles
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                    + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                    * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
