using Microsoft.Extensions.Logging;
using MsfsAiAtc.SimBridge;

namespace MsfsAiAtc.Airspace;

/// <summary>
/// Phase 3: Airspace context service.
///
/// Combines data sources to produce a rich context string for the LLM:
///   1. OurAirports DB  — worldwide airport/runway data (replaces BGL parsing)
///   2. Flight Plan (.pln XML) — from MSFS LocalState folder
///   3. SimState (position, frequencies, wind) — live from SimConnect
///   4. BGL Airport Cache (optional legacy fallback, always empty for MSFS 2020)
/// </summary>
public class AirspaceLookup
{
    private readonly ILogger<AirspaceLookup> _logger;
    private readonly AirportCache? _airportCache;       // BGL-based (empty for MSFS 2020)
    private readonly FlightPlanReader? _flightPlanReader;
    private readonly OurAirportsDb? _ourAirports;       // PRIMARY airport data source

    // Flight plan is cached and refreshed every 60 s in case the user loads a new plan
    private ActiveFlightPlan? _cachedPlan;
    private DateTime _planLastRead = DateTime.MinValue;
    private const int PlanRefreshIntervalSec = 60;

    public AirspaceLookup(
        ILogger<AirspaceLookup> logger,
        AirportCache? airportCache = null,
        FlightPlanReader? flightPlanReader = null,
        OurAirportsDb? ourAirports = null)
    {
        _logger          = logger;
        _airportCache    = airportCache;
        _flightPlanReader = flightPlanReader;
        _ourAirports     = ourAirports;
    }

    /// <summary>
    /// Builds the airspace context string to inject into the LLM system prompt.
    /// Called once per PTT press. Lightweight — all data is already in memory.
    /// </summary>
    public string BuildContextString(SimState state)
    {
        var sb = new System.Text.StringBuilder();

        // ── Flight plan ────────────────────────────────────────────────────────────────────
        var plan = GetCachedFlightPlan();
        if (plan != null && !string.IsNullOrWhiteSpace(plan.DepartureIcao))
            sb.AppendLine($"[FLIGHT PLAN] {plan.ToContextString()}");

        // ── Airport layout ──────────────────────────────────────────────────────────────────
        // Step 1: Try to get airport from OurAirports DB (primary source)
        OurAirport? ourAirport = null;

        // Try by ICAO code first (from SimConnect sim vars)
        if (!string.IsNullOrWhiteSpace(state.NearestAirportIcao))
            ourAirport = _ourAirports?.Lookup(state.NearestAirportIcao);

        // Fallback: find by position if we have a valid lat/lon (SimConnect connected)
        if (ourAirport == null && state.IsConnected && state.LatitudeDeg != 0)
            ourAirport = _ourAirports?.NearestTo(state.LatitudeDeg, state.LongitudeDeg, 25);

        if (ourAirport != null)
        {
            // Update SimState airport ICAO if we found it by position
            if (string.IsNullOrWhiteSpace(state.NearestAirportIcao))
                state.NearestAirportIcao = ourAirport.Icao;

            sb.AppendLine($"[AIRPORT LAYOUT] {ourAirport.ToLayoutString()}");

            // Populate SimState runways from OurAirports if not already set
            if (ourAirport.Runways.Count > 0 && state.Runways.Count == 0)
            {
                state.Runways = ourAirport.Runways
                    .Select(r => new RunwayInfo { Designation = r.Designation, LengthFt = r.LengthFt, HeadingDeg = r.HeadingDeg })
                    .ToList();
            }
        }
        else if (_airportCache?.IsLoaded == true && state.NearestAirportIcao != null)
        {
            // Legacy fallback: BGL cache (empty for MSFS 2020, but kept for FSX/P3D users)
            var bglAirport = _airportCache.Lookup(state.NearestAirportIcao)
                          ?? _airportCache.NearestAirport(state.LatitudeDeg, state.LongitudeDeg, 5);

            if (bglAirport != null)
            {
                sb.AppendLine($"[AIRPORT LAYOUT] {bglAirport.ToLayoutString()}");
                if (bglAirport.Runways.Count > 0 && state.Runways.Count == 0)
                {
                    state.Runways = bglAirport.Runways.Select(r => new RunwayInfo
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
