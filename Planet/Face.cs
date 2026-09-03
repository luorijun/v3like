using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace monogame.Planet;

internal enum FaceId : byte {
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

internal sealed class Face {
    private static readonly Matrix[] s_orientations = [
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Right),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Left),
        Matrix.Identity,
        Matrix.CreateWorld(Vector3.Zero, Vector3.Backward, Vector3.Down),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Backward),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Forward),
    ];

    private readonly FaceData _data;
    private readonly uint[] _activeIds;
    private readonly bool[] _splitStates;
    private int _activeCount;

    internal Face(Sphere sphere, FaceId id, FaceData data) {
        ArgumentNullException.ThrowIfNull(sphere);
        if (!Enum.IsDefined(id)) {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        Sphere = sphere;
        Id = id;
        _data = data;
        _activeIds = new uint[data.Chunks.Length];
        _splitStates = new bool[data.Chunks.Length];
    }

    public FaceId Id { get; }

    internal Sphere Sphere { get; }

    internal Matrix Orientation => GetOrientation(Id);

    internal float GetElevation(int x, int y) {
        return _data.GetElevationUnchecked(x, y);
    }

    internal void Update(in View view, ref SelectionCounters counters) {
        _activeCount = 0;
        UpdateChunk(0, 0, view, ref counters);
    }

    internal void TouchCachedChunks(Cache cache) {
        for (var index = 0; index < _activeCount; index++) {
            cache.Touch(GetCacheKey(_activeIds[index]));
        }
    }

    internal void DrawSurface(
        Cache cache,
        Effect surfaceEffect,
        EffectParameter tileIndexTextureParameter,
        ref DrawCounters counters
    ) {
        for (var index = 0; index < _activeCount; index++) {
            var chunk = GetChunk(cache, _activeIds[index], ref counters);
            chunk.DrawSurface(surfaceEffect, tileIndexTextureParameter, ref counters);
        }
    }

    internal void DrawWireframe(
        Cache cache,
        BasicEffect effect,
        ref DrawCounters counters
    ) {
        for (var index = 0; index < _activeCount; index++) {
            var chunk = GetChunk(cache, _activeIds[index], ref counters);
            chunk.DrawWireframe(effect, ref counters);
        }
    }

    private Chunk GetChunk(Cache cache, uint id, ref DrawCounters counters) {
        var key = GetCacheKey(id);
        var chunk = cache.Get(key, ref counters);
        if (chunk is not null) {
            return chunk;
        }

        chunk = new Chunk(this, id);
        if (cache.Add(key, chunk)) {
            counters.CacheEvictions++;
        }

        return chunk;
    }

    private void UpdateChunk(
        uint id,
        int level,
        in View view,
        ref SelectionCounters counters
    ) {
        counters.VisitedNodes++;
        var chunkIndex = checked((int)id);
        ref readonly var data = ref _data.Chunks[chunkIndex];

        if (Chunk.IsFullyBehindHorizon(data, Sphere, view)) {
            counters.HorizonRejected++;
            return;
        }

        counters.FrustumTests++;
        if (Chunk.IsOutsideFrustum(data, view)) {
            counters.FrustumRejected++;
            return;
        }

        var threshold = _splitStates[chunkIndex]
            ? Sphere.MergeThreshold
            : Sphere.SplitThreshold;
        if (Chunk.IsAtMaximumLod(level, Sphere.MaximumLod) || Chunk.IsWithinThreshold(data, Sphere, view, threshold)) {
            _splitStates[chunkIndex] = false;
            _activeIds[_activeCount++] = id;
            counters.ActiveChunks++;
            return;
        }

        _splitStates[chunkIndex] = true;
        for (var index = 0; index < 4; index++) {
            UpdateChunk(
                Chunk.GetChildId(id, (ChunkQuadrant)index),
                level + 1,
                view,
                ref counters
            );
        }
    }

    private int GetCacheKey(uint id) {
        return checked((int)Id * _data.Chunks.Length + (int)id);
    }

    internal static Matrix GetOrientation(FaceId face) {
        return s_orientations[(int)face];
    }
}
