using MsfsAiAtc.SimBridge;

namespace MsfsAiAtc.Handoff;

/// <summary>
/// ATC controller phase / identity — who is currently responsible for the aircraft.
/// </summary>
public enum ControllerPhase
{
    /// <summary>Before engine start / at gate</summary>
    ClearanceDelivery,
    /// <summary>Taxiing on ground</summary>
    Ground,
    /// <summary>Holding short, on runway, or in pattern</summary>
    Tower,
    /// <summary>Departed, below ~18,000 ft, within ~50 nm</summary>
    Departure,
    /// <summary>Enroute, above FL180 or far from departure airport (Phase 3+)</summary>
    Center,
    /// <summary>Descending toward destination (Phase 3+)</summary>
    Approach,
}

/// <summary>
/// Determines which ATC controller is currently responsible based on SimState.
/// This is purely deterministic logic — the LLM has no input here.
/// </summary>
public class HandoffStateMachine
{
    private ControllerPhase _phase = ControllerPhase.Ground;
    private double _departureAirportLat;
    private double _departureAirportLon;
    private bool _hasDepartureAirport;

    public ControllerPhase CurrentPhase => _phase;
    public event Action<ControllerPhase, ControllerPhase>? PhaseChanged; // (from, to)

    public string ControllerRoleLabel => _phase switch
    {
        ControllerPhase.ClearanceDelivery => "Clearance Delivery",
        ControllerPhase.Ground => "Ground",
        ControllerPhase.Tower => "Tower",
        ControllerPhase.Departure => "Departure",
        ControllerPhase.Center => "Center",
        ControllerPhase.Approach => "Approach",
        _ => "ATC"
    };

    /// <summary>
    /// Called on every SimState update. Evaluates transitions.
    /// </summary>
    public void Update(SimState state)
    {
        if (!state.IsConnected) return;

        // Record departure airport position (first time we see an airport while on ground)
        if (!_hasDepartureAirport && state.OnGround && state.NearestAirportIcao != null)
        {
            _departureAirportLat = state.LatitudeDeg;
            _departureAirportLon = state.LongitudeDeg;
            _hasDepartureAirport = true;
        }

        var next = DeterminePhase(state);
        if (next != _phase)
        {
            var prev = _phase;
            _phase = next;
            PhaseChanged?.Invoke(prev, next);
        }
    }

    private ControllerPhase DeterminePhase(SimState state)
    {
        // On ground
        if (state.OnGround)
        {
            if (state.GroundSpeedKts < 1.0)
                return ControllerPhase.ClearanceDelivery;
            return ControllerPhase.Ground;
        }

        // Airborne
        double agl = state.AltitudeAglFt;
        double distFromDep = _hasDepartureAirport
            ? HaversineNm(state.LatitudeDeg, state.LongitudeDeg, _departureAirportLat, _departureAirportLon)
            : 0;

        if (agl < 200) return ControllerPhase.Tower; // just lifted off / pattern
        if (agl < 3000 || distFromDep < 10) return ControllerPhase.Tower;
        if (distFromDep < 50 || state.AltitudeMslFt < 18000) return ControllerPhase.Departure;

        return ControllerPhase.Center; // Phase 3+ fully populates this
    }

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in NM
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
