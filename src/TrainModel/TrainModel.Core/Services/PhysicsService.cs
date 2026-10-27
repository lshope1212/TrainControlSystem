using TrainModel.Core.Models;

namespace TrainModel.Core.Services;

/// <summary>
/// The train plant physics, implemented from the final-project model:
///
///     P = F v           (power -> tractive force)
///     F = m a           (Newton's second law; point-mass rigid body)
///     v_n = v_(n-1) + (T/2)(a_n + a_(n-1))   (trapezoidal integration)
///
/// Scope is deliberately limited to what the lecture specified: gravity (grade)
/// and steel-on-steel rolling friction. No aerodynamic or curve resistance.
///
/// EVERYTHING HERE IS METRIC (SI). The caller converts for display.
/// The single most important behaviour: given a power command, produce a correct
/// actual speed. Everything else layers on top of that.
/// </summary>
public class PhysicsService
{
    private const double Gravity = 9.80665;           // m/s^2
    // Rolling friction coefficient for steel wheel on steel rail. The lecture's
    // friction model is a resistive force proportional to the normal load; this
    // coefficient (dimensionless) is applied as mu * m * g, opposing motion.
    private const double RollingFrictionCoefficient = 0.002;

    // Below this speed we cannot use F = P / v (it diverges as v -> 0). While the
    // train is at/near rest we apply force at the acceleration limit instead, which
    // is what a real traction controller does on launch.
    private const double MinSpeedForPowerModel = 0.1;  // m/s

