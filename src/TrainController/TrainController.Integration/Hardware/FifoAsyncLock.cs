namespace TrainController.Integration.Hardware;

/// <summary>
/// Asynchronous mutual exclusion granted strictly in request order (first come, first served).
/// Makes the Hardware request queue deterministic: requests from the five Hardware trains go
/// to the Pi one at a time, in the order they were issued.
/// </summary>
public sealed class FifoAsyncLock
{
    private readonly object _gate = new object();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new Queue<TaskCompletionSource<bool>>();
    private bool _held;

    public async Task<Releaser> AcquireAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return new Releaser(this);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        using (cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)))
        {
            await waiter.Task.ConfigureAwait(false);
        }

        return new Releaser(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            while (_waiters.Count > 0)
            {
                // Skip waiters that were cancelled; hand the lock to the next live one.
                if (_waiters.Dequeue().TrySetResult(true))
                {
                    return;
                }
            }

            _held = false;
        }
    }

    public readonly struct Releaser : IDisposable
    {
        private readonly FifoAsyncLock _owner;

        internal Releaser(FifoAsyncLock owner) => _owner = owner;

        public void Dispose() => _owner?.Release();
    }
}
