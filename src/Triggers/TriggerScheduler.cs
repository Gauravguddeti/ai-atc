using Microsoft.Extensions.Logging;
using MsfsAiAtc.SimBridge;

namespace MsfsAiAtc.Triggers;

/// <summary>
/// Watches SimState continuously and fires proactive ATC transmission events
/// when real flight events occur — without waiting for a pilot PTT press.
///
/// Each trigger enqueues a message to the ATC brain which goes straight
/// through the TTS pipeline.
///
/// This is purely deterministic: it decides WHEN to speak, not WHAT to say
/// (the LLM's job is still just phraseology).
/// </summary>
public class TriggerScheduler : IDisposable
{
    private readonly ILogger<TriggerScheduler> _logger;
    private System.Threading.Timer? _pollTimer;
    private bool _disposed;

    // Trigger state tracking
    private bool _clearanceOffered;
    private bool _takeoffClearanceOffered;
    private bool _initialClimbInstructionGiven;
    private bool _levelOffInstructionGiven;
    private double _lastGroundSpeed;
    private bool _wasOnGround = true;
    private int _stationarySeconds;
#pragma warning disable CS0169
    private bool _enginesRunningDetected; // reserved for future SimVar-based engine detection
#pragma warning restore CS0169
    private double _assignedAltitudeFt = -1;


    // Fired when the scheduler wants ATC to speak unprompted
    public event Func<string, Task>? TriggerFired; // trigger label → pipeline handles the rest

    private SimState? _lastState;

    public TriggerScheduler(ILogger<TriggerScheduler> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _pollTimer = new System.Threading.Timer(Poll, null, 2000, 2000); // every 2 seconds
        _logger.LogInformation("Trigger scheduler started");
    }

    public void UpdateState(SimState state)
    {
        _lastState = state;
    }

    private void Poll(object? _)
    {
        var state = _lastState;
        if (state == null || !state.IsConnected) return;

        try
        {
            EvaluateTriggers(state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Trigger poll error: {Msg}", ex.Message);
        }
    }

    private void EvaluateTriggers(SimState state)
    {
        // Track engine running (proxy: engines on = ground speed > 0 or engines on sim var)
        // Simple proxy: ground speed slightly above 0 = engines likely running
        bool enginesRunning = state.GroundSpeedKts > 0.5 || (!state.OnGround);

        // ── Trigger 1: Clearance delivery (stationary + engines running) ──
        if (state.OnGround && !_clearanceOffered)
        {
            if (state.GroundSpeedKts < 0.5)
                _stationarySeconds += 2;
            else
                _stationarySeconds = 0;

            if (_stationarySeconds >= 10 && enginesRunning && !_clearanceOffered)
            {
                _clearanceOffered = true;
                _logger.LogInformation("Trigger: Clearance delivery");
                FireTrigger("Aircraft is stationary with engines running at the gate. Issue an IFR/VFR clearance.");
            }
        }

        // ── Trigger 2: Takeoff roll threshold ──
        if (state.OnGround && !_takeoffClearanceOffered && state.GroundSpeedKts > 30)
        {
            _takeoffClearanceOffered = true;
            _logger.LogInformation("Trigger: Takeoff roll");
            FireTrigger("Aircraft is accelerating on the runway above 30 kts. Confirm takeoff clearance or issue wind/traffic advisory.");
        }

        // ── Trigger 3: Airborne + ~1000 ft AGL ──
        if (!state.OnGround && _wasOnGround)
        {
            _wasOnGround = false;
            _logger.LogInformation("Trigger: Aircraft airborne");
        }

        if (!state.OnGround && state.AltitudeAglFt > 900 && state.AltitudeAglFt < 1200
            && !_initialClimbInstructionGiven)
        {
            _initialClimbInstructionGiven = true;
            _logger.LogInformation("Trigger: Initial climb 1000 ft AGL");
            FireTrigger($"Aircraft has passed 1000 ft AGL, climbing. Issue initial climb instruction or frequency change to departure.");
        }

        // ── Trigger 4: Level-off at assigned altitude ──
        if (_assignedAltitudeFt > 0 && !_levelOffInstructionGiven)
        {
            double diff = Math.Abs(state.AltitudeMslFt - _assignedAltitudeFt);
            if (diff < 100 && state.GroundSpeedKts > 50)
            {
                _levelOffInstructionGiven = true;
                _logger.LogInformation("Trigger: Level off at {Alt}", _assignedAltitudeFt);
                FireTrigger($"Aircraft has leveled off at assigned altitude {_assignedAltitudeFt:F0} ft. Issue onward clearance or enroute instruction.");
            }
        }

        // ── Reset on-ground tracking ──
        if (state.OnGround && !_wasOnGround)
        {
            _wasOnGround = true;
            // Reset airborne triggers for next flight
            _initialClimbInstructionGiven = false;
            _levelOffInstructionGiven = false;
            _takeoffClearanceOffered = false;
        }

        _lastGroundSpeed = state.GroundSpeedKts;
    }

    private void FireTrigger(string label)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (TriggerFired != null)
                    await TriggerFired(label);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trigger handler error");
            }
        });
    }

    /// <summary>
    /// Set the assigned cruise/clearance altitude for level-off trigger.
    /// </summary>
    public void SetAssignedAltitude(double altFt)
    {
        _assignedAltitudeFt = altFt;
        _levelOffInstructionGiven = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
    }
}
