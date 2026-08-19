using System;
using Microsoft.Xna.Framework;

namespace monogame.Planet.Gen;

internal enum TerrainCubeFace : byte {
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

internal readonly record struct CubeSurfaceKey(int X, int Y, int Z);

internal static class CubeSphereGrid {
    public const int FaceCount = 6;

    public static CubeSurfaceKey GetSurfaceKey(TerrainCubeFace face, int x, int y, int intervals) {
        var u = checked(x * 2 - intervals);
        var v = checked(y * 2 - intervals);
        return face switch {
            TerrainCubeFace.PositiveX => new CubeSurfaceKey(intervals, v, -u),
            TerrainCubeFace.NegativeX => new CubeSurfaceKey(-intervals, v, u),
            TerrainCubeFace.PositiveY => new CubeSurfaceKey(u, intervals, -v),
            TerrainCubeFace.NegativeY => new CubeSurfaceKey(u, -intervals, v),
            TerrainCubeFace.PositiveZ => new CubeSurfaceKey(u, v, intervals),
            TerrainCubeFace.NegativeZ => new CubeSurfaceKey(-u, v, -intervals),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    public static Vector3 GetDirection(TerrainCubeFace face, int x, int y, int intervals) {
        var key = GetSurfaceKey(face, x, y, intervals);
        return Vector3.Normalize(new Vector3(key.X, key.Y, key.Z));
    }

    public static Vector3 GetDirection(TerrainCubeFace face, float u, float v) {
        var cubePoint = face switch {
            TerrainCubeFace.PositiveX => new Vector3(1.0f, v, -u),
            TerrainCubeFace.NegativeX => new Vector3(-1.0f, v, u),
            TerrainCubeFace.PositiveY => new Vector3(u, 1.0f, -v),
            TerrainCubeFace.NegativeY => new Vector3(u, -1.0f, v),
            TerrainCubeFace.PositiveZ => new Vector3(u, v, 1.0f),
            TerrainCubeFace.NegativeZ => new Vector3(-u, v, -1.0f),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
        return Vector3.Normalize(cubePoint);
    }
}
