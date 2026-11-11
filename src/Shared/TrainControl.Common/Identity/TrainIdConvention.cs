using System.Globalization;

namespace TrainControl.Common.Identity;

/// <summary>
/// The system-wide train identifier convention, and the one translation boundary for module-local
/// train labels.
/// </summary>
/// <remarks>
/// <para>
/// The canonical train ID, the only form that crosses a module boundary in a shared-contract
/// message, is CTC's three-digit dispatch ID: "000" to "999". CTC assigns it when it builds a
/// schedule, so every later message about that train (movement requests, Track Model train
/// updates, Train Model / Train Controller traffic) uses the same string.
/// </para>
/// <para>
/// Modules may show other labels: CTC shows "Train 007" and the Train Controller fleet uses
/// "TRAIN-007". Those are presentation/fleet labels; a module translates them here, at its own
/// pipe boundary, rather than putting them on the wire. The translation keeps the number:
/// "TRAIN-007" is canonical "007". Anything else (e.g. a two-digit "01" demonstration label) is
/// not a canonical ID and is rejected rather than guessed.
/// </para>
/// </remarks>
public static class TrainIdConvention
{
    /// <summary>Digits in a canonical train ID.</summary>
    public const int Digits = 3;

    /// <summary>Highest number a canonical train ID can hold.</summary>
    public const int MaxNumber = 999;

    /// <summary>Prefix of the Train Controller's fleet labels, e.g. "TRAIN-007".</summary>
    public const string TrainControllerLabelPrefix = "TRAIN-";

    /// <summary>Prefix of CTC's dispatcher-facing labels, e.g. "Train 007".</summary>
    public const string DisplayLabelPrefix = "Train ";

    /// <summary>Formats a train number as its canonical ID, e.g. 7 -> "007".</summary>
    public static string Format(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxNumber);

        return number.ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="trainId"/> is exactly three ASCII digits.</summary>
    public static bool IsCanonical(string? trainId) =>
        trainId is { Length: Digits } && trainId.All(char.IsAsciiDigit);

    /// <summary>
    /// Translates a canonical ID, a Train Controller fleet label ("TRAIN-007", any case) or a CTC
    /// display label ("Train 007") into the canonical ID. Returns false for anything else.
    /// </summary>
    public static bool TryNormalize(string? label, out string canonicalId)
    {
        canonicalId = string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        string text = label.Trim();
        foreach (string prefix in new[] { TrainControllerLabelPrefix, DisplayLabelPrefix })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        if (!IsCanonical(text))
        {
            return false;
        }

        canonicalId = text;
        return true;
    }

    /// <summary>The Train Controller fleet label for a canonical ID, e.g. "007" -> "TRAIN-007".</summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalId"/> is not a canonical ID.</exception>
    public static string ToTrainControllerLabel(string canonicalId) => IsCanonical(canonicalId)
        ? TrainControllerLabelPrefix + canonicalId
        : throw new ArgumentException($"'{canonicalId}' is not a canonical three-digit train ID.", nameof(canonicalId));
}
