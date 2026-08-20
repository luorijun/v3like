using System;
using Microsoft.Xna.Framework;

namespace monogame.Planet;

internal enum CubeFace : byte {
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

internal sealed class Face : IDisposable {
    private readonly short[] _elevations;
    private readonly Chunk[] _chunks;

    internal Face(
        Sphere sphere,
        CubeFace id,
        short[] elevations,
        ChunkData[] chunks
    ) {
        ArgumentNullException.ThrowIfNull(sphere);
        ArgumentNullException.ThrowIfNull(elevations);
        ArgumentNullException.ThrowIfNull(chunks);
        if (!Enum.IsDefined(id)) {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        var expectedElevations = checked(sphere.FinestResolution * sphere.FinestResolution);
        if (elevations.Length != expectedElevations) {
            throw new ArgumentException("The face has an invalid elevation count.", nameof(elevations));
        }

        if (chunks.Length != GetChunkCount(sphere.MaximumLod)) {
            throw new ArgumentException("The face has an invalid chunk count.", nameof(chunks));
        }

        Sphere = sphere;
        Id = id;
        Orientation = GetOrientation(id);
        _elevations = elevations;
        _chunks = new Chunk[chunks.Length];
        for (var index = 0; index < chunks.Length; index++) {
            _chunks[index] = new Chunk(this, GetChunkId(id, index, sphere.MaximumLod), chunks[index]);
        }
    }

    public CubeFace Id { get; }

    public Chunk Root => GetChunk(new ChunkId(Id, 0, 0, 0));

    public float GetElevation(int x, int y) {
        var resolution = Sphere.FinestResolution;
        if ((uint)x >= (uint)resolution) {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)resolution) {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return GetElevationUnchecked(x, y);
    }

    internal Sphere Sphere { get; }

    internal Matrix Orientation { get; }

    internal ReadOnlySpan<Chunk> Chunks => _chunks;

    internal float GetElevationUnchecked(int x, int y) {
        return _elevations[y * Sphere.FinestResolution + x] * Sphere.ElevationQuantizationStep;
    }

    internal Chunk GetChunk(in ChunkId id) {
        if (id.Face != Id || id.Level > Sphere.MaximumLod) {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return _chunks[GetChunkIndex(id)];
    }

    internal void Select(Sphere.Selection selection) {
        Root.Select(selection);
    }

    public void Dispose() {
        foreach (var chunk in _chunks) {
            chunk.Dispose();
        }
    }

    internal static int GetChunkCount(int maximumLod) {
        var count = 0L;
        var chunksAtLevel = 1L;
        for (var level = 0; level <= maximumLod; level++) {
            count += chunksAtLevel;
            chunksAtLevel *= 4;
        }

        return checked((int)count);
    }

    internal static int GetChunkIndex(in ChunkId id) {
        return checked(GetLevelOffset(id.Level) + Morton(id.X, id.Y, id.Level));
    }

    internal static ChunkId GetChunkId(CubeFace face, int index, int maximumLod) {
        var chunkCount = GetChunkCount(maximumLod);
        if ((uint)index >= (uint)chunkCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var level = 0;
        var chunksAtLevel = 1;
        while (index >= chunksAtLevel) {
            index -= chunksAtLevel;
            chunksAtLevel *= 4;
            level++;
        }

        var x = 0;
        var y = 0;
        for (var bit = 0; bit < level; bit++) {
            x |= ((index >> (bit * 2)) & 1) << bit;
            y |= ((index >> (bit * 2 + 1)) & 1) << bit;
        }

        return new ChunkId(face, level, x, y);
    }

    private static int GetLevelOffset(int level) {
        var offset = 0;
        var chunksAtLevel = 1;
        for (var current = 0; current < level; current++) {
            offset = checked(offset + chunksAtLevel);
            chunksAtLevel = checked(chunksAtLevel * 4);
        }

        return offset;
    }

    private static int Morton(int x, int y, int level) {
        var result = 0;
        for (var bit = 0; bit < level; bit++) {
            result |= ((x >> bit) & 1) << (bit * 2);
            result |= ((y >> bit) & 1) << (bit * 2 + 1);
        }

        return result;
    }

    internal static Matrix GetOrientation(CubeFace face) {
        return face switch {
            CubeFace.PositiveX => CreateOrientation(-Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY),
            CubeFace.NegativeX => CreateOrientation(Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY),
            CubeFace.PositiveY => Matrix.Identity,
            CubeFace.NegativeY => CreateOrientation(Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ),
            CubeFace.PositiveZ => CreateOrientation(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
            CubeFace.NegativeZ => CreateOrientation(-Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    private static Matrix CreateOrientation(
        in Vector3 xAxis,
        in Vector3 yAxis,
        in Vector3 zAxis
    ) {
        return new Matrix(
            xAxis.X, xAxis.Y, xAxis.Z, 0.0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0.0f,
            zAxis.X, zAxis.Y, zAxis.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );
    }
}
