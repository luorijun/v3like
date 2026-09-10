using System;
using System.Collections.Generic;
using System.Diagnostics;
using monogame.Lod;

namespace monogame.Debugging;

internal enum PeakMetric { FrameInterval, CpuWork }

internal readonly record struct ProfileSpan(string Name, int Parent, long Start, long End);

internal sealed class StepTiming {
    internal long TotalTicks;
    internal long SelfTicks;
    internal int Calls;
    internal readonly Dictionary<string, StepTiming> Children = new(StringComparer.Ordinal);
}

internal sealed class FrameSnapshot {
    internal ProfileSpan[] Buffer = new ProfileSpan[256];
    internal readonly Dictionary<string, StepTiming> Timings = new(StringComparer.Ordinal);
    private StepTiming[] _spanTimings = new StepTiming[256];
    internal int Count;
    internal long Number;
    internal long Start;
    internal long End;
    internal long? IntervalTicks;
    internal long CpuTicks;
    internal SelectionMetrics Selection;

    internal ReadOnlySpan<ProfileSpan> Spans => Buffer.AsSpan(0, Count);
    internal bool HasFrame => Number != 0;
    internal static double Milliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);

    internal void BuildTimings() {
        Timings.Clear();
        if (_spanTimings.Length < Count) Array.Resize(ref _spanTimings, Buffer.Length);
        CpuTicks = 0;
        for (var i = 0; i < Count; i++) {
            var span = Buffer[i];
            var parent = span.Parent < 0 ? null : _spanTimings[span.Parent];
            var siblings = parent is null ? Timings : parent.Children;
            if (!siblings.TryGetValue(span.Name, out var timing)) {
                timing = new StepTiming();
                siblings.Add(span.Name, timing);
            }
            _spanTimings[i] = timing;
            var elapsed = span.End - span.Start;
            timing.TotalTicks += elapsed;
            timing.SelfTicks += elapsed;
            timing.Calls++;
            if (parent is null) CpuTicks += elapsed;
            else parent.SelfTicks -= elapsed;
        }
        Array.Clear(_spanTimings);
    }

    internal void CopyFrom(FrameSnapshot source) {
        if (Buffer.Length < source.Count) Array.Resize(ref Buffer, source.Buffer.Length);
        source.Spans.CopyTo(Buffer);
        Count = source.Count;
        Number = source.Number;
        Start = source.Start;
        End = source.End;
        IntervalTicks = source.IntervalTicks;
        Selection = source.Selection;
        BuildTimings();
    }
}

// Single game thread, nested scopes, and one completion per Draw.
internal sealed class FrameProfiler {
    private FrameSnapshot _working = new();
    private int _parent = -1;
    private long _previousCompletedAt;
    private long _frameNumber;
    private bool _frameOpen;
    private bool _recording;
    private bool _paused;

    internal FrameSnapshot Last { get; private set; } = new();
    internal FrameSnapshot Peak { get; } = new();
    internal PeakMetric Metric { get; private set; }
    internal bool Paused => _paused;

    internal void SetPaused(bool paused) {
        if (_paused == paused) return;
        _paused = paused;
        _previousCompletedAt = 0;
        _recording = false;
    }

    internal void SetMetric(PeakMetric metric) {
        if (Metric == metric) return;
        Metric = metric;
        ResetPeak();
    }

    internal void ResetPeak() => Peak.Number = 0;

    internal Scope Measure(string name) {
        if (!_frameOpen) {
            _frameOpen = true;
            _recording = !_paused;
            _working.Start = _previousCompletedAt != 0 ? _previousCompletedAt : Stopwatch.GetTimestamp();
        }
        if (!_recording) return default;
        var index = _working.Count++;
        if (index == _working.Buffer.Length) Array.Resize(ref _working.Buffer, index * 2);
        _working.Buffer[index] = new ProfileSpan(name, _parent, Stopwatch.GetTimestamp(), 0);
        _parent = index;
        return new Scope(this, index);
    }

    private void EndScope(int index) {
        Debug.Assert(_parent == index, "Profiler scopes must close in nesting order.");
        var span = _working.Buffer[index];
        _working.Buffer[index] = span with { End = Stopwatch.GetTimestamp() };
        _parent = span.Parent;
    }

    internal void CompleteFrame(in SelectionMetrics selection) {
        Debug.Assert(_parent == -1, "CompleteFrame must be outside all scopes.");
        var now = Stopwatch.GetTimestamp();
        _frameNumber++;
        if (_recording) {
            _working.Number = _frameNumber;
            _working.End = now;
            _working.IntervalTicks = _previousCompletedAt == 0 ? null : now - _previousCompletedAt;
            _working.Selection = selection;
            _working.BuildTimings();
            (Last, _working) = (_working, Last);
            var score = Metric == PeakMetric.CpuWork ? Last.CpuTicks : Last.IntervalTicks;
            var peakScore = Metric == PeakMetric.CpuWork ? Peak.CpuTicks : Peak.IntervalTicks;
            if (score.HasValue && (!Peak.HasFrame || score > peakScore)) Peak.CopyFrom(Last);
        }
        Array.Clear(_working.Buffer, 0, _working.Count);
        _working.Count = 0;
        _previousCompletedAt = _paused ? 0 : now;
        _frameOpen = false;
        _recording = false;
    }

    internal readonly struct Scope : IDisposable {
        private readonly FrameProfiler _owner;
        private readonly int _index;
        internal Scope(FrameProfiler owner, int index) { _owner = owner; _index = index; }
        public void Dispose() => _owner?.EndScope(_index);
    }
}
