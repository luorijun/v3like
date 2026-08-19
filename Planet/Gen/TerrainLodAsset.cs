using System;
using Microsoft.Xna.Framework;

namespace monogame.Planet.Gen;

internal readonly record struct TerrainLodBuildSettings(
    float ReferenceRadius,
    int ChunkResolution,
    int MaximumLod,
    float ElevationQuantizationStep
) {
    public int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

    public int FinestResolution => checked(FinestIntervals + 1);

    public void Validate() {
        if (!float.IsFinite(ReferenceRadius) || ReferenceRadius <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(ReferenceRadius));
        }

        if (ChunkResolution < 2) {
            throw new ArgumentOutOfRangeException(nameof(ChunkResolution));
        }

        if (MaximumLod is < 0 or > 14) {
            throw new ArgumentOutOfRangeException(nameof(MaximumLod));
        }

        if (!float.IsFinite(ElevationQuantizationStep) || ElevationQuantizationStep <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(ElevationQuantizationStep));
        }

        _ = FinestResolution;
        _ = TerrainLodNodeIndex.GetNodesPerFace(MaximumLod);
    }
}

internal readonly record struct TerrainLodNodeId(
    TerrainCubeFace Face,
    int Level,
    int X,
    int Y
);

internal readonly record struct TerrainLodNode(
    TerrainLodNodeId Id,
    Vector3 CenterDirection,
    float AngularRadius,
    float MinimumRadius,
    float MaximumRadius,
    BoundingSphere BoundingSphere,
    float HorizonPointRadius,
    float GeometricError
);

internal sealed class TerrainLodAsset {
    private readonly short[][] _quantizedFaceElevations;
    private readonly TerrainLodNode[] _nodes;
    private readonly byte[] _referenceGeometryHash;

    internal TerrainLodAsset(
        TerrainLodBuildSettings settings,
        short[][] quantizedFaceElevations,
        TerrainLodNode[] nodes,
        float occluderRadius,
        byte[] referenceGeometryHash) {
        Settings = settings;
        _quantizedFaceElevations = quantizedFaceElevations;
        _nodes = nodes;
        OccluderRadius = occluderRadius;
        _referenceGeometryHash = referenceGeometryHash;
    }

    public TerrainLodBuildSettings Settings { get; }

    public ReadOnlySpan<TerrainLodNode> Nodes => _nodes;

    public float OccluderRadius { get; }

    public ReadOnlySpan<byte> ReferenceGeometryHash => _referenceGeometryHash;

    public ReadOnlySpan<short> GetQuantizedElevations(TerrainCubeFace face) {
        return _quantizedFaceElevations[(int)face];
    }

    public float GetElevation(TerrainCubeFace face, int x, int y) {
        var resolution = Settings.FinestResolution;
        if ((uint)x >= (uint)resolution || (uint)y >= (uint)resolution) {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        return _quantizedFaceElevations[(int)face][y * resolution + x]
            * Settings.ElevationQuantizationStep;
    }

    public TerrainLodNode GetNode(in TerrainLodNodeId id) {
        return _nodes[TerrainLodNodeIndex.GetIndex(id, Settings.MaximumLod)];
    }
}

internal static class TerrainLodNodeIndex {
    public static int GetIndex(in TerrainLodNodeId id, int maximumLod) {
        var chunksPerAxis = id.Level is >= 0 and <= 30 ? 1 << id.Level : 0;
        if ((uint)id.Face >= CubeSphereGrid.FaceCount
            || id.Level < 0
            || id.Level > maximumLod
            || (uint)id.X >= (uint)chunksPerAxis
            || (uint)id.Y >= (uint)chunksPerAxis) {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        var nodesPerFace = GetNodesPerFace(maximumLod);
        return checked((int)id.Face * nodesPerFace + GetLevelOffset(id.Level) + Morton(id.X, id.Y, id.Level));
    }

    public static TerrainLodNodeId GetId(int index, int maximumLod) {
        var nodeCount = GetNodeCount(maximumLod);
        if ((uint)index >= (uint)nodeCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var nodesPerFace = GetNodesPerFace(maximumLod);
        var face = (TerrainCubeFace)(index / nodesPerFace);
        var faceIndex = index % nodesPerFace;
        var level = 0;
        var nodesAtLevel = 1;
        while (faceIndex >= nodesAtLevel) {
            faceIndex -= nodesAtLevel;
            nodesAtLevel *= 4;
            level++;
        }

        var x = 0;
        var y = 0;
        for (var bit = 0; bit < level; bit++) {
            x |= ((faceIndex >> (bit * 2)) & 1) << bit;
            y |= ((faceIndex >> (bit * 2 + 1)) & 1) << bit;
        }

        return new TerrainLodNodeId(face, level, x, y);
    }

    public static int GetNodeCount(int maximumLod) {
        return checked(CubeSphereGrid.FaceCount * GetNodesPerFace(maximumLod));
    }

    public static int GetNodesPerFace(int maximumLod) {
        var count = 0L;
        var nodesAtLevel = 1L;
        for (var level = 0; level <= maximumLod; level++) {
            count += nodesAtLevel;
            nodesAtLevel *= 4;
        }

        return checked((int)count);
    }

    private static int GetLevelOffset(int level) {
        var offset = 0;
        var nodesAtLevel = 1;
        for (var current = 0; current < level; current++) {
            offset = checked(offset + nodesAtLevel);
            nodesAtLevel = checked(nodesAtLevel * 4);
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
}
