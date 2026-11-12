namespace TrainController.Abstractions.Inputs;

/// <summary>
/// Per-train Engineer tuning. Gains are in SI terms: the speed error fed to the PI law is
/// in m/s and the output is power in W, identically for Software and Hardware controllers.
/// </summary>
/// <remarks>
/// <para>
/// STARTUP DEFAULTS (Kp = 100 000 W per m/s, Ki = 10 000 W per m/s per s) are first-estimate
/// operating gains, not a placeholder: with the 40.9 t empty train at about 15 m/s, Kp gives a
/// speed-error time constant of roughly m·v / Kp ≈ 6 s, and Ki (integral time ≈ 10 s) removes
/// the steady error needed to overcome rolling resistance. They are linearized estimates; the
/// Engineer can retune Kp/Ki per train at any time.
/// </para>
/// <para>Only the lower bound (≥ 0, finite) is enforced; no project source defines an upper bound.</para>
/// </remarks>
public sealed record EngineerSettings
{
    /// <summary>Startup proportional gain, W per (m/s). First-estimate operating value.</summary>
    public const double DefaultKp = 100_000.0;

    /// <summary>Startup integral gain, W per (m/s·s). First-estimate operating value.</summary>
    public const double DefaultKi = 10_000.0;

    public double Kp { get; init; } = DefaultKp;

    public double Ki { get; init; } = DefaultKi;

    public static EngineerSettings Default { get; } = new EngineerSettings();

    public bool IsValid => IsValidGain(Kp) && IsValidGain(Ki);

    public static bool IsValidGain(double gain) => double.IsFinite(gain) && gain >= 0.0;

    /// <exception cref="ArgumentOutOfRangeException">A gain is negative, NaN or infinite.</exception>
    public static EngineerSettings Create(double kp, double ki)
    {
        if (!IsValidGain(kp))
        {
            throw new ArgumentOutOfRangeException(nameof(kp), kp, "Kp must be a finite value >= 0.");
        }

        if (!IsValidGain(ki))
        {
            throw new ArgumentOutOfRangeException(nameof(ki), ki, "Ki must be a finite value >= 0.");
        }

        return new EngineerSettings { Kp = kp, Ki = ki };
    }
}
