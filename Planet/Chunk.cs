using System;
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

    private readonly Face _face;
    private readonly ChunkData _data;
    private Chunk[] _children;
    private VertexBuffer _vertexBuffer;

    internal Chunk(Face face, in ChunkData data)
        : this(face, 0, 0, 0, 0, data) {
    }

    internal Chunk(Chunk parent, ChunkQuadrant quadrant, uint id, in ChunkData data)
        : this(
            parent._face,
            id,
            parent.Level + 1,
            checked(parent.X * 2 + ((int)quadrant & 1)),
            checked(parent.Y * 2 + (((int)quadrant >> 1) & 1)),
            data
        ) {
    }

    private Chunk(Face face, uint id, int level, int x, int y, in ChunkData data) {
        _face = face;
        _data = data;
        Id = id;
        Level = level;
        X = x;
        Y = y;
    }

    public uint Id { get; }

    public int Level { get; }

    public int X { get; }

    public int Y { get; }


    internal bool Update(in View view) {
        if (IsFullyBehindHorizon(view)) {
            ReleaseChildren();
            return false;
        }

        if (IsOutsideFrustum(view)) {
            ReleaseChildren();
            return false;
        }

        if (IsAtMaximumLod()) {
            ReleaseChildren();
            return true;
        }

        if (IsWithinSplitThreshold(view)) {
            ReleaseChildren();
            return true;
        }

        return UpdateChildren(view);
    }

    internal void Draw() {
        if (_children is not null) {
            foreach (var child in _children) {
                child?.Draw();
            }

            return;
        }

        DrawSelf();
    }

    public void Dispose() {
        ReleaseChildren();
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
    }

    private void DrawSelf() {
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

    private void EnsureVertexBuffer() {
        if (_vertexBuffer is not null) {
            return;
        }

        var sphere = _face.Sphere;
        var chunksPerAxis = 1 << Level;
        var size = 2.0f / chunksPerAxis;
        var position = new Vector2(-1.0f + X * size, -1.0f + Y * size);
        var sampleScale = 1 << (sphere.MaximumLod - Level);
        var cellsPerChunk = sphere.ChunkResolution - 1;
        var startX = X * cellsPerChunk * sampleScale;
        var startY = Y * cellsPerChunk * sampleScale;
        var orientation = _face.Orientation;
        var vertices = Mesh.CreateGrid(
            sphere.ChunkResolution,
            position,
            size,
            (x, y, point) => {
                var direction = Mesh.GetSphereDirection(point, orientation);
                var sampleX = startX + x * sampleScale;
                var sampleY = startY + y * sampleScale;
                var radius = sphere.ReferenceRadius + _face.GetElevation(sampleX, sampleY);
                return new VertexPosition(direction * radius);
            }
        );

        _vertexBuffer = new VertexBuffer(
            sphere.GraphicsDevice,
            VertexPosition.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        _vertexBuffer.SetData(vertices);
    }

    private bool UpdateChildren(in View view) {
        _children ??= new Chunk[4];
        var hasVisibleChild = false;

        for (var index = 0; index < _children.Length; index++) {
            var child = _children[index]
                ?? _face.CreateChild(this, (ChunkQuadrant)index);
            if (child.Update(view)) {
                _children[index] = child;
                hasVisibleChild = true;
            }
            else {
                child.Dispose();
                _children[index] = null;
            }
        }

        if (!hasVisibleChild) {
            _children = null;
        }

        return hasVisibleChild;
    }

    private void ReleaseChildren() {
        if (_children is null) {
            return;
        }

        foreach (var child in _children) {
            child?.Dispose();
        }

        _children = null;
    }

    private bool IsFullyBehindHorizon(in View view) {
        if (_data.HorizonPointRadius <= 0.0f) {
            return false;
        }

        var occluderRadius = _face.Sphere.OccluderRadius;
        var occluderRadiusSquared = (double)occluderRadius * occluderRadius;
        if (view.CameraLengthSquared <= occluderRadiusSquared) {
            return false;
        }

        var pointX = _data.CenterDirection.X * (double)_data.HorizonPointRadius;
        var pointY = _data.CenterDirection.Y * (double)_data.HorizonPointRadius;
        var pointZ = _data.CenterDirection.Z * (double)_data.HorizonPointRadius;
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

    private bool IsOutsideFrustum(in View view) {
        return view.Frustum.Contains(_data.BoundingSphere) == ContainmentType.Disjoint;
    }

    private bool IsAtMaximumLod() {
        return Level == _face.Sphere.MaximumLod;
    }

    private bool IsWithinSplitThreshold(in View view) {
        var centerLength = _data.CenterDirection.Length();
        var cosineTheta = Vector3.Dot(
            view.CameraPosition,
            _data.CenterDirection
        ) / (view.CameraLength * centerLength);
        var theta = Math.Acos(Math.Clamp(cosineTheta, -1.0, 1.0));
        var beta = Math.Max(0.0, theta - _data.AngularRadius);
        var cosineBeta = Math.Cos(beta);
        var radius = Math.Clamp(
            view.CameraLength * cosineBeta,
            _data.MinimumRadius,
            _data.MaximumRadius
        );
        var distanceSquared = view.CameraLengthSquared
            + radius * radius
            - 2.0 * view.CameraLength * radius * cosineBeta;

        var sphere = _face.Sphere;
        var distance = Math.Max(
            Math.Sqrt(Math.Max(0.0, distanceSquared)),
            sphere.DistanceFloor
        );
        var screenSpaceError = _data.GeometricError * view.FocalLength / distance;
        return screenSpaceError <= sphere.SplitThreshold;
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
