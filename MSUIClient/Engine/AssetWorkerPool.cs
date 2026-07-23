namespace MSUIClient.Engine;

/// <summary>
/// Bounded CPU preparation pool. ThreadPool supplies the threads, while the
/// semaphore reserves headroom for the render loop, input and the OS instead
/// of letting a newly queued terrain ring occupy every logical processor.
/// </summary>
public sealed class AssetWorkerPool : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public int WorkerCount { get; }

    public AssetWorkerPool()
    {
        WorkerCount = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
        _slots = new SemaphoreSlim(WorkerCount, WorkerCount);
        Console.WriteLine(
            $"[stream] {WorkerCount} CPU asset worker(s) on {Environment.ProcessorCount} logical core(s)");
    }

    public Task<T> Run<T>(Func<T> work)
        => Task.Run(async () =>
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try { return work(); }
            finally { _slots.Release(); }
        });

    public void Dispose() => _slots.Dispose();
}
