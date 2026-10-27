namespace TrainController.Abstractions.Inputs;

/// <summary>
/// Per-train Engineer tuning. Gains are in SI terms: the speed error fed to the PI law is
/// in m/s and the output is power in W, identically for Software and Hardware controllers.
/// </summary>
/// <remarks>
/// <para>
/// UNTUNED SAFE STARTUP PLACEHOLDER: the defaults (Kp = 1.0, Ki = 0.0) only give the
/// controller a defined, harmless starting point. They are NOT usable operating gains — in
/// SI terms Kp = 1 W per m/s produces only a few watts, so the train barely moves. The
/// Engineer must tune Kp/Ki per train. Nothing in the controllers or tests may treat the
/// placeholder as a realistic tuned value; see <see cref="IsUntunedStartupPlaceholder"/>.
/// </para>
/// <para>Only the lower bound (≥ 0, finite) is enforced; no project source defines an upper bound.</para>
/// </remarks>
public sealed record EngineerSettings
{
    /// <summary>Untuned startup placeholder, not an operating gain.</summary>
    public const double DefaultKp = 1.0;

    /// <summary>Untuned startup placeholder, not an operating gain.</summary>
    public const double DefaultKi = 0.0;

    public double Kp { get; init; } = DefaultKp;

    public double Ki { get; init; } = DefaultKi;

    public static EngineerSettings Default { get; } = new EngineerSettings();

    public bool IsValid => IsValidGain(Kp) && IsValidGain(Ki);

    /// <summary>
    /// True while the gains still equal the untuned startup placeholder. For UI warnings only;
    /// controllers apply whatever gains they are given and never special-case this.
    /// </summary>
    public bool IsUntunedStartupPlaceholder => Kp == DefaultKp && Ki == DefaultKi;

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
