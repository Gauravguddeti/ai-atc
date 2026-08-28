using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.RegularExpressions;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Automatically locates the MSFS 2020/2024 installation on any Windows PC.
///
/// Covers all 4 installation variants without any user config:
///   - MSFS 2020 Microsoft Store
///   - MSFS 2020 Steam
///   - MSFS 2024 Microsoft Store
///   - MSFS 2024 Steam
///
/// Reads InstalledPackagesPath from UserCfg.opt to find the correct
/// Community, Official, and LocalState folders — even if the user installed
/// MSFS to a custom drive or path.
/// </summary>
public class MsfsPathFinder
{
    private readonly ILogger<MsfsPathFinder> _logger;

    // Known UserCfg.opt locations — checked in priority order
    private static readonly (string label, string relativePath)[] _cfgCandidates =
    [
        ("MSFS 2024 Store",   @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
        ("MSFS 2020 Store",   @"Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
        ("MSFS 2024 Steam",   @"..\Roaming\Microsoft Flight Simulator 2024\UserCfg.opt"),
        ("MSFS 2020 Steam",   @"..\Roaming\Microsoft Flight Simulator\UserCfg.opt"),
    ];

    public MsfsInstallPaths? Paths { get; private set; }
    public bool Found => Paths != null;

    public MsfsPathFinder(ILogger<MsfsPathFinder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans known locations for UserCfg.opt and resolves all derived paths.
    /// Safe to call on a background thread.
    /// </summary>
    public MsfsInstallPaths? Discover()
    {
        // 1. Check for manual override in .env
        var manualPath = Environment.GetEnvironmentVariable("MSFS_PACKAGES_PATH");
        if (!string.IsNullOrWhiteSpace(manualPath) && Directory.Exists(manualPath))
        {
            _logger.LogInformation("Using manual MSFS packages path from .env: {Path}", manualPath);
            // In manual mode, we assume LocalState is a sibling to Community/Official
            // or inside the packages root.
            var paths = new MsfsInstallPaths
            {
                Label = "Manual Override",
                UserCfgOptPath = "Manual",
                PackagesRoot = manualPath,
                CommunityFolder = Path.Combine(manualPath, "Community"),
                OfficialFolder = FindOfficialFolder(manualPath),
                LocalStateFolder = FindLocalStateFolder(manualPath, manualPath) 
            };
            
            // If LocalState isn't found easily, default to Packages root
            if (!Directory.Exists(paths.LocalStateFolder))
                paths = paths with { LocalStateFolder = manualPath };

            Paths = paths;
            return paths;
        }

        // 2. Scan standard UserCfg.opt locations
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var (label, relative) in _cfgCandidates)
        {
            var cfgPath = Path.GetFullPath(Path.Combine(localAppData, relative));
            if (!File.Exists(cfgPath)) continue;

            _logger.LogInformation("Found {Label} config at: {Path}", label, cfgPath);

            var packagesRoot = ParseInstalledPackagesPath(cfgPath);
            if (packagesRoot == null)
            {
                _logger.LogWarning("Could not parse InstalledPackagesPath from {Path}", cfgPath);
                continue;
            }

            // LocalCache is always one level above the cfg file folder
            var localCacheDir = Path.GetDirectoryName(cfgPath)!;

            var paths = new MsfsInstallPaths
            {
                Label            = label,
                UserCfgOptPath   = cfgPath,
                PackagesRoot     = packagesRoot,
                CommunityFolder  = Path.Combine(packagesRoot, "Community"),
                OfficialFolder   = FindOfficialFolder(packagesRoot),
                LocalStateFolder = FindLocalStateFolder(localCacheDir, packagesRoot),
            };

            _logger.LogInformation("MSFS install resolved: {Root}", packagesRoot);
            _logger.LogInformation("  Community : {Path}", paths.CommunityFolder);
            _logger.LogInformation("  Official  : {Path}", paths.OfficialFolder);
            _logger.LogInformation("  LocalState: {Path}", paths.LocalStateFolder);

            Paths = paths;
            return paths;
        }

        _logger.LogWarning("MSFS installation not found — airport/taxiway data unavailable. " +
            "Is MSFS installed? Is it set to a standard location?");
        return null;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string? ParseInstalledPackagesPath(string cfgPath)
    {
        try
        {
            foreach (var line in File.ReadLines(cfgPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Format: InstalledPackagesPath "D:\MSFS Packages"
                var match = Regex.Match(trimmed, "\"([^\"]+)\"");
                if (match.Success)
                {
                    var raw = match.Groups[1].Value;
                    // Expand environment variables in case the path contains them
                    raw = Environment.ExpandEnvironmentVariables(raw);
                    if (Directory.Exists(raw)) return raw;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading UserCfg.opt");
        }
        return null;
    }

    private static string FindOfficialFolder(string packagesRoot)
    {
        // Could be "Official/OneStore" or "Official/Steam"
        var onestorePath = Path.Combine(packagesRoot, "Official", "OneStore");
        var steamPath    = Path.Combine(packagesRoot, "Official", "Steam");
        if (Directory.Exists(onestorePath)) return onestorePath;
        if (Directory.Exists(steamPath))    return steamPath;
        return Path.Combine(packagesRoot, "Official");
    }

    private static string FindLocalStateFolder(string localCacheDir, string packagesRoot)
    {
        // For Store version: LocalCache sits inside the package sandbox
        // LocalState is a sibling of LocalCache
        var sibling = Path.Combine(localCacheDir, "..", "LocalState");
        var resolved = Path.GetFullPath(sibling);
        if (Directory.Exists(resolved)) return resolved;

        // Steam fallback: LocalState usually doesn't exist separately
        return Path.Combine(packagesRoot, "LocalState");
    }
}

/// <summary>All MSFS filesystem paths resolved from a single UserCfg.opt.</summary>
public class MsfsInstallPaths
{
    public string Label           { get; init; } = string.Empty;
    public string UserCfgOptPath  { get; init; } = string.Empty;
    public string PackagesRoot    { get; init; } = string.Empty;
    public string CommunityFolder { get; init; } = string.Empty;
    public string OfficialFolder  { get; init; } = string.Empty;
    public string LocalStateFolder{ get; init; } = string.Empty;
}
