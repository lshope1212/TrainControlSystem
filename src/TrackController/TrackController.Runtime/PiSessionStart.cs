using System.Text;
using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;
/// <summary>Supplies territory, PLC and maintenance intent for a new Pi session.</summary>
public sealed record PiSessionStart(WaysideConfigurationMessage Configuration, string ProgramName, string ProgramSource, bool MaintenanceMode = false);
