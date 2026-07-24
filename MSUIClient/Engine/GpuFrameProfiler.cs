using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// Non-blocking GPU pass timings. Results are polled several frames after a
/// query is submitted; the render thread never waits for the GPU to finish.
/// </summary>
public sealed class GpuFrameProfiler : IDisposable
{
    public enum Pass
    {
        Terrain,
        Wmo,
        Doodads,
        Character,
        Debug,
        Count,
    }

    private const int SlotCount = 4;
    private readonly GL _gl;
    private readonly uint[,] _queries = new uint[SlotCount, (int)Pass.Count];
    private readonly bool[,] _pending = new bool[SlotCount, (int)Pass.Count];
    private readonly bool[] _recorded = new bool[(int)Pass.Count];
    private readonly double[] _milliseconds = new double[(int)Pass.Count];
    private readonly bool[] _hasResult = new bool[(int)Pass.Count];

    private int _writeSlot = -1;
    private bool _canRecord;
    private Pass? _active;

    public GpuFrameProfiler(GL gl)
    {
        _gl = gl;
        for (int slot = 0; slot < SlotCount; slot++)
        for (int pass = 0; pass < (int)Pass.Count; pass++)
            _queries[slot, pass] = gl.GenQuery();
    }

    public bool HasResults => _hasResult.Any(x => x);
    public double this[Pass pass] => _milliseconds[(int)pass];
    public double MeasuredTotalMilliseconds => _milliseconds.Sum();

    public void BeginFrame()
    {
        PollReadyResults();

        int next = (_writeSlot + 1) % SlotCount;
        _canRecord = true;
        for (int pass = 0; pass < (int)Pass.Count; pass++)
        {
            if (_pending[next, pass])
            {
                _canRecord = false;
                break;
            }
        }

        if (!_canRecord) return;
        _writeSlot = next;
        Array.Clear(_recorded);
    }

    public void Begin(Pass pass)
    {
        if (!_canRecord || _active is not null) return;
        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[_writeSlot, (int)pass]);
        _active = pass;
    }

    public void End(Pass pass)
    {
        if (!_canRecord || _active != pass) return;
        _gl.EndQuery(QueryTarget.TimeElapsed);
        _recorded[(int)pass] = true;
        _active = null;
    }

    public void EndFrame()
    {
        if (!_canRecord) return;
        if (_active is { } active) End(active);
        for (int pass = 0; pass < (int)Pass.Count; pass++)
            if (_recorded[pass]) _pending[_writeSlot, pass] = true;
    }

    private void PollReadyResults()
    {
        for (int slot = 0; slot < SlotCount; slot++)
        for (int pass = 0; pass < (int)Pass.Count; pass++)
        {
            if (!_pending[slot, pass]) continue;

            _gl.GetQueryObject(
                _queries[slot, pass], QueryObjectParameterName.ResultAvailable,
                out int available);
            if (available == 0) continue;

            _gl.GetQueryObject(
                _queries[slot, pass], QueryObjectParameterName.Result,
                out ulong nanoseconds);
            double sample = nanoseconds / 1_000_000.0;
            _milliseconds[pass] = _hasResult[pass]
                ? _milliseconds[pass] * 0.8 + sample * 0.2
                : sample;
            _hasResult[pass] = true;
            _pending[slot, pass] = false;
        }
    }

    public void Dispose()
    {
        if (_active is not null)
            _gl.EndQuery(QueryTarget.TimeElapsed);

        for (int slot = 0; slot < SlotCount; slot++)
        for (int pass = 0; pass < (int)Pass.Count; pass++)
            if (_queries[slot, pass] != 0) _gl.DeleteQuery(_queries[slot, pass]);
    }
}
