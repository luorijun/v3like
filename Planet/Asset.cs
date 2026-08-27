using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using monogame.Terrain;

namespace monogame.Planet;

internal sealed class SphereData {
    internal const int FaceCount = 6;

    internal SphereData(
        int chunkResolution,
        int maximumLod,
        float occluderRadius,
        FaceData[] faces
    ) {
        ChunkResolution = chunkResolution;
        MaximumLod = maximumLod;
        OccluderRadius = occluderRadius;
        Faces = faces;
    }

    public int ChunkResolution { get; }

    public int MaximumLod { get; }

    public float OccluderRadius { get; }

    internal FaceData[] Faces { get; }

    internal int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

    internal int FinestResolution => checked(FinestIntervals + 1);
}

internal sealed class FaceData {
    private readonly short[] _elevations;
    private readonly float _elevationStep;

    internal FaceData(
        int resolution,
        float elevationStep,
        short[] elevations,
        ChunkData[] chunks
    ) {
        Resolution = resolution;
        _elevationStep = elevationStep;
        _elevations = elevations;
        Chunks = chunks;
    }

    public int Resolution { get; }

    internal ReadOnlySpan<short> QuantizedElevations => _elevations;

    internal ChunkData[] Chunks { get; }

    internal float GetElevationUnchecked(int x, int y) {
        return _elevations[y * Resolution + x] * _elevationStep;
    }
}

internal readonly record struct ChunkData(
    Vector3 CenterDirection,
    float AngularRadius,
    float MinimumRadius,
    float MaximumRadius,
    BoundingSphere BoundingSphere,
    float HorizonPointRadius,
    float GeometricError
);

internal readonly record struct AssetSettings {
    internal AssetSettings(
        int chunkResolution,
        int maximumLod,
        float maximumElevation
    ) {
        if (chunkResolution < 2
            || maximumLod is < 0 or > Chunk.MaximumLevel
            || !float.IsFinite(maximumElevation)
            || maximumElevation < 0.0f
            || !float.IsFinite(1.0f + maximumElevation)) {
            throw new ArgumentOutOfRangeException(nameof(maximumElevation));
        }

        ChunkResolution = chunkResolution;
        MaximumLod = maximumLod;
        MaximumElevation = maximumElevation;
        _ = FinestResolution;
        _ = ChunkCount;
    }

    internal int ChunkResolution { get; }

    internal int MaximumLod { get; }

    internal float MaximumElevation { get; }

    internal int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

    internal int FinestResolution => checked(FinestIntervals + 1);

    internal int ChunkCount {
        get {
            var count = 0L;
            var chunksAtLevel = 1L;
            for (var level = 0; level <= MaximumLod; level++) {
                count += chunksAtLevel;
                chunksAtLevel *= 4;
            }

            return checked((int)count);
        }
    }
}

internal static class Asset {
    public static SphereData Read(Stream source) {
        return AssetCodec.Read(source);
    }

    public static void Write(
        Stream destination,
        int chunkResolution,
        int maximumLod,
        float maximumElevation,
        IReferenceElevationSource elevationSource
    ) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(elevationSource);
        if (!destination.CanWrite) {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        var settings = new AssetSettings(
            chunkResolution,
            maximumLod,
            maximumElevation
        );
        var stopwatch = Stopwatch.StartNew();
        var data = AssetBuilder.Build(settings, elevationSource);
        Console.WriteLine($"[Asset] build: {stopwatch.Elapsed.TotalSeconds:F3}s");

        stopwatch.Restart();
        AssetCodec.Write(destination, settings, data);
        Console.WriteLine($"[Asset] write: {stopwatch.Elapsed.TotalSeconds:F3}s");
    }

    internal static SphereData CreateData(in AssetSettings settings, float occluderRadius, short[][] elevations, ChunkData[][] chunks) {
        var faces = new FaceData[SphereData.FaceCount];
        var elevationStep = settings.MaximumElevation / short.MaxValue;
        for (var index = 0; index < faces.Length; index++) {
            faces[index] = new FaceData(
                settings.FinestResolution,
                elevationStep,
                elevations[index],
                chunks[index]
            );
        }

        return new SphereData(
            settings.ChunkResolution,
            settings.MaximumLod,
            occluderRadius,
            faces
        );
    }

}
