namespace TrainController.Abstractions.Fleet;

/// <summary>
/// The ONE place that defines the fleet and its fixed Software/Hardware assignment.
/// </summary>
/// <remarks>
/// The assignment is a design decision, not configuration: there is no setter, no
/// runtime switching and no fallback. Odd-numbered trains are Software, even-numbered
/// trains are Hardware (all five on one Raspberry Pi). TrainIds use the repository's
/// existing "TRAIN-001" style and are matched exactly (ordinal, case-sensitive).
/// </remarks>
public static class TrainFleet
{
    public const int TrainCount = 10;

    private static readonly IReadOnlyDictionary<string, ControllerType> Assignment =
        new Dictionary<string, ControllerType>(StringComparer.Ordinal)
        {
            ["TRAIN-001"] = ControllerType.Software,
            ["TRAIN-002"] = ControllerType.Hardware,
            ["TRAIN-003"] = ControllerType.Software,
            ["TRAIN-004"] = ControllerType.Hardware,
            ["TRAIN-005"] = ControllerType.Software,
            ["TRAIN-006"] = ControllerType.Hardware,
            ["TRAIN-007"] = ControllerType.Software,
            ["TRAIN-008"] = ControllerType.Hardware,
            ["TRAIN-009"] = ControllerType.Software,
            ["TRAIN-010"] = ControllerType.Hardware,
        };

    /// <summary>All ten trains in display order, TRAIN-001 .. TRAIN-010.</summary>
    public static IReadOnlyList<string> AllTrainIds { get; } =
        Assignment.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> SoftwareTrainIds { get; } =
        AllTrainIds.Where(id => Assignment[id] == ControllerType.Software).ToArray();

    public static IReadOnlyList<string> HardwareTrainIds { get; } =
        AllTrainIds.Where(id => Assignment[id] == ControllerType.Hardware).ToArray();

    public static bool IsKnownTrain(string? trainId) =>
        trainId is not null && Assignment.ContainsKey(trainId);

    /// <exception cref="UnknownTrainException"><paramref name="trainId"/> is not a fleet train.</exception>
    public static ControllerType GetControllerType(string? trainId)
    {
        if (trainId is not null && Assignment.TryGetValue(trainId, out var type))
        {
            return type;
        }

        throw new UnknownTrainException(trainId);
    }

    /// <exception cref="UnknownTrainException"><paramref name="trainId"/> is not a fleet train.</exception>
    public static void EnsureKnown(string? trainId) => GetControllerType(trainId);
}
