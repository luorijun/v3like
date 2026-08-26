using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;

namespace monogame.Planet;

internal struct SelectionCounters {
    internal int VisitedNodes;
    internal int ActiveChunks;
    internal int HorizonRejected;
    internal int FrustumTests;
    internal int FrustumRejected;
}

internal struct DrawCounters {
    internal int DrawCalls;
    internal int CacheMisses;
    internal int CacheEvictions;
    internal int MeshBuilds;
    internal long MeshBuildTimestampTicks;
}

internal readonly record struct SelectionMetrics(
    long Revision,
    double UpdateMilliseconds,
    int VisitedNodes,
    int ActiveChunks,
    int HorizonRejected,
    int FrustumTests,
    int FrustumRejected
);

internal readonly record struct DrawMetrics(
    double CpuMilliseconds,
    int DrawCalls,
    int CacheMisses,
    int CacheEvictions,
    int MeshBuilds,
    double MeshBuildMilliseconds
);

internal readonly record struct SphereMetrics(
    SelectionMetrics Selection,
    DrawMetrics Draw
);

internal sealed class PerformanceMonitor : IDisposable {
    private const double SampleIntervalSeconds = 0.5;

    private readonly PerformanceLog _log = new();
    private double _elapsedSeconds;
    private double _drawMilliseconds;
    private double _maximumDrawMilliseconds;
    private long _drawCalls;
    private int _drawFrames;
    private int _cacheMisses;
    private int _cacheEvictions;
    private int _meshBuilds;
    private double _meshBuildMilliseconds;
    private SelectionMetrics _selection;

    internal string LogPath => _log.Path;

    internal string Observe(in SphereMetrics metrics, GameTime gameTime) {
        _selection = metrics.Selection;
        _elapsedSeconds += gameTime.ElapsedGameTime.TotalSeconds;
        _drawMilliseconds += metrics.Draw.CpuMilliseconds;
        _maximumDrawMilliseconds = Math.Max(
            _maximumDrawMilliseconds,
            metrics.Draw.CpuMilliseconds
        );
        _drawCalls += metrics.Draw.DrawCalls;
        _drawFrames++;
        _cacheMisses += metrics.Draw.CacheMisses;
        _cacheEvictions += metrics.Draw.CacheEvictions;
        _meshBuilds += metrics.Draw.MeshBuilds;
        _meshBuildMilliseconds += metrics.Draw.MeshBuildMilliseconds;

        if (_elapsedSeconds < SampleIntervalSeconds) {
            return null;
        }

        var drawAverage = _drawFrames == 0 ? 0.0 : _drawMilliseconds / _drawFrames;
        var drawCallsAverage = _drawFrames == 0 ? 0.0 : (double)_drawCalls / _drawFrames;
        var sample = new PerformanceSample(
            DateTimeOffset.Now,
            _selection,
            drawAverage,
            _maximumDrawMilliseconds,
            drawCallsAverage,
            _cacheMisses,
            _cacheEvictions,
            _meshBuilds,
            _meshBuildMilliseconds
        );
        _log.Enqueue(sample);
        ResetInterval();
        return FormatTitle(sample, _log.HasError);
    }

    public void Dispose() {
        _log.Dispose();
    }

    private void ResetInterval() {
        _elapsedSeconds = 0.0;
        _drawMilliseconds = 0.0;
        _maximumDrawMilliseconds = 0.0;
        _drawCalls = 0;
        _drawFrames = 0;
        _cacheMisses = 0;
        _cacheEvictions = 0;
        _meshBuilds = 0;
        _meshBuildMilliseconds = 0.0;
    }

