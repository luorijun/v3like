using System;
using Microsoft.Xna.Framework;

namespace monogame.Utils;

internal static class Mesh {
    public static TVertex[] CreateLineList<TVertex>(
        int segmentCount,
        Func<float, TVertex> createVertex
    ) where TVertex : struct {
        ArgumentNullException.ThrowIfNull(createVertex);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentCount, 1);

        var vertices = new TVertex[checked(segmentCount * 2)];
        for (var segment = 0; segment < segmentCount; segment++) {
            vertices[segment * 2] = createVertex((float)segment / segmentCount);
            vertices[segment * 2 + 1] = createVertex((float)(segment + 1) / segmentCount);
        }

        return vertices;
    }

    public static TVertex[] CreateGrid<TVertex>(
        int resolution,
        Vector2 position,
        float size,
        Func<int, int, Vector2, TVertex> createVertex
    ) where TVertex : struct {
        ArgumentNullException.ThrowIfNull(createVertex);
        ValidateResolution(resolution);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!float.IsFinite(size) || size <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var intervals = resolution - 1;
        var step = size / intervals;
        var vertices = new TVertex[checked(resolution * resolution)];
        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                var point = new Vector2(
                    position.X + x * step,
                    position.Y + y * step
                );
                vertices[y * resolution + x] = createVertex(x, y, point);
            }
        }

        return vertices;
    }

    public static Vector3 GetSphereDirection(in Vector2 point, in Matrix orientation) {
        const float faceHalfAngle = MathF.PI * 0.25f;
        var x = MathF.Abs(point.X) == 1.0f
            ? point.X
            : MathF.Tan(point.X * faceHalfAngle);
        var z = MathF.Abs(point.Y) == 1.0f
            ? point.Y
            : MathF.Tan(point.Y * faceHalfAngle);
        var direction = Vector3.Normalize(new Vector3(x, 1.0f, -z));
        return Vector3.TransformNormal(direction, orientation);
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
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 2);
    }
}
