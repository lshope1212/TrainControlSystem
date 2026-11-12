using System.Globalization;

namespace TrainControl.Common.Identity;

/// <summary>
/// The system-wide train identifier convention, and the one translation boundary for module-local
/// train labels.
/// </summary>
/// <remarks>
/// <para>
/// The canonical train ID, the only form that crosses a module boundary in a shared-contract
/// message, is CTC's three-digit dispatch ID. Syntactically any three digits ("000" to "999") is
/// canonical (<see cref="IsCanonical"/>), but only the ten trains of the physical fleet,
/// "001" to "010", can be dispatched (<see cref="IsDispatchable"/>): those are the trains the
/// Train Controller controls (TRAIN-001 .. TRAIN-010; odd Software, even Hardware). CTC assigns
/// the ID when it builds a schedule, so every later message about that train (movement requests,
/// Track Model train updates, Train Model / Train Controller traffic) uses the same string.
/// Train IDs are unique across the whole system, not per line.
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

    /// <summary>Number of the first dispatchable fleet train, "001".</summary>
    public const int FirstDispatchableNumber = 1;

    /// <summary>Number of the last dispatchable fleet train, "010".</summary>
    public const int LastDispatchableNumber = 10;

    /// <summary>Size of the dispatchable fleet: ten trains.</summary>
    public const int DispatchableTrainCount = LastDispatchableNumber - FirstDispatchableNumber + 1;

    /// <summary>The dispatchable fleet's canonical IDs in order, "001" .. "010".</summary>
    public static IReadOnlyList<string> DispatchableTrainIds { get; } =
        Enumerable.Range(FirstDispatchableNumber, DispatchableTrainCount).Select(Format).ToArray();

    /// <summary>Readable range of the dispatchable fleet, "001–010", for diagnostics.</summary>
    public static string DispatchableRangeText => $"{DispatchableTrainIds[0]}–{DispatchableTrainIds[^1]}";

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
    /// True when <paramref name="trainId"/> is the canonical ID of a train in the dispatchable
    /// fleet, "001" to "010". "000", "011" and non-canonical labels are not dispatchable.
    /// </summary>
    public static bool IsDispatchable(string? trainId) =>
        IsCanonical(trainId)
        && int.Parse(trainId!, NumberStyles.None, CultureInfo.InvariantCulture) is >= FirstDispatchableNumber and <= LastDispatchableNumber;

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
