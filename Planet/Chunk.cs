using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Utils;

namespace monogame.Planet;

internal enum ChunkQuadrant : byte {
    LowerLeft,
    LowerRight,
    UpperLeft,
    UpperRight,
}

internal sealed class Chunk : IDisposable {
    internal const int MaximumLevel = 14;

    private const int LodColorCount = 6;
    private const float LodColorSaturation = 0.72f;
    private const float LodColorValue = 0.9f;

    private readonly Face _face;
    private VertexBuffer _vertexBuffer;

    internal Chunk(Face face, uint id) {
        _face = face;
        Id = id;

        var level = 0;
        var x = 0;
        var y = 0;
        while (id > 0) {
            var encoded = id - 1;
            var quadrant = encoded & 3;
            x |= checked((int)(quadrant & 1) << level);
            y |= checked((int)((quadrant >> 1) & 1) << level);
            id = encoded >> 2;
            level++;
        }

        Level = level;
        X = x;
        Y = y;
    }

    public uint Id { get; }

    public int Level { get; }

    public int X { get; }

    public int Y { get; }

    internal void Draw(ref DrawCounters counters) {
        EnsureVertexBuffer(ref counters);
        var sphere = _face.Sphere;
        sphere.GraphicsDevice.SetVertexBuffer(_vertexBuffer);
        sphere.GraphicsDevice.DrawIndexedPrimitives(
            PrimitiveType.TriangleList,
            0,
            0,
            sphere.TriangleCount
        );
        counters.DrawCalls++;
    }

    public void Dispose() {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
    }

    private void EnsureVertexBuffer(ref DrawCounters counters) {
        if (_vertexBuffer is not null) {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var sphere = _face.Sphere;
        var chunksPerAxis = 1 << Level;
        var size = 2.0f / chunksPerAxis;
        var position = new Vector2(-1.0f + X * size, -1.0f + Y * size);
        var sampleScale = 1 << (sphere.MaximumLod - Level);
        var cellsPerChunk = sphere.ChunkResolution - 1;
        var startX = X * cellsPerChunk * sampleScale;
        var startY = Y * cellsPerChunk * sampleScale;
        var orientation = _face.Orientation;
        var color = CreateLodColor(Level);
        var vertices = Mesh.CreateGrid(
            sphere.ChunkResolution,
            position,
            size,
            (x, y, point) => {
                var direction = Mesh.GetSphereDirection(point, orientation);
                var sampleX = startX + x * sampleScale;
                var sampleY = startY + y * sampleScale;
                var radius = 1.0f + _face.GetElevation(sampleX, sampleY);
                return new VertexPositionColor(direction * radius, color);
            }
        );

        _vertexBuffer = new VertexBuffer(
            sphere.GraphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        _vertexBuffer.SetData(vertices);
        counters.MeshBuilds++;
        counters.MeshBuildTimestampTicks += Stopwatch.GetTimestamp() - started;
    }

    private static Color CreateLodColor(int level) {
        var maximum = LodColorValue;
        var minimum = LodColorValue * (1.0f - LodColorSaturation);
        var color = (level % LodColorCount) switch {
            0 => new Vector3(maximum, minimum, minimum),
            1 => new Vector3(maximum, maximum, minimum),
            2 => new Vector3(minimum, maximum, minimum),
            3 => new Vector3(minimum, maximum, maximum),
            4 => new Vector3(minimum, minimum, maximum),
            _ => new Vector3(maximum, minimum, maximum),
        };
        return new Color(color);
    }

    internal static bool IsFullyBehindHorizon(
        in ChunkData data,
        Sphere sphere,
        in View view
    ) {
        if (data.HorizonPointRadius <= 0.0f) {
            return false;
        }

        var occluderRadius = sphere.OccluderRadius;
        var occluderRadiusSquared = (double)occluderRadius * occluderRadius;
        if (view.CameraLengthSquared <= occluderRadiusSquared) {
            return false;
        }

        var pointX = data.CenterDirection.X * (double)data.HorizonPointRadius;
        var pointY = data.CenterDirection.Y * (double)data.HorizonPointRadius;
        var pointZ = data.CenterDirection.Z * (double)data.HorizonPointRadius;
        var vectorX = pointX - view.CameraPosition.X;
        var vectorY = pointY - view.CameraPosition.Y;
        var vectorZ = pointZ - view.CameraPosition.Z;
        var vectorLengthSquared = vectorX * vectorX + vectorY * vectorY + vectorZ * vectorZ;
        if (vectorLengthSquared <= 0.0) {
            return false;
        }

        var projection = -(
            view.CameraPosition.X * vectorX
            + view.CameraPosition.Y * vectorY
            + view.CameraPosition.Z * vectorZ
        );
        var cameraHorizonSquared = view.CameraLengthSquared - occluderRadiusSquared;
        return projection > 0.0
            && projection < vectorLengthSquared
            && projection * projection > cameraHorizonSquared * vectorLengthSquared;
    }

    internal static bool IsOutsideFrustum(in ChunkData data, in View view) {
        return view.Frustum.Contains(data.BoundingSphere) == ContainmentType.Disjoint;
    }

    internal static bool IsAtMaximumLod(int level, int maximumLod) {
        return level == maximumLod;
    }

    internal static bool IsWithinThreshold(
        in ChunkData data,
        Sphere sphere,
        in View view,
        float threshold
    ) {
        var centerLength = data.CenterDirection.Length();
        var cosineTheta = Vector3.Dot(
            view.CameraPosition,
            data.CenterDirection
        ) / (view.CameraLength * centerLength);
        var theta = Math.Acos(Math.Clamp(cosineTheta, -1.0, 1.0));
        var beta = Math.Max(0.0, theta - data.AngularRadius);
        var cosineBeta = Math.Cos(beta);
        var radius = Math.Clamp(
            view.CameraLength * cosineBeta,
            data.MinimumRadius,
            data.MaximumRadius
        );
        var distanceSquared = view.CameraLengthSquared
            + radius * radius
            - 2.0 * view.CameraLength * radius * cosineBeta;

        var distance = Math.Max(
            Math.Sqrt(Math.Max(0.0, distanceSquared)),
            sphere.DistanceFloor
        );
        var screenSpaceError = data.GeometricError * view.FocalLength / distance;
        return screenSpaceError <= threshold;
    }

    internal static uint GetId(int level, int x, int y) {
        var id = 0u;
        for (var bit = level - 1; bit >= 0; bit--) {
            var quadrant = ((x >> bit) & 1) | (((y >> bit) & 1) << 1);
            id = GetChildId(id, (ChunkQuadrant)quadrant);
        }
        return id;
    }

    internal static uint GetChildId(uint parentId, ChunkQuadrant quadrant) {
        return checked(parentId * 4 + 1 + (uint)quadrant);
    }

}
