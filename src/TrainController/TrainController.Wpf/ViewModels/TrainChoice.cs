using TrainController.Abstractions.Fleet;
using TrainController.Integration.Presentation;

namespace TrainController.Wpf.ViewModels;

/// <summary>One entry of a train dropdown. The controller type is display-only (fixed assignment).</summary>
public sealed record TrainChoice(string TrainId, ControllerType ControllerType)
{
    public string Display => $"{TrainId}  ·  {TrainControllerPresenter.ControllerShortText(ControllerType)}";

    public static IReadOnlyList<TrainChoice> AllTrains { get; } =
        TrainFleet.AllTrainIds.Select(id => new TrainChoice(id, TrainFleet.GetControllerType(id))).ToArray();
}
