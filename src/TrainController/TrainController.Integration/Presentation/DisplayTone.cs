namespace TrainController.Integration.Presentation;

/// <summary>
/// Semantic tone of a displayed value. Views map tones to colors in ONE place (the WPF theme),
/// so restyling never touches presentation logic.
/// </summary>
public enum DisplayTone
{
    /// <summary>Ordinary value, default text color.</summary>
    Neutral = 0,
    Muted,
    Good,
    Info,
    Warning,
    Danger
}
