using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Terrain;
using monogame.Utils;

namespace monogame.Planet;

internal sealed class Sphere : IDisposable
{
    private const int ChunkResolution = 17;
    private const int MaximumLod = 7;
    private const float SplitThresholdPixels = 220.0f;
    private const int GuideLineSegments = 256;
    private const int RotationAxisSegments = 64;
    private const float TropicLatitudeDegrees = 23.44f;
    private const float GuideLineSurfaceOffset = 0.001f;
    private const float RotationAxisHalfLength = 1.15f;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly BasicEffect _surfaceEffect;
    private readonly BasicEffect _wireframeEffect;
    private readonly BasicEffect _guideLineEffect;
    private readonly IndexBuffer _indexBuffer;
    private readonly VertexBuffer _guideLineVertexBuffer;
    private readonly int _guideLinePrimitiveCount;
    private readonly RasterizerState _solidRasterizerState;
    private readonly RasterizerState _wireframeRasterizerState;
    private readonly Face[] _faces;

    public Sphere(GraphicsDevice graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;
        _surfaceEffect = CreateSurfaceEffect(graphicsDevice);
        _wireframeEffect = CreateWireframeEffect(graphicsDevice);
        _guideLineEffect = CreateGuideLineEffect(graphicsDevice);
        _indexBuffer = CreateIndexBuffer(graphicsDevice);
        _guideLineVertexBuffer = CreateGuideLineVertexBuffer(graphicsDevice);
        _guideLinePrimitiveCount = _guideLineVertexBuffer.VertexCount / 2;
        _solidRasterizerState = new RasterizerState
        {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.Solid,
        };
        _wireframeRasterizerState = new RasterizerState
        {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.WireFrame,
        };

        var triangleCount = _indexBuffer.IndexCount / 3;
        _faces = new Face[CubeSphereProjection.FaceBases.Length];
        for (var index = 0; index < _faces.Length; index++)
        {
            _faces[index] = new Face(
                graphicsDevice,
                CubeSphereProjection.FaceBases[index],
                ChunkResolution,
                MaximumLod,
                SplitThresholdPixels,
                triangleCount);
        }
    }

    public int DeepestLod { get; private set; }

    public int VisibleChunkCount { get; private set; }

    public int VisibleTriangleCount => VisibleChunkCount * (_indexBuffer.IndexCount / 3);

    public void Update(Vector3 cameraPosition, float verticalFieldOfView, int viewportHeight)
    {
        var view = new SphereView(cameraPosition, verticalFieldOfView, viewportHeight);
        var visibleChunkCount = 0;
        var deepestLod = 0;

        foreach (var face in _faces)
        {
            face.Update(view, ref visibleChunkCount, ref deepestLod);
        }

        VisibleChunkCount = visibleChunkCount;
        DeepestLod = deepestLod;
    }

    public void Draw(
        Matrix view,
        Matrix projection,
        in PlanetRenderOptions options)
    {
        ConfigureEffect(_surfaceEffect, view, projection);
        ConfigureEffect(_wireframeEffect, view, projection);
        ConfigureEffect(_guideLineEffect, view, projection);

        if (options.ShowSurface)
        {
            DrawSphereGeometry(
                _surfaceEffect,
                BlendState.Opaque,
                DepthStencilState.Default,
                _solidRasterizerState);
        }

        if (options.ShowWireframe)
        {
            DrawSphereGeometry(
                _wireframeEffect,
                BlendState.Opaque,
                DepthStencilState.None,
                _wireframeRasterizerState);
        }

        if (options.ShowGuideLines)
        {
            DrawGuideLines();
        }
    }

    public void Dispose()
    {
        foreach (var face in _faces)
        {
            face.Dispose();
        }

        _indexBuffer.Dispose();
        _guideLineVertexBuffer.Dispose();
        _surfaceEffect.Dispose();
        _wireframeEffect.Dispose();
        _guideLineEffect.Dispose();
        _solidRasterizerState.Dispose();
        _wireframeRasterizerState.Dispose();
    }

