using System.Collections.Concurrent;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MSUIClient.Engine;

/// <summary>
/// Owns a hidden OpenGL context sharing objects with the render context.
/// Resource creation runs exclusively on its dedicated thread; completed tasks
/// are published only after an upload-context fence signals, so the render
/// thread never touches a half-created object. A per-upload fence matters on
/// integrated Intel drivers: glFinish on the shared context can serialize the
/// render context too and present as a full-screen freeze.
/// </summary>
public sealed class GpuUploadWorker : IDisposable
{
    private readonly IWindow _window;
    private readonly BlockingCollection<Action<GL>> _queue = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private GL? _gl;

    private int _inFlight;
    private int _completedTotal;
    private int _completedAtLastFrame;

    /// <summary>
    /// Uploads queued but not yet finished. Read once per frame by the hitch
    /// recorder and NOT used for any control decision - it is evidence, not a
    /// gate.
    ///
    /// Why it exists: the surviving stutter is a multi-ms block inside the
    /// frame's LAST GL call, on frames where our own CPU work is under 1 ms and
    /// the GPU is under 1 ms. The standing hypothesis (SYSTEM_STREAMING 5.2 H2)
    /// is that this shared upload context serializes the render context on the
    /// Intel driver - the exact failure the fence below was chosen to avoid. That
    /// hypothesis is only testable if each record can say whether an upload was
    /// in flight at the time, so this counter IS the discriminator: flush spikes
    /// that always coincide with in-flight uploads confirm it, flush spikes on
    /// quiet frames refute it.
    /// </summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Uploads that finished since the previous call. Call exactly once per
    /// frame - it consumes what it reports, so a second caller silently sees
    /// zero. Only ever called from the render thread.
    /// </summary>
    public int ConsumeCompletedSinceLastFrame()
    {
        int total = Volatile.Read(ref _completedTotal);
        int delta = total - _completedAtLastFrame;
        _completedAtLastFrame = total;
        return delta;
    }

    public GpuUploadWorker(IWindow renderWindow, GraphicsAPI api)
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1, 1),
            Title = "MSUI GPU uploader",
            IsVisible = false,
            VSync = false,
            ShouldSwapAutomatically = false,
            API = api,
            SharedContext = renderWindow.GLContext,
        };

        _window = Window.Create(options);
        _window.Initialize();

        // Initialize makes the new context current on this thread. Restore the
        // real window before its next update/render callback.
        renderWindow.GLContext?.MakeCurrent();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "MSUI GPU upload",
        };
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    public Task<T> Enqueue<T>(string label, Func<GL, T> upload)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Incremented before the item is queued, so InFlight can never read low
        // during the window where work exists but has not been picked up.
        Interlocked.Increment(ref _inFlight);

        _queue.Add(gl =>
        {
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                T result = upload(gl);
                nint fence = gl.FenceSync(
                    SyncCondition.SyncGpuCommandsComplete, (SyncBehaviorFlags)0);
                gl.Flush();
                while (true)
                {
                    var status = gl.ClientWaitSync(fence, (SyncObjectMask)0, 1_000_000);
                    if (status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied) break;
                    if (status == GLEnum.WaitFailed)
                        throw new InvalidOperationException("OpenGL upload fence wait failed");
                    Thread.Yield();
                }
                gl.DeleteSync(fence);
                if (timer.Elapsed.TotalMilliseconds >= 8)
                    Console.WriteLine(
                        $"[gpu-upload] {label} completed in {timer.Elapsed.TotalMilliseconds:F0}ms off-thread");
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                // In the finally, so a failed upload cannot leave InFlight
                // permanently high and make every later record read as
                // "uploads were busy".
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref _completedTotal);
            }
        });

        return completion.Task;
    }

    private void Run()
    {
        try
        {
            _window.GLContext?.MakeCurrent();
            _gl = _window.CreateOpenGL();
            _started.TrySetResult();

            foreach (var work in _queue.GetConsumingEnumerable()) work(_gl);
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            // Discarded items never run their finally, so balance InFlight here
            // or a dead worker leaves every later record reading "uploads busy".
            while (_queue.TryTake(out _)) Interlocked.Decrement(ref _inFlight);
        }
        finally
        {
            _gl?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
        _window.Dispose();
    }
}
