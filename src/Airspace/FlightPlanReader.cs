using Microsoft.Extensions.Logging;
using System.IO;
using System.Xml;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Reads the active MSFS flight plan (.pln XML) from the simulator's LocalState folder.
///
/// MSFS auto-saves the World Map flight plan to:
///   {LocalState}\flightplans\*.pln  (most recently modified = active plan)
///
/// When the user files the plan on the World Map and again in the cockpit,
/// both writes go to the same file — so we always read the latest one.
/// </summary>
public class FlightPlanReader
{
    private readonly ILogger<FlightPlanReader> _logger;
    private readonly string _localStateFolder;

    public FlightPlanReader(ILogger<FlightPlanReader> logger, string localStateFolder)
    {
        _logger = logger;
        _localStateFolder = localStateFolder;
    }

    /// <summary>
    /// Reads and parses the most recently modified .pln file in LocalState.
    /// Returns null if no plan is found or the folder doesn't exist.
    /// </summary>
    public ActiveFlightPlan? ReadActivePlan()
    {
        try
        {
            var plansDir = Path.Combine(_localStateFolder, "FlightPlans");
            if (!Directory.Exists(plansDir))
            {
                // MSFS 2020 sometimes saves directly in LocalState
                plansDir = _localStateFolder;
            }

            if (!Directory.Exists(plansDir)) return null;

            var plnFiles = Directory
                .GetFiles(plansDir, "*.pln", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (plnFiles.Count == 0)
            {
                _logger.LogDebug("No .pln flight plan files found in {Dir}", plansDir);
                return null;
            }

            var latestFile = plnFiles[0];
            _logger.LogInformation("Reading flight plan: {File}", latestFile);
            return ParsePln(latestFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read flight plan");
            return null;
        }
    }

    // ─── Parser ───────────────────────────────────────────────────────────────

    private ActiveFlightPlan? ParsePln(string filePath)
    {
        var doc = new XmlDocument();
        doc.Load(filePath);

        var ns = new XmlNamespaceManager(doc.NameTable);
        var fp = doc.SelectSingleNode("//FlightPlan.FlightPlan");
        if (fp == null) fp = doc.SelectSingleNode("//*[local-name()='FlightPlan.FlightPlan']");
        if (fp == null)
        {
            _logger.LogWarning("Could not find FlightPlan root element in {File}", filePath);
            return null;
        }

        string? Get(string tag)
        {
            var node = fp.SelectSingleNode(tag) ?? fp.SelectSingleNode($"*[local-name()='{tag}']");
            return node?.InnerText?.Trim();
        }

        var plan = new ActiveFlightPlan
        {
            Title           = Get("Title") ?? "Unknown",
            Type            = Get("FPType") ?? "VFR",
            DepartureIcao   = Get("DepartureID") ?? string.Empty,
            DestinationIcao = Get("DestinationID") ?? string.Empty,
            DepartureName   = Get("DepartureName") ?? string.Empty,
            DestinationName = Get("DestinationName") ?? string.Empty,
            CruiseAltitudeFt = ParseDouble(Get("CruisingAlt")),
            DepartureRunway = Get("DeparturePosition") ?? string.Empty,
        };

        // Parse waypoints
        var waypoints = fp.SelectNodes("ATCWaypoint") ?? fp.SelectNodes("*[local-name()='ATCWaypoint']");
        if (waypoints != null)
        {
            foreach (XmlNode wp in waypoints)
            {
                var id   = wp.Attributes?["id"]?.Value ?? string.Empty;
                var type = wp.SelectSingleNode("ATCWaypointType")?.InnerText?.Trim()
                        ?? wp.SelectSingleNode("*[local-name()='ATCWaypointType']")?.InnerText?.Trim()
                        ?? "fix";

                if (!string.IsNullOrWhiteSpace(id))
                    plan.Waypoints.Add(new FlightPlanWaypoint { Id = id, Type = type });
            }
        }

        _logger.LogInformation(
            "Flight plan: {Dep} → {Dest} ({Type}), cruise {Alt:F0}ft, {Wpts} waypoints",
            plan.DepartureIcao, plan.DestinationIcao, plan.Type,
            plan.CruiseAltitudeFt, plan.Waypoints.Count);

        return plan;
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// ─── Data models ──────────────────────────────────────────────────────────────

public class ActiveFlightPlan
{
    public string Title           { get; set; } = string.Empty;
    public string Type            { get; set; } = "VFR";   // IFR / VFR / DVFR
    public string DepartureIcao   { get; set; } = string.Empty;
    public string DestinationIcao { get; set; } = string.Empty;
    public string DepartureName   { get; set; } = string.Empty;
    public string DestinationName { get; set; } = string.Empty;
    public string DepartureRunway { get; set; } = string.Empty;
    public double CruiseAltitudeFt{ get; set; }
    public List<FlightPlanWaypoint> Waypoints { get; set; } = new();

    /// <summary>
    /// Compact string injected into the LLM context.
    /// Example: "IFR KLAX→KSFO cruise 25000ft via SMO DARTS BUGGS"
    /// </summary>
    public string ToContextString()
    {
        var wptList = Waypoints
            .Where(w => w.Type is "Intersection" or "VOR" or "NDB" or "RNAV")
            .Select(w => w.Id)
            .Take(8); // cap at 8 for token budget

        var via = wptList.Any() ? $" via {string.Join(" ", wptList)}" : string.Empty;
        var alt  = CruiseAltitudeFt > 0 ? $" cruise {CruiseAltitudeFt:F0}ft" : string.Empty;

        return $"{Type} {DepartureIcao}→{DestinationIcao}{alt}{via}";
    }
}

public class FlightPlanWaypoint
{
    public string Id   { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
