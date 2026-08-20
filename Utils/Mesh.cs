using System;
using Microsoft.Xna.Framework;

namespace monogame.Utils;

internal sealed class MeshData {
    private readonly Vector3[] _positions;

    internal MeshData(int resolution, Vector3[] positions) {
        Resolution = resolution;
        _positions = positions;
    }

    public int Resolution { get; }

    public ReadOnlySpan<Vector3> Positions => _positions;
}

internal static class Mesh {
    public static MeshData CreatePlane(int resolution) {
        ValidateResolution(resolution);

        var intervals = resolution - 1;
        var scale = 1.0f / intervals;
        var positions = new Vector3[checked(resolution * resolution)];
        for (var y = 0; y < resolution; y++) {
            var v = checked(y * 2 - intervals);
            for (var x = 0; x < resolution; x++) {
                var u = checked(x * 2 - intervals);
                positions[y * resolution + x] = new Vector3(u * scale, 0.0f, -v * scale);
            }
        }

        return new MeshData(resolution, positions);
    }

    public static MeshData CreateSphere(
        int resolution,
        Vector2 position,
        float size,
        in Matrix orientation
    ) {
        ValidateResolution(resolution);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!float.IsFinite(size) || size <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (position.X < -1.0f || position.Y < -1.0f
            || position.X + size > 1.0f
            || position.Y + size > 1.0f) {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var intervals = resolution - 1;
        var startU = position.X * intervals;
        var startV = position.Y * intervals;
        var positions = new Vector3[checked(resolution * resolution)];
        for (var y = 0; y < resolution; y++) {
            var v = startV + y * size;
            for (var x = 0; x < resolution; x++) {
                var u = startU + x * size;
                var spherePosition = Vector3.Normalize(new Vector3(u, intervals, -v));
                positions[y * resolution + x] = Vector3.TransformNormal(spherePosition, orientation);
            }
        }

        return new MeshData(resolution, positions);
    }

    public static Vector3 GetSpherePosition(float u, float v, in Matrix orientation) {
        var position = Vector3.Normalize(new Vector3(u, 1.0f, -v));
        return Vector3.TransformNormal(position, orientation);
    }

    public static ushort[] CreateTriangleIndices(int resolution) {
        ValidateResolution(resolution);
        if ((long)resolution * resolution > ushort.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(resolution), "The mesh has more vertices than a 16-bit index can address.");
        }

        var indices = new ushort[(resolution - 1) * (resolution - 1) * 6];
        var index = 0;

        for (var y = 0; y < resolution - 1; y++) {
            for (var x = 0; x < resolution - 1; x++) {
                var upperLeft = (ushort)(y * resolution + x);
                var upperRight = (ushort)(upperLeft + 1);
                var lowerLeft = (ushort)(upperLeft + resolution);
                var lowerRight = (ushort)(lowerLeft + 1);

                indices[index++] = upperLeft;
                indices[index++] = upperRight;
                indices[index++] = lowerLeft;
                indices[index++] = upperRight;
                indices[index++] = lowerRight;
                indices[index++] = lowerLeft;
            }
        }

        return indices;
    }

    private static void ValidateResolution(int resolution) {
        if (resolution < 2) {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
    }
}