    private void DrawSphereGeometry(
        BasicEffect effect,
        BlendState blendState,
        DepthStencilState depthStencilState,
        RasterizerState rasterizerState)
    {
        _graphicsDevice.BlendState = blendState;
        _graphicsDevice.DepthStencilState = depthStencilState;
        _graphicsDevice.RasterizerState = rasterizerState;
        _graphicsDevice.Indices = _indexBuffer;

        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            foreach (var face in _faces)
            {
                face.Draw();
            }
        }
    }

    private void DrawGuideLines()
    {
        _graphicsDevice.BlendState = BlendState.Opaque;
        _graphicsDevice.DepthStencilState = DepthStencilState.None;
        _graphicsDevice.RasterizerState = RasterizerState.CullNone;
        _graphicsDevice.SetVertexBuffer(_guideLineVertexBuffer);

        foreach (var pass in _guideLineEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphicsDevice.DrawPrimitives(
                PrimitiveType.LineList,
                0,
                _guideLinePrimitiveCount);
        }
    }

    private static void ConfigureEffect(
        BasicEffect effect,
        Matrix view,
        Matrix projection)
    {
        effect.World = Matrix.Identity;
        effect.View = view;
        effect.Projection = projection;
    }

    private static BasicEffect CreateSurfaceEffect(GraphicsDevice graphicsDevice)
    {
        var effect = new BasicEffect(graphicsDevice)
        {
            VertexColorEnabled = true,
            LightingEnabled = true,
            PreferPerPixelLighting = true,
            TextureEnabled = false,
            AmbientLightColor = new Vector3(0.42f, 0.42f, 0.48f),
        };

        effect.EnableDefaultLighting();
        effect.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(-0.6f, -0.4f, -0.7f));
        effect.DirectionalLight0.DiffuseColor = new Vector3(0.92f, 0.9f, 0.82f);
        effect.DirectionalLight1.Enabled = false;
        effect.DirectionalLight2.Enabled = false;
        return effect;
    }

    private static BasicEffect CreateWireframeEffect(GraphicsDevice graphicsDevice)
    {
        return new BasicEffect(graphicsDevice)
        {
            VertexColorEnabled = false,
            LightingEnabled = false,
            TextureEnabled = false,
            DiffuseColor = new Vector3(0.3f, 0.88f, 1.0f),
        };
    }

    private static BasicEffect CreateGuideLineEffect(GraphicsDevice graphicsDevice)
    {
        return new BasicEffect(graphicsDevice)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
    }

    private static IndexBuffer CreateIndexBuffer(GraphicsDevice graphicsDevice)
    {
        var indices = RegularGrid.CreateTriangleIndices(ChunkResolution);
        var indexBuffer = new IndexBuffer(
            graphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly);
        indexBuffer.SetData(indices);
        return indexBuffer;
    }

    private static VertexBuffer CreateGuideLineVertexBuffer(GraphicsDevice graphicsDevice)
    {
        var vertices = CreateGuideLineVertices();
        var vertexBuffer = new VertexBuffer(
            graphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly);
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static VertexPositionColor[] CreateGuideLineVertices()
    {
        var vertices = new VertexPositionColor[
            (GuideLineSegments * 3 + RotationAxisSegments) * 2];
        var vertexIndex = 0;
        var radius = 1.0f
            + ProceduralTerrain.MaximumElevation
            + GuideLineSurfaceOffset;

        AddLatitudeLine(vertices, ref vertexIndex, radius, 0.0f, Color.Gold);
        AddLatitudeLine(
            vertices,
            ref vertexIndex,
            radius,
            TropicLatitudeDegrees,
            Color.OrangeRed);
        AddLatitudeLine(
            vertices,
            ref vertexIndex,
            radius,
            -TropicLatitudeDegrees,
            Color.OrangeRed);

        AddRotationAxis(vertices, ref vertexIndex);
        return vertices;
    }

    private static void AddLatitudeLine(
        VertexPositionColor[] vertices,
        ref int vertexIndex,
        float radius,
        float latitudeDegrees,
        Color color)
    {
        var latitude = MathHelper.ToRadians(latitudeDegrees);
        var y = MathF.Sin(latitude) * radius;
        var horizontalRadius = MathF.Cos(latitude) * radius;

        for (var segment = 0; segment < GuideLineSegments; segment++)
        {
            var startLongitude = MathHelper.TwoPi * segment / GuideLineSegments;
            var endLongitude = MathHelper.TwoPi * (segment + 1) / GuideLineSegments;
            vertices[vertexIndex++] = new VertexPositionColor(
                LatitudePoint(horizontalRadius, y, startLongitude),
                color);
            vertices[vertexIndex++] = new VertexPositionColor(
                LatitudePoint(horizontalRadius, y, endLongitude),
                color);
        }
    }

    private static Vector3 LatitudePoint(
        float horizontalRadius,
        float y,
        float longitude)
    {
        return new Vector3(
            MathF.Cos(longitude) * horizontalRadius,
            y,
            MathF.Sin(longitude) * horizontalRadius);
    }

    private static void AddRotationAxis(
        VertexPositionColor[] vertices,
        ref int vertexIndex)
    {
        var axisLength = RotationAxisHalfLength * 2.0f;
        for (var segment = 0; segment < RotationAxisSegments; segment++)
        {
            var startY = -RotationAxisHalfLength
                + axisLength * segment / RotationAxisSegments;
            var endY = -RotationAxisHalfLength
                + axisLength * (segment + 1) / RotationAxisSegments;
            vertices[vertexIndex++] = new VertexPositionColor(
                Vector3.UnitY * startY,
                Color.Cyan);
            vertices[vertexIndex++] = new VertexPositionColor(
                Vector3.UnitY * endY,
                Color.Cyan);
        }
    }
}

internal readonly record struct PlanetRenderOptions(
    bool ShowSurface,
    bool ShowWireframe,
    bool ShowGuideLines);

internal readonly record struct SphereView(
    Vector3 CameraPosition,
    float VerticalFieldOfView,
    int ViewportHeight);

internal readonly record struct CubeFaceBasis(
    Vector3 Normal,
    Vector3 UAxis,
    Vector3 VAxis);

internal static class CubeSphereProjection
{
    internal static readonly CubeFaceBasis[] FaceBases =
    [
        new(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
        new(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        new(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
        new(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        new(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
        new(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
    ];

    public static Vector3 ToDirection(in CubeFaceBasis face, float u, float v)
    {
        return Vector3.Normalize(face.Normal + u * face.UAxis + v * face.VAxis);
    }
}
