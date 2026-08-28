using Microsoft.Extensions.Logging;
using MsfsAiAtc.SimBridge;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Phase 3: Airspace context service.
///
/// Combines three data sources to produce a rich context string for the LLM:
///   1. BGL Airport Database (taxiways, runways, parking) — from parsed MSFS game files
///   2. Active Flight Plan (.pln XML) — from MSFS LocalState folder
///   3. SimState (position, frequencies, wind) — live from SimConnect
///
/// This class is the single integration point wired into AtcBrainService.
/// </summary>
public class AirspaceLookup
{
    private readonly ILogger<AirspaceLookup> _logger;
    private readonly AirportCache? _airportCache;
    private readonly FlightPlanReader? _flightPlanReader;

    // Flight plan is cached and refreshed every 60 s in case the user loads a new plan
    private ActiveFlightPlan? _cachedPlan;
    private DateTime _planLastRead = DateTime.MinValue;
    private const int PlanRefreshIntervalSec = 60;

    public AirspaceLookup(
        ILogger<AirspaceLookup> logger,
        AirportCache? airportCache = null,
        FlightPlanReader? flightPlanReader = null)
    {
        _logger = logger;
        _airportCache = airportCache;
        _flightPlanReader = flightPlanReader;

        if (airportCache == null)
            _logger.LogInformation("AirspaceLookup: no airport cache (MSFS not found) — taxiway data unavailable");
        if (flightPlanReader == null)
            _logger.LogInformation("AirspaceLookup: no flight plan reader — IFR routing unavailable");
    }

    /// <summary>
    /// Builds the airspace context string to inject into the LLM system prompt.
    /// Call once per LLM request. Lightweight — data is already cached in memory.
    /// </summary>
    public string BuildContextString(SimState state)
    {
        var sb = new System.Text.StringBuilder();

        // ── Flight plan ────────────────────────────────────────────────────────
        var plan = GetCachedFlightPlan();
        if (plan != null && !string.IsNullOrWhiteSpace(plan.DepartureIcao))
        {
            sb.AppendLine($"[FLIGHT PLAN] {plan.ToContextString()}");
        }

        // ── Airport layout from BGL ────────────────────────────────────────────
        if (_airportCache?.IsLoaded == true && state.NearestAirportIcao != null)
        {
            var airport = _airportCache.Lookup(state.NearestAirportIcao);

            // If SimConnect's ICAO didn't match, try by position
            airport ??= _airportCache.NearestAirport(state.LatitudeDeg, state.LongitudeDeg, 5);

            if (airport != null)
            {
                sb.AppendLine($"[AIRPORT LAYOUT] {airport.ToLayoutString()}");

                // Override SimState runway list with BGL data (more accurate)
                if (airport.Runways.Count > 0 && state.Runways.Count == 0)
                {
                    state.Runways = airport.Runways.Select(r => new RunwayInfo
                    {
                        Designation = r.Designation,
                        HeadingDeg  = r.HeadingDeg,
                        LengthFt    = r.LengthFt,
                    }).ToList();
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the ARTCC sector for a given position (stub for now).
    /// Originally this was the only method — preserved for Phase 3+ IFR handoffs.
    /// </summary>
    public ArtccSector? GetSectorForPosition(double latDeg, double lonDeg) => null;

    // ─── Flight plan helpers ──────────────────────────────────────────────────

    private ActiveFlightPlan? GetCachedFlightPlan()
    {
        if (_flightPlanReader == null) return null;

        // Refresh plan if it's been more than PlanRefreshIntervalSec seconds
        if ((DateTime.UtcNow - _planLastRead).TotalSeconds > PlanRefreshIntervalSec)
        {
            try
            {
                _cachedPlan   = _flightPlanReader.ReadActivePlan();
                _planLastRead = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flight plan refresh failed");
            }
        }

        return _cachedPlan;
    }
}

public class ArtccSector
{
    public string Name       { get; set; } = string.Empty;
    public double FreqMhz    { get; set; }
    public string Identifier { get; set; } = string.Empty;
}
