using CTC.Core.Models;
using TrainControl.Common.Utilities;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Services;

/// <summary>
/// Applies a received <see cref="TrackLayoutMessage"/> to CTC's territory line by line, so a
/// repeated or partly changed layout never resets state it does not have to.
/// </summary>
/// <remarks>
/// <para>
/// A layout is its publisher's complete set of lines. Per line: a new line is added; an identical
/// line is left completely untouched (live block status, schedules, the dispatch queue and train
/// tracking all survive an equivalent repeated layout); a line whose blocks and connections are the
/// same but whose static attributes changed is updated in place.
/// </para>
/// <para>
/// A genuinely incompatible revision (blocks added, removed or reconnected), or a line missing from
/// the layout, is applied only when no train is released on that line, because CTC's tracking of a
/// released train is defined in terms of the old topology. The line is then rebuilt (its blocks
/// start with Unknown status) and any schedule whose route no longer exists is dropped and
/// reported. With trains released, CTC refuses that line's change, keeps its current topology and
/// reports why; other lines in the same layout are still applied.
/// </para>
/// <para>
/// Validation happens before anything changes: blank or duplicate line IDs and blank or duplicate
/// block IDs (within or ACROSS lines) reject the whole layout. Block IDs are taken verbatim from the
/// layout and never remapped.
/// </para>
/// </remarks>
internal static class TrackLayoutReconciler
{
    public static TrackLayoutSyncResult Apply(CtcSystemState state, TrackLayoutMessage message)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);

        var result = new TrackLayoutSyncResult();
        var changes = new List<Action>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var incomingLineIds = message.Lines.Select(line => line.LineId).ToHashSet(StringComparer.Ordinal);

        foreach (var definition in message.Lines)
        {
            var existing = state.FindLine(definition.LineId);
            if (existing is null)
            {
                result.AddedLineIds.Add(definition.LineId);
                changes.Add(() => state.Lines.Add(CreateLine(definition)));
                Claim(owners, definition.LineId, definition.Blocks.Select(block => block.BlockId));
            }
            else if (!HasSameTopology(existing, definition))
            {
                if (HasReleasedTrains(state, existing.LineId))
                {
                    result.RejectedChanges.Add($"{DisplayName(existing)}'s topology changed while trains are released on it, so CTC kept its current topology; resynchronize the layout once those trains are cleared.");
                    Claim(owners, existing.LineId, existing.Blocks.Select(block => block.BlockId));
                }
                else
                {
                    result.ReplacedLineIds.Add(existing.LineId);
                    changes.Add(() => Replace(state, existing, definition, result));
                    Claim(owners, definition.LineId, definition.Blocks.Select(block => block.BlockId));
                }
            }
            else
            {
                if (HasSameAttributes(existing, definition))
                {
                    result.UnchangedLineIds.Add(existing.LineId);
                }
                else
                {
                    result.UpdatedLineIds.Add(existing.LineId);
                    changes.Add(() =>
                    {
                        UpdateInPlace(existing, definition);
                        DropObsoleteStarts(state, existing, result);
                    });
                }

                Claim(owners, definition.LineId, definition.Blocks.Select(block => block.BlockId));
            }
        }

        foreach (var existing in state.Lines.Where(line => !incomingLineIds.Contains(line.LineId)).ToList())
        {
            if (HasReleasedTrains(state, existing.LineId))
            {
                result.RejectedChanges.Add($"{DisplayName(existing)} is no longer in the layout but has released trains, so CTC kept it.");
                Claim(owners, existing.LineId, existing.Blocks.Select(block => block.BlockId));
            }
            else
            {
                result.RemovedLineIds.Add(existing.LineId);
                changes.Add(() => Remove(state, existing, result));
            }
        }

        // Every check above can still throw (a block ID shared with a line CTC keeps); only now change anything.
        foreach (var change in changes)
        {
            change();
        }

        return result;
    }

    /// <summary>Speed limit in km/h; falls back to the m/s field when a producer filled only that one.</summary>
    internal static double SpeedLimitKilometersPerHour(TrackBlockDefinition definition) =>
        definition.SpeedLimitKilometersPerHour > 0 ? definition.SpeedLimitKilometersPerHour
        : definition.SpeedLimitMetersPerSecond > 0 ? UnitConversion.MetersPerSecondToKilometersPerHour(definition.SpeedLimitMetersPerSecond)
        : 0;

    private static void Validate(TrackLayoutMessage message)
    {
        if (message.Lines is null)
        {
            throw new ArgumentException("The track layout has no line list.", nameof(message));
        }

        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        var blockLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in message.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.LineId))
            {
                throw new ArgumentException("Every line in the track layout needs a line ID.", nameof(message));
            }

            if (!lineIds.Add(line.LineId))
            {
                throw new ArgumentException($"Line {line.LineId} appears more than once in the track layout.", nameof(message));
            }

            if (line.Blocks is null)
            {
                throw new ArgumentException($"Line {line.LineId} has no block list.", nameof(message));
            }

            foreach (var block in line.Blocks)
            {
                if (block is null || string.IsNullOrWhiteSpace(block.BlockId))
                {
                    throw new ArgumentException($"Line {line.LineId} has a block without a block ID.", nameof(message));
                }

                if (blockLines.TryGetValue(block.BlockId, out var otherLine))
                {
                    throw new ArgumentException(DuplicateMessage(block.BlockId, otherLine, line.LineId), nameof(message));
                }

                blockLines[block.BlockId] = line.LineId;
            }
        }
    }

    private static void Claim(Dictionary<string, string> owners, string lineId, IEnumerable<string> blockIds)
    {
        foreach (var blockId in blockIds)
        {
            if (owners.TryGetValue(blockId, out var otherLine) && otherLine != lineId)
            {
                throw new ArgumentException(DuplicateMessage(blockId, otherLine, lineId));
            }

            owners[blockId] = lineId;
        }
    }

    private static string DuplicateMessage(string blockId, string firstLine, string secondLine) => firstLine == secondLine
        ? $"Block ID '{blockId}' appears twice on line {firstLine}."
        : $"Block ID '{blockId}' would exist on both line {firstLine} and line {secondLine}. Block IDs must be unique across every line because block status messages carry no line; qualify them where the layout is defined (e.g. \"{secondLine.ToUpperInvariant()}-{blockId}\"). The layout was not applied.";

    private static bool HasReleasedTrains(CtcSystemState state, string lineId) =>
        state.DispatchedTrains.Any(train => train.LineId == lineId)
        || state.DispatchQueue.Any(entry => entry.LineId == lineId && entry.QueueStatus == DispatchQueueStatus.Dispatching);

    private static bool HasSameTopology(CtcLineState line, TrackLineDefinition definition)
    {
        if (line.Blocks.Count != definition.Blocks.Count)
        {
            return false;
        }

        var blocks = line.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        foreach (var incoming in definition.Blocks)
        {
            if (!blocks.TryGetValue(incoming.BlockId, out var block)
                || !block.ConnectedBlockIds.ToHashSet(StringComparer.Ordinal).SetEquals(incoming.ConnectedBlockIds ?? []))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameAttributes(CtcLineState line, TrackLineDefinition definition)
    {
        if (line.Name != definition.Name)
        {
            return false;
        }

        var blocks = line.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        return definition.Blocks.All(incoming =>
        {
            var block = blocks[incoming.BlockId];
            return block.BlockNumber == incoming.BlockNumber
                && block.Section == incoming.Section
                && block.LengthMeters.Equals(incoming.LengthMeters)
                && block.SpeedLimitKilometersPerHour.Equals(SpeedLimitKilometersPerHour(incoming))
                && block.StationName == incoming.StationName
                && block.HasSwitch == incoming.HasSwitch
                && block.HasSignal == incoming.HasSignal
                && block.HasCrossing == incoming.HasCrossing
                && block.IsYardEntry == incoming.IsYardEntry
                && block.TravelDirection == Direction(incoming)
                && block.NormalNextBlockId == (incoming.NormalNextBlockId ?? string.Empty)
                && block.ReverseNextBlockId == (incoming.ReverseNextBlockId ?? string.Empty);
        });
    }

    private static void UpdateInPlace(CtcLineState line, TrackLineDefinition definition)
    {
        line.Name = definition.Name;
        var blocks = line.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        foreach (var incoming in definition.Blocks)
        {
            CopyAttributes(incoming, blocks[incoming.BlockId]);
        }
    }

    private static void Replace(CtcSystemState state, CtcLineState line, TrackLineDefinition definition, TrackLayoutSyncResult result)
    {
        line.Name = definition.Name;
        line.Blocks.Clear();
        foreach (var block in definition.Blocks)
        {
            line.Blocks.Add(CreateBlock(block));
        }

        // Keep only schedules that still describe a connected route on the new topology.
        foreach (var train in state.ScheduledTrains.Where(train => train.LineId == line.LineId && !RouteFits(train, line)).ToList())
        {
            DropSchedule(state, train, result);
        }

        DropObsoleteStarts(state, line, result);
    }

    /// <summary>
    /// Drops schedules not yet released whose start block the revised layout no longer marks as a
    /// yard entry, so CTC never releases a train from a start that is no longer permitted. A train
    /// already released (dispatched or being released) is on the track and keeps its schedule.
    /// </summary>
    private static void DropObsoleteStarts(CtcSystemState state, CtcLineState line, TrackLayoutSyncResult result)
    {
        var entries = line.Blocks.Where(block => block.IsYardEntry).Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        var obsolete = state.ScheduledTrains
            .Where(train => train.LineId == line.LineId && !entries.Contains(train.StartBlockId))
            .Where(train => state.FindDispatchedTrain(train.TrainId) is null)
            .Where(train => !state.DispatchQueue.Any(entry => entry.TrainId == train.TrainId && entry.QueueStatus == DispatchQueueStatus.Dispatching))
            .ToList();
        foreach (var train in obsolete)
        {
            DropSchedule(state, train, result);
            result.InvalidStartTrainIds.Add(train.TrainId);
        }
    }

    private static void Remove(CtcSystemState state, CtcLineState line, TrackLayoutSyncResult result)
    {
        state.Lines.Remove(line);
        foreach (var train in state.ScheduledTrains.Where(train => train.LineId == line.LineId).ToList())
        {
            DropSchedule(state, train, result);
        }
    }

    private static void DropSchedule(CtcSystemState state, ScheduledTrain train, TrackLayoutSyncResult result)
    {
        state.ScheduledTrains.Remove(train);
        foreach (var entry in state.DispatchQueue.Where(entry => entry.TrainId == train.TrainId && entry.LineId == train.LineId).ToList())
        {
            state.DispatchQueue.Remove(entry);
        }

        result.DroppedScheduledTrainIds.Add(train.TrainId);
    }

    private static bool RouteFits(ScheduledTrain train, CtcLineState line)
    {
        var blocks = new List<CtcBlockState>();
        foreach (var routeBlock in train.Route)
        {
            var block = line.Blocks.FirstOrDefault(block => block.BlockId == routeBlock.BlockId);
            if (block is null)
            {
                return false;
            }

            blocks.Add(block);
        }

        for (int i = 0; i + 1 < blocks.Count; i++)
        {
            if (!blocks[i].ConnectedBlockIds.Contains(blocks[i + 1].BlockId))
            {
                return false;
            }
        }

        return true;
    }

    private static CtcLineState CreateLine(TrackLineDefinition definition)
    {
        var line = new CtcLineState
        {
            LineId = definition.LineId,
            Name = definition.Name,
        };

        foreach (var block in definition.Blocks)
        {
            line.Blocks.Add(CreateBlock(block));
        }

        return line;
    }

    private static CtcBlockState CreateBlock(TrackBlockDefinition definition)
    {
        var block = new CtcBlockState { BlockId = definition.BlockId };
        CopyAttributes(definition, block);
        foreach (var connectedBlockId in definition.ConnectedBlockIds ?? [])
        {
            block.ConnectedBlockIds.Add(connectedBlockId);
        }

        return block;
    }

    private static void CopyAttributes(TrackBlockDefinition definition, CtcBlockState block)
    {
        block.BlockNumber = definition.BlockNumber;
        block.Section = definition.Section;
        block.LengthMeters = definition.LengthMeters;
        block.SpeedLimitKilometersPerHour = SpeedLimitKilometersPerHour(definition);
        block.StationName = definition.StationName;
        block.HasSwitch = definition.HasSwitch;
        block.HasSignal = definition.HasSignal;
        block.HasCrossing = definition.HasCrossing;
        block.IsYardEntry = definition.IsYardEntry;
        block.TravelDirection = Direction(definition);
        block.NormalNextBlockId = definition.NormalNextBlockId ?? string.Empty;
        block.ReverseNextBlockId = definition.ReverseNextBlockId ?? string.Empty;
    }

    private static string Direction(TrackBlockDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.TravelDirection) ? "Bidirectional" : definition.TravelDirection;

    private static string DisplayName(CtcLineState line) => string.IsNullOrWhiteSpace(line.Name) ? $"Line {line.LineId}" : line.Name;
}
