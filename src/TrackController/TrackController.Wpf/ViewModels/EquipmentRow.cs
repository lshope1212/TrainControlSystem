using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Presents commanded and measured states side by side for one equipment item.</summary>
public sealed record EquipmentRow(string Equipment, string Block, string Command, string Feedback, string Status);
