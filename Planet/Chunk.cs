using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Terrain;

namespace monogame.Planet;

internal sealed class Chunk : IDisposable {
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Face _face;
    private readonly int _resolution;
    private readonly int _maximumLod;
    private readonly float _splitThresholdPixels;
    private readonly int _triangleCount;
    private readonly float _minimumU;
    private readonly float _minimumV;
    private readonly float _maximumU;
    private readonly float _maximumV;
    private readonly Vector3 _boundsCenter;
    private readonly float _boundsRadius;
    private readonly VertexBuffer _vertexBuffer;

    private Chunk[] _children;

    public Chunk(
        GraphicsDevice graphicsDevice,
        Face face,
        int level,
        int x,
        int y,
        int resolution,
        int maximumLod,
        float splitThresholdPixels,
        int triangleCount) {
        _graphicsDevice = graphicsDevice;
        _face = face;
        _resolution = resolution;
        _maximumLod = maximumLod;
        _splitThresholdPixels = splitThresholdPixels;
        _triangleCount = triangleCount;

        Level = level;
        X = x;
        Y = y;

        var chunksPerAxis = 1 << level;
        var chunkSize = 2.0f / chunksPerAxis;
        _minimumU = -1.0f + x * chunkSize;
        _minimumV = -1.0f + y * chunkSize;
        _maximumU = _minimumU + chunkSize;
        _maximumV = _minimumV + chunkSize;

        (_boundsCenter, _boundsRadius) = CalculateBounds();
        _vertexBuffer = CreateVertexBuffer();
    }

    public int Level { get; }

    public int X { get; }

    public int Y { get; }

    public void Update(in SphereView view, ref int visibleChunkCount, ref int deepestLod) {
        if (ShouldSplit(view)) {
            EnsureChildren();
            foreach (var child in _children) {
                child.Update(view, ref visibleChunkCount, ref deepestLod);
            }

            return;
        }

        RemoveChildren();
        visibleChunkCount++;
        deepestLod = Math.Max(deepestLod, Level);
    }

    public void Draw() {
        if (_children is not null) {
            foreach (var child in _children) {
                child.Draw();
            }

            return;
        }

        _graphicsDevice.SetVertexBuffer(_vertexBuffer);
        _graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    public void Dispose() {
        RemoveChildren();
        _vertexBuffer.Dispose();
    }

    private bool ShouldSplit(in SphereView view) {
        if (Level >= _maximumLod) {
            return false;
        }

        var distanceToBounds = Math.Max(0.0001f, Vector3.Distance(view.CameraPosition, _boundsCenter) - _boundsRadius);
        var viewportHeight = Math.Max(1, view.ViewportHeight);
        var focalLength = viewportHeight * 0.5f
            / MathF.Tan(view.VerticalFieldOfView * 0.5f);
        var projectedDiameter = _boundsRadius * 2.0f * focalLength / distanceToBounds;
        return projectedDiameter > _splitThresholdPixels;
    }

    private void EnsureChildren() {
        if (_children is not null) {
            return;
        }

        var childLevel = Level + 1;
        var childX = X * 2;
        var childY = Y * 2;
        _children = [
            CreateChild(childLevel, childX, childY),
            CreateChild(childLevel, childX + 1, childY),
            CreateChild(childLevel, childX, childY + 1),
            CreateChild(childLevel, childX + 1, childY + 1),
        ];
    }

    private Chunk CreateChild(int level, int x, int y) {
        return new Chunk(
            _graphicsDevice,
            _face,
            level,
            x,
            y,
            _resolution,
            _maximumLod,
            _splitThresholdPixels,
            _triangleCount
        );
    }

    private void RemoveChildren() {
        if (_children is null) {
            return;
        }

        foreach (var child in _children) {
            child.Dispose();
        }

        _children = null;
    }

    private (Vector3 Center, float Radius) CalculateBounds() {
        var middleU = (_minimumU + _maximumU) * 0.5f;
        var middleV = (_minimumV + _maximumV) * 0.5f;
        var center = _face.Project(middleU, middleV) * (1.0f + ProceduralTerrain.MaximumElevation * 0.5f);

        var radius = 0.0f;
        radius = Math.Max(radius, Vector3.Distance(center, _face.Project(_minimumU, _minimumV)));
        radius = Math.Max(radius, Vector3.Distance(center, _face.Project(_maximumU, _minimumV)));
        radius = Math.Max(radius, Vector3.Distance(center, _face.Project(_minimumU, _maximumV)));
        radius = Math.Max(radius, Vector3.Distance(center, _face.Project(_maximumU, _maximumV)));
        return (center, radius + ProceduralTerrain.MaximumElevation);
    }

    private VertexBuffer CreateVertexBuffer() {
        var stepU = (_maximumU - _minimumU) / (_resolution - 1);
        var stepV = (_maximumV - _minimumV) / (_resolution - 1);
        var vertices = new TerrainVertex[_resolution * _resolution];

        for (var y = 0; y < _resolution; y++) {
            var v = _minimumV + y * stepV;
            for (var x = 0; x < _resolution; x++) {
                var u = _minimumU + x * stepU;
                var direction = _face.Project(u, v);
                var terrain = ProceduralTerrain.Sample(direction);
                var position = direction * (1.0f + terrain.Elevation);
                var normal = CalculateNormal(u, v, stepU, stepV, direction);
                vertices[y * _resolution + x] = new TerrainVertex(position, normal, terrain.Color);
            }
        }

        var vertexBuffer = new VertexBuffer(_graphicsDevice, TerrainVertex.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private Vector3 CalculateNormal(float u, float v, float stepU, float stepV, Vector3 outwardDirection) {
        var left = SurfacePoint(u - stepU, v);
        var right = SurfacePoint(u + stepU, v);
        var bottom = SurfacePoint(u, v - stepV);
        var top = SurfacePoint(u, v + stepV);
        var normal = Vector3.Normalize(Vector3.Cross(right - left, top - bottom));
        return Vector3.Dot(normal, outwardDirection) >= 0.0f ? normal : -normal;
    }

    private Vector3 SurfacePoint(float u, float v) {
        var direction = _face.Project(u, v);
        var elevation = ProceduralTerrain.Sample(direction).Elevation;
        return direction * (1.0f + elevation);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct TerrainVertex : IVertexType {
        public static readonly VertexDeclaration VertexDeclaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0)
        );

        public TerrainVertex(Vector3 position, Vector3 normal, Color color) {
            Position = position;
            Normal = normal;
            Color = color;
        }

        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Color Color;

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
    }
}
