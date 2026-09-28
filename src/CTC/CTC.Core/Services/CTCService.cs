using CTC.Core.Interfaces;
using CTC.Core.Models;

namespace CTC.Core.Services;

/// <summary>
/// Stub CTC office service. No dispatching logic yet.
/// </summary>
public class CTCService : ICTCService
{
    public SystemState SystemState { get; } = new SystemState();

    public DispatcherState DispatcherState { get; } = new DispatcherState();
}
