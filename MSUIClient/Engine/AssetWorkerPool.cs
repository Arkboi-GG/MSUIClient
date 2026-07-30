namespace MSUIClient.Engine;

/// <summary>
/// Bounded CPU preparation pool. ThreadPool supplies the threads, while the
/// semaphore reserves headroom for the render loop, input and the OS instead
/// of letting a newly queued terrain ring occupy every logical processor.
/// </summary>
public sealed class AssetWorkerPool : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _generalSlots;

    public int WorkerCount { get; }
    public int ReservedCriticalSlots { get; }

    public AssetWorkerPool()
    {
        WorkerCount = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
        ReservedCriticalSlots = Math.Min(2, WorkerCount - 1);
        _slots = new SemaphoreSlim(WorkerCount, WorkerCount);
        _generalSlots = new SemaphoreSlim(
            WorkerCount - ReservedCriticalSlots,
            WorkerCount - ReservedCriticalSlots);
        Console.WriteLine(
            $"[stream] {WorkerCount} CPU asset worker(s), {ReservedCriticalSlots} reserved for terrain ADTs, " +
            $"on {Environment.ProcessorCount} logical core(s)");
    }

    public Task<T> Run<T>(Func<T> work)
        => Task.Run(async () =>
        {
            await _generalSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                await _slots.WaitAsync().ConfigureAwait(false);
                try { return work(); }
                finally { _slots.Release(); }
            }
            finally { _generalSlots.Release(); }
        });

    /// <summary>
    /// Run residency-critical work. These jobs may consume the two slots that
    /// general model preparation cannot occupy, while still respecting the
    /// pool's total CPU bound.
    /// </summary>
    public Task<T> RunCritical<T>(Func<T> work)
        => Task.Run(async () =>
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try { return work(); }
            finally { _slots.Release(); }
        });

    public void Dispose()
    {
        _generalSlots.Dispose();
        _slots.Dispose();
    }
}