    private static string FormatTitle(in PerformanceSample sample, bool hasLogError) {
        var selection = sample.Selection;
        var error = hasLogError ? " | LOG ERR" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"LOD {selection.UpdateMilliseconds:F3}ms N{selection.VisitedNodes}->{selection.ActiveChunks} H{selection.HorizonRejected} F{selection.FrustumRejected}/{selection.FrustumTests} | Draw {sample.DrawAverageMilliseconds:F3}/{sample.MaximumDrawMilliseconds:F3}ms {sample.DrawCallsAverage:F0} calls | Mesh {sample.MeshBuilds}/{sample.MeshBuildMilliseconds:F3}ms E{sample.CacheEvictions}{error}"
        );
    }
}

internal readonly record struct PerformanceSample(
    DateTimeOffset Timestamp,
    SelectionMetrics Selection,
    double DrawAverageMilliseconds,
    double MaximumDrawMilliseconds,
    double DrawCallsAverage,
    int CacheMisses,
    int CacheEvictions,
    int MeshBuilds,
    double MeshBuildMilliseconds
);

internal sealed class PerformanceLog : IDisposable {
    private static readonly TimeSpan s_retention = TimeSpan.FromMinutes(1.0);

    private readonly Channel<PerformanceSample> _samples;
    private readonly Task _writer;
    private int _hasError;

    internal PerformanceLog() {
        Path = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Logs",
            "planet-performance.csv"
        );
        _samples = Channel.CreateBounded<PerformanceSample>(
            new BoundedChannelOptions(4) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            }
        );
        _writer = Task.Run(WriteSamplesAsync);
    }

    internal string Path { get; }

    internal bool HasError => Volatile.Read(ref _hasError) != 0;

    internal void Enqueue(in PerformanceSample sample) {
        _samples.Writer.TryWrite(sample);
    }

    public void Dispose() {
        _samples.Writer.TryComplete();
        try {
            _writer.GetAwaiter().GetResult();
        }
        catch (IOException) {
            Volatile.Write(ref _hasError, 1);
        }
        catch (UnauthorizedAccessException) {
            Volatile.Write(ref _hasError, 1);
        }
    }

    private async Task WriteSamplesAsync() {
        var retained = new Queue<PerformanceSample>();
        await foreach (var sample in _samples.Reader.ReadAllAsync()) {
            retained.Enqueue(sample);
            var cutoff = sample.Timestamp - s_retention;
            while (retained.TryPeek(out var oldest) && oldest.Timestamp < cutoff) {
                retained.Dequeue();
            }

            try {
                WriteSnapshot(retained);
                Volatile.Write(ref _hasError, 0);
            }
            catch (IOException) {
                Volatile.Write(ref _hasError, 1);
            }
            catch (UnauthorizedAccessException) {
                Volatile.Write(ref _hasError, 1);
            }
        }
    }

    private void WriteSnapshot(IEnumerable<PerformanceSample> samples) {
        var directory = System.IO.Path.GetDirectoryName(Path);
        Directory.CreateDirectory(directory!);
        var temporaryPath = Path + ".tmp";
        using (var writer = new StreamWriter(temporaryPath, append: false)) {
            writer.WriteLine(
                "timestamp,lod_revision,lod_update_ms,visited,active,horizon_rejected,frustum_tests,frustum_rejected,draw_cpu_avg_ms,draw_cpu_max_ms,draw_calls_avg,cache_misses,evictions,mesh_builds,mesh_build_ms"
            );
            foreach (var sample in samples) {
                writer.WriteLine(FormatCsv(sample));
            }
        }

        File.Move(temporaryPath, Path, overwrite: true);
    }

    private static string FormatCsv(in PerformanceSample sample) {
        var selection = sample.Selection;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sample.Timestamp:O},{selection.Revision},{selection.UpdateMilliseconds:F6},{selection.VisitedNodes},{selection.ActiveChunks},{selection.HorizonRejected},{selection.FrustumTests},{selection.FrustumRejected},{sample.DrawAverageMilliseconds:F6},{sample.MaximumDrawMilliseconds:F6},{sample.DrawCallsAverage:F3},{sample.CacheMisses},{sample.CacheEvictions},{sample.MeshBuilds},{sample.MeshBuildMilliseconds:F6}"
        );
    }
}
