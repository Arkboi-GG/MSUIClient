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
            while (_queue.TryTake(out _)) { }
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
