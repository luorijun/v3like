using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Utils;

namespace monogame.Planet;

internal readonly record struct ChunkId {
    public ChunkId(CubeFace face, int level, int x, int y) {
        var chunksPerAxis = level is >= 0 and <= 30 ? 1 << level : 0;
        if (!Enum.IsDefined(face)) {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        if (level < 0 || level > 30) {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if ((uint)x >= (uint)chunksPerAxis) {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)chunksPerAxis) {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        Face = face;
        Level = level;
        X = x;
        Y = y;
    }

    public CubeFace Face { get; }

    public int Level { get; }

    public int X { get; }

    public int Y { get; }

    public ChunkId Child(int x, int y) {
        if ((uint)x > 1) {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y > 1) {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return new ChunkId(Face, Level + 1, X * 2 + x, Y * 2 + y);
    }
}

internal sealed class Chunk : IDisposable {
    private readonly Face _face;
    private VertexBuffer _vertexBuffer;

    internal Chunk(Face face, in ChunkId id, in ChunkData data) {
        _face = face;
        Id = id;
        CenterDirection = data.CenterDirection;
        AngularRadius = data.AngularRadius;
        MinimumRadius = data.MinimumRadius;
        MaximumRadius = data.MaximumRadius;
        BoundingSphere = data.BoundingSphere;
        HorizonPointRadius = data.HorizonPointRadius;
        GeometricError = data.GeometricError;
    }

    public ChunkId Id { get; }

    public Vector3 CenterDirection { get; }

    public float AngularRadius { get; }

    public float MinimumRadius { get; }

    public float MaximumRadius { get; }

    public BoundingSphere BoundingSphere { get; }

    public float HorizonPointRadius { get; }

    public float GeometricError { get; }

    public MeshData CreateMesh() {
        var sphere = _face.Sphere;
        var chunksPerAxis = 1 << Id.Level;
        var size = 2.0f / chunksPerAxis;
        var position = new Vector2(-1.0f + Id.X * size, -1.0f + Id.Y * size);
        var directions = Mesh.CreateSphere(
            sphere.ChunkResolution,
            position,
            size,
            _face.Orientation
        );
        var directionPositions = directions.Positions;
        var positions = new Vector3[directionPositions.Length];
        var sampleScale = 1 << (sphere.MaximumLod - Id.Level);
        var cellsPerChunk = sphere.ChunkResolution - 1;
        var startX = Id.X * cellsPerChunk * sampleScale;
        var startY = Id.Y * cellsPerChunk * sampleScale;

        for (var y = 0; y < sphere.ChunkResolution; y++) {
            var sampleY = startY + y * sampleScale;
            for (var x = 0; x < sphere.ChunkResolution; x++) {
                var index = y * sphere.ChunkResolution + x;
                var sampleX = startX + x * sampleScale;
                var radius = sphere.ReferenceRadius + _face.GetElevationUnchecked(sampleX, sampleY);
                positions[index] = directionPositions[index] * radius;
            }
        }

        return new MeshData(sphere.ChunkResolution, positions);
    }

    internal void Select(Sphere.Selection selection) {
        if (IsFullyBehindHorizon(selection)
            || selection.View.Frustum.Contains(BoundingSphere) == ContainmentType.Disjoint) {
            return;
        }

        if (Id.Level == selection.MaximumLod) {
            selection.Add(this);
            return;
        }

        if (Id.Level < selection.MinimumLod) {
            SelectChildren(selection);
            return;
        }

        var distance = CalculateDistance(selection);
        var screenSpaceError = GeometricError * selection.View.FocalLength / distance;
        if (screenSpaceError <= selection.SplitThreshold) {
            selection.Add(this);
            return;
        }

        SelectChildren(selection);
    }

    internal void Draw() {
        EnsureVertexBuffer();
        var sphere = _face.Sphere;
        sphere.GraphicsDevice.SetVertexBuffer(_vertexBuffer);
        sphere.GraphicsDevice.DrawIndexedPrimitives(
            PrimitiveType.TriangleList,
            0,
            0,
            sphere.TriangleCount
        );
    }

    public void Dispose() {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
    }

    private void EnsureVertexBuffer() {
        if (_vertexBuffer is not null) {
            return;
        }

        var mesh = CreateMesh();
        var positions = mesh.Positions;
        var vertices = new VertexPositionColor[positions.Length];
        for (var index = 0; index < vertices.Length; index++) {
            vertices[index] = new VertexPositionColor(positions[index], new Color(82, 142, 96));
        }

        _vertexBuffer = new VertexBuffer(
            _face.Sphere.GraphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        _vertexBuffer.SetData(vertices);
    }

    private void SelectChildren(Sphere.Selection selection) {
        _face.GetChunk(Id.Child(0, 0)).Select(selection);
        _face.GetChunk(Id.Child(1, 0)).Select(selection);
        _face.GetChunk(Id.Child(0, 1)).Select(selection);
        _face.GetChunk(Id.Child(1, 1)).Select(selection);
    }

    private bool IsFullyBehindHorizon(Sphere.Selection selection) {
        if (HorizonPointRadius <= 0.0f) {
            return false;
        }

        var occluderRadius = selection.OccluderRadius;
        var occluderRadiusSquared = (double)occluderRadius * occluderRadius;
        if (selection.View.CameraLengthSquared <= occluderRadiusSquared) {
            return false;
        }

        var pointX = CenterDirection.X * (double)HorizonPointRadius;
        var pointY = CenterDirection.Y * (double)HorizonPointRadius;
        var pointZ = CenterDirection.Z * (double)HorizonPointRadius;
        var vectorX = pointX - selection.View.CameraPosition.X;
        var vectorY = pointY - selection.View.CameraPosition.Y;
        var vectorZ = pointZ - selection.View.CameraPosition.Z;
        var vectorLengthSquared = vectorX * vectorX + vectorY * vectorY + vectorZ * vectorZ;
        if (vectorLengthSquared <= 0.0) {
            return false;
        }

        var projection = -(
            selection.View.CameraPosition.X * vectorX
            + selection.View.CameraPosition.Y * vectorY
            + selection.View.CameraPosition.Z * vectorZ
        );
        var cameraHorizonSquared = selection.View.CameraLengthSquared - occluderRadiusSquared;
        return projection > 0.0
            && projection < vectorLengthSquared
            && projection * projection > cameraHorizonSquared * vectorLengthSquared;
    }

    private double CalculateDistance(Sphere.Selection selection) {
        var centerLength = Math.Sqrt(LengthSquared(CenterDirection));
        var cosineTheta = (
            selection.View.CameraPosition.X * CenterDirection.X
            + selection.View.CameraPosition.Y * CenterDirection.Y
            + selection.View.CameraPosition.Z * CenterDirection.Z
        ) / (selection.View.CameraLength * centerLength);
        var theta = Math.Acos(Math.Clamp(cosineTheta, -1.0, 1.0));
        var beta = Math.Max(0.0, theta - AngularRadius);
        var cosineBeta = Math.Cos(beta);
        var radius = Math.Clamp(
            selection.View.CameraLength * cosineBeta,
            MinimumRadius,
            MaximumRadius
        );
        var distanceSquared = selection.View.CameraLengthSquared
            + radius * radius
            - 2.0 * selection.View.CameraLength * radius * cosineBeta;
        return Math.Max(Math.Sqrt(Math.Max(0.0, distanceSquared)), selection.DistanceFloor);
    }

    private static double LengthSquared(in Vector3 value) {
        return (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
    }
}
