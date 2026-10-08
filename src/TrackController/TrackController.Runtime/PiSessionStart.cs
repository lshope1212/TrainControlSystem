using System.Text;
using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;
/// <summary>Supplies the owned territory and PLC program for a new paused Pi session.</summary>
public sealed record PiSessionStart(WaysideConfigurationMessage Configuration, string ProgramName, string ProgramSource);
