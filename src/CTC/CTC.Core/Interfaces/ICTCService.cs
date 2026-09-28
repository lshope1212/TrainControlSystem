using CTC.Core.Models;

namespace CTC.Core.Interfaces;

/// <summary>
/// Placeholder contract for the centralized traffic control office.
/// </summary>
public interface ICTCService
{
    SystemState SystemState { get; }

    DispatcherState DispatcherState { get; }
}
