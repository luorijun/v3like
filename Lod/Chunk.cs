using System;
using Microsoft.Xna.Framework;
using monogame.Utils;

namespace monogame.Lod;

internal readonly record struct Chunk {
    internal Chunk(FaceId face, int level, int x, int y) {
        if (!Enum.IsDefined(face)) {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        // The chunk uses positive, signed 32-bit counts (1 << level).
        if (level is < 0 or > 30) {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        var chunksPerAxis = 1 << level;
        if (x < 0 || x >= chunksPerAxis) {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (y < 0 || y >= chunksPerAxis) {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        Face = face;
        Level = level;
        X = x;
        Y = y;
    }

    internal FaceId Face { get; }

    internal int Level { get; }

    internal int X { get; }

    internal int Y { get; }

    internal Chunk GetChild(int quadrant) {
        if (quadrant is < 0 or > 3) {
            throw new ArgumentOutOfRangeException(nameof(quadrant));
        }

        return new Chunk(
            Face,
            Level + 1,
            X * 2 + (quadrant & 1),
            Y * 2 + ((quadrant >> 1) & 1)
        );
    }

    internal void GetFaceRegion(out Vector2 position, out float size) {
        var chunksPerAxis = 1 << Level;
        size = 2.0f / chunksPerAxis;
        position = new Vector2(
            -1.0f + X * size,
            -1.0f + Y * size
        );
    }
}

internal readonly record struct ChunkBounds(
    Vector3 CenterDirection,
    float AngularRadius,
    BoundingSphere Sphere
);

internal static class ChunkGeometry {
    private const float AngularSafetyMargin = 1e-5f;
    private const double HorizonComparisonMargin = 1e-7;

    internal static ChunkBounds CalculateBounds(in Chunk chunk) {
        chunk.GetFaceRegion(out var position, out var size);
        var orientation = CubeFace.GetOrientation(chunk.Face);
        var centerPoint = position + new Vector2(size * 0.5f);
        var center = Mesh.GetSphereDirection(centerPoint, orientation);
        var maximumPoint = position + new Vector2(size);
        var minimumDot = 1.0f;

        AccumulateCorner(position.X, position.Y);
        AccumulateCorner(maximumPoint.X, position.Y);
        AccumulateCorner(position.X, maximumPoint.Y);
        AccumulateCorner(maximumPoint.X, maximumPoint.Y);

        var angularRadius = MathF.Min(
            MathHelper.PiOver2,
            MathF.Acos(Math.Clamp(minimumDot, -1.0f, 1.0f)) + AngularSafetyMargin
        );
        var cosineRadius = MathF.Cos(angularRadius);
        var bounds = new BoundingSphere(
            center * cosineRadius,
            MathF.Sin(angularRadius)
        );
        return new ChunkBounds(center, angularRadius, bounds);

        void AccumulateCorner(float x, float y) {
            var corner = Mesh.GetSphereDirection(new Vector2(x, y), orientation);
            minimumDot = MathF.Min(minimumDot, Vector3.Dot(center, corner));
        }
    }

    internal static bool IsFullyBehindHorizon(in ChunkBounds bounds, in View view) {
        if (view.CameraLength <= 1.0) {
            return false;
        }

        var horizonAngle = Math.Acos(1.0 / view.CameraLength);
        var rejectionAngle = horizonAngle + bounds.AngularRadius;
        if (rejectionAngle >= Math.PI) {
            return false;
        }

        var inverseCameraLength = 1.0 / view.CameraLength;
        var centerDot = (
            view.CameraPosition.X * (double)bounds.CenterDirection.X
            + view.CameraPosition.Y * (double)bounds.CenterDirection.Y
            + view.CameraPosition.Z * (double)bounds.CenterDirection.Z
        ) * inverseCameraLength;
        return centerDot < Math.Cos(rejectionAngle) - HorizonComparisonMargin;
    }

    internal static bool IsOutsideFrustum(in ChunkBounds bounds, in View view) {
        return view.Frustum.Contains(bounds.Sphere) == ContainmentType.Disjoint;
    }
}
