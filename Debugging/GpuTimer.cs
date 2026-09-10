using System;
using Microsoft.Xna.Framework.Graphics;
using SharpDX.Direct3D11;

namespace monogame.Debugging;

// DirectX timestamps cover submitted drawing, not Present or whole-device utilization.
internal sealed class GpuTimer : IDisposable {
    private readonly DeviceContext _context;
    private readonly Sample[] _samples = new Sample[4];
    private int _read;
    private int _write;
    private int _pending;
    private bool _recording;

    internal double? Milliseconds { get; private set; }

    internal GpuTimer(GraphicsDevice graphicsDevice) {
        var device = (Device)graphicsDevice.Handle;
        _context = device.ImmediateContext;
        try {
            for (var i = 0; i < _samples.Length; i++) _samples[i] = new Sample(device);
        }
        catch {
            Dispose();
            throw;
        }
    }

    // One frame-wide scope; nested GPU measurements are not supported.
    internal Scope Measure() {
        // Consume only ready samples; never flush or wait for the GPU.
        while (_pending > 0) {
            var sample = _samples[_read];
            if (!_context.GetData(sample.Disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint clock)
                || !_context.GetData(sample.Start, AsynchronousFlags.DoNotFlush, out long start)
                || !_context.GetData(sample.End, AsynchronousFlags.DoNotFlush, out long end)) break;

            Milliseconds = !clock.Disjoint && clock.Frequency > 0 && end >= start
                ? (end - start) * 1000.0 / clock.Frequency : null;
            _read = (_read + 1) % _samples.Length;
            _pending--;
        }

        _recording = _pending < _samples.Length;
        if (!_recording) return default;
        var current = _samples[_write];
        _context.Begin(current.Disjoint);
        _context.End(current.Start);
        return new Scope(this);
    }

    private void End() {
        if (!_recording) return;
        var current = _samples[_write];
        _context.End(current.End);
        _context.End(current.Disjoint);
        _write = (_write + 1) % _samples.Length;
        _pending++;
        _recording = false;
    }

    public void Dispose() {
        foreach (var sample in _samples) sample?.Dispose();
        // Device and immediate context are borrowed from MonoGame.
    }

    internal readonly struct Scope : IDisposable {
        private readonly GpuTimer _owner;
        internal Scope(GpuTimer owner) => _owner = owner;
        public void Dispose() => _owner?.End();
    }

    private sealed class Sample : IDisposable {
        internal readonly Query Start;
        internal readonly Query End;
        internal readonly Query Disjoint;

        internal Sample(Device device) {
            try {
                Start = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
                End = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
                Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
            }
            catch {
                Dispose();
                throw;
            }
        }

        public void Dispose() {
            Disjoint?.Dispose();
            End?.Dispose();
            Start?.Dispose();
        }
    }
}
