using TrainControl.Common.Communication;

namespace TrackController.Wpf.Services;

/// <summary>Addresses only; both environments use the same receiver and production controller.</summary>
public sealed record WaysideEndpoints(string Input, string CtcOutput, string TrackModelOutput)
{
    // Track Model adapter endpoint is owned here until the team adopts it as a shared contract.
    public const string TrackModelInput = "TrainControl.TrackModel";

    public static WaysideEndpoints Live { get; } = new(NamedPipeNames.TrackController, NamedPipeNames.Ctc, TrackModelInput);

    /// <summary>Creates unique pipe addresses so a test session cannot send traffic to live modules.</summary>
    public static WaysideEndpoints CreateIsolated() => CreateForPrefix($"TrainControl.TrackController.Test.{Guid.NewGuid():N}");

    /// <summary>Builds input and output pipe names from a caller-selected namespace.</summary>
    public static WaysideEndpoints CreateForPrefix(string prefix) => new(prefix + ".Input", prefix + ".CTC", prefix + ".TrackModel");
}