    /// <summary>
    /// Advance the train's kinematic state by one fixed timestep.
    /// Mutates <paramref name="train"/>'s velocity, acceleration, and position.
    /// </summary>
    /// <param name="train">The train whose state is being integrated (metric).</param>
    /// <param name="inputs">Resolved inputs for this tick (power, brakes, grade, faults).</param>
    /// <param name="dtSeconds">Fixed physics timestep T, seconds.</param>
    public void Step(Train train, TrainInputs inputs, double dtSeconds)
    {
        double mass = train.TotalMassKg;
        double v = train.VelocityMetersPerSecond;

        // ---- 1. Tractive force from the power command (P = F v) ----
        // Clamp the commanded power to the engine's maximum: the train can never
        // draw more than its rated power regardless of what is commanded.
        double powerW = Math.Clamp(inputs.PowerCommandWatts, 0.0, train.Specs.MaxPowerWatts);

        // Engine failure (= power failure) and emergency brake both zero the power.
        if (train.EngineFailed || inputs.EmergencyBrakeApplied)
        {
            powerW = 0.0;
        }

        // Tractive force always drives the train FORWARD (+). The engine does not
        // propel the train backwards; it either drives forward or (if underpowered on
        // a grade) lets gravity win. Use the forward speed magnitude for P = F v, with
        // a launch regime near rest to avoid the singularity as v -> 0.
        double maxTractive = mass * train.Specs.MaxAccelMetersPerSecondSquared;
        double tractiveForce;
        if (v < MinSpeedForPowerModel)
        {
            // At/near rest or rolling backward: commanded power pushes forward at up to
            // the service acceleration limit (this is what lets power arrest a rollback
            // and start climbing again).
            tractiveForce = powerW > 0.0 ? maxTractive : 0.0;
        }
        else
        {
            tractiveForce = Math.Min(powerW / v, maxTractive);
        }

        // ---- 2. Grade force ----
        // Component of gravity along the track. Positive grade (uphill) always pulls
        // the train in the negative (backward) direction, regardless of motion.
        double gradeFraction = inputs.GradePercent / 100.0;
        double theta = Math.Atan(gradeFraction);
        double gradeForce = mass * Gravity * Math.Sin(theta); // >0 uphill: pulls backward

        // ---- 3. Resistive forces that OPPOSE THE DIRECTION OF MOTION ----
        // Rolling friction and braking both act against whichever way the train moves.
        // Their sign therefore follows the sign of the current velocity. (At true rest
        // they contribute nothing, so a powerless train on a grade begins to roll.)
        double dir = v > 0.0 ? 1.0 : (v < 0.0 ? -1.0 : 0.0);

        double frictionMag = RollingFrictionCoefficient * mass * Gravity;

        // Brake failure disables ONLY the service brake; the emergency brake always works.
        double brakeDecel = 0.0;
        bool emergency = inputs.EmergencyBrakeApplied || train.EmergencyBrakeEngaged;
        if (emergency)
        {
            brakeDecel = train.Specs.EmergencyDecelMetersPerSecondSquared;
        }
        else if (inputs.ServiceBrakeApplied && !train.BrakeFailed)
        {
            brakeDecel = train.Specs.ServiceDecelMetersPerSecondSquared;
        }
        double brakeMag = mass * brakeDecel;

        // Opposing forces act in -dir (against motion).
        double opposingForce = dir * (frictionMag + brakeMag);

        // ---- 4. Net force -> acceleration (F = m a) ----
        // Forward tractive, minus the backward grade pull, minus motion-opposing
        // friction/braking. Net force may be negative, producing backward acceleration.
        double netForce = tractiveForce - gradeForce - opposingForce;
        double a = netForce / mass;

        // ---- 5. Trapezoidal integration: v_n = v_(n-1) + (T/2)(a_n + a_(n-1)) ----
        double aPrev = train.PreviousAccelerationMetersPerSecondSquared;
        double newV = v + (dtSeconds / 2.0) * (a + aPrev);

        // Friction and braking oppose motion but can never, by themselves, push the
        // train in the opposite direction — they can only bring it to rest and hold it.
        // When velocity crosses zero this tick, decide whether the train SETTLES at rest
        // or continues into the new direction. It continues only if the direction-
        // independent drive (tractive forward, grade pull) is strong enough to overcome
        // the maximum resistive hold (friction + brake) in the new direction; otherwise
        // the resistance arrests it at zero. This makes braking (and friction) correctly
        // resist motion in BOTH directions — e.g., brakes applied while rolling backward
        // down a slope bring the train to rest instead of accelerating it.
        double resistiveHold = frictionMag + brakeMag;   // max force resistance can exert at rest
        if (v > 0.0 && newV < 0.0)
        {
            // Drive that would push the train BACKWARD past zero: grade minus tractive.
            double backwardDrive = gradeForce - tractiveForce;
            if (backwardDrive <= resistiveHold) { newV = 0.0; a = 0.0; }
        }
        else if (v < 0.0 && newV > 0.0)
        {
            // Drive that would push the train FORWARD past zero: tractive minus grade.
            double forwardDrive = tractiveForce - gradeForce;
            if (forwardDrive <= resistiveHold) { newV = 0.0; a = 0.0; }
        }

        // ---- Stationary hold ----
        // When the train is essentially at rest and the resistive hold (brake + friction)
        // is enough to counter the net drive (|tractive - grade|), the train is being HELD
        // in place — pin it to exactly zero. Without this, a braked train stopped on a
        // grade jitters between 0 and a small value each tick (grade nudges it off zero,
        // the brake snaps it back, repeat). The hold applies only when stopped, so it does
        // not prevent a legitimate launch (which commands enough tractive force to exceed
        // the hold) or a runaway when the brake cannot hold the grade.
        const double RestEpsilon = 0.15; // m/s (~0.3 mph)
        if (Math.Abs(newV) < RestEpsilon && Math.Abs(tractiveForce - gradeForce) <= resistiveHold)
        {
            newV = 0.0;
            a = 0.0;
        }

        // ---- 6. Vehicle maximum speed cap (physical, power-driven only) ----
        // The motors cannot push the train past its rated maximum speed. This is a
        // PHYSICAL limit on traction, so it is enforced only while the train is being
        // driven by power. It is NOT a commanded-speed clamp: regulating to the
        // commanded speed is the Train Controller's job (it manages the power command),
        // so the plant does not clamp to commanded speed here.
        //
        // Gravity is exempt: on a downgrade the train may legitimately OVERSPEED past
        // its rated maximum (real runaway/overspeed behavior). We therefore only cap
        // when the overspeed is being produced by traction, not by gravity. Gravity is
        // "driving" when the grade pulls the train forward (downhill: gradeForce < 0).
        double maxSpeed = train.Specs.MaxSpeedMetersPerSecond;
        bool gravityDrivenForward = gradeForce < 0.0;   // downhill pulls the train forward
        if (newV > maxSpeed && !gravityDrivenForward)
        {
            newV = maxSpeed;
            a = 0.0;
        }

        // ---- 6. Integrate position (trapezoidal on velocity) ----
        double newPosition = train.PositionMeters + (dtSeconds / 2.0) * (newV + v);

        // ---- 7. Commit state ----
        train.PreviousAccelerationMetersPerSecondSquared = a;
        train.AccelerationMetersPerSecondSquared = a;
        train.VelocityMetersPerSecond = newV;
        train.PositionMeters = newPosition;
    }
}
