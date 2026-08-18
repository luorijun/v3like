using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Utils;

namespace monogame.Sphere;

internal sealed class CubeSphere : IDisposable
{
    private const int ChunkResolution = 17;
    private const int MaximumLod = 7;
    private const float SplitThresholdPixels = 220.0f;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly BasicEffect _effect;
    private readonly IndexBuffer _indexBuffer;
    private readonly RasterizerState _rasterizerState;
    private readonly CubeFace[] _faces;

    public CubeSphere(GraphicsDevice graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;
        _effect = CreateEffect(graphicsDevice);
        _indexBuffer = CreateIndexBuffer(graphicsDevice);
        _rasterizerState = new RasterizerState
        {
            CullMode = CullMode.CullCounterClockwiseFace,
            FillMode = FillMode.Solid,
        };

        var triangleCount = _indexBuffer.IndexCount / 3;
        _faces = new CubeFace[CubeSphereProjection.FaceBases.Length];
        for (var index = 0; index < _faces.Length; index++)
        {
            _faces[index] = new CubeFace(
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

    public void Draw(Matrix view, Matrix projection)
    {
        _graphicsDevice.BlendState = BlendState.Opaque;
        _graphicsDevice.DepthStencilState = DepthStencilState.Default;
        _graphicsDevice.RasterizerState = _rasterizerState;
        _graphicsDevice.Indices = _indexBuffer;

        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            foreach (var face in _faces)
            {
                face.Draw();
            }
        }
    }

    public void Dispose()
    {
        foreach (var face in _faces)
        {
            face.Dispose();
        }

        _indexBuffer.Dispose();
        _effect.Dispose();
        _rasterizerState.Dispose();
    }

    private static BasicEffect CreateEffect(GraphicsDevice graphicsDevice)
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
}

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
