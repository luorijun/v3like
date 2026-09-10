using System;
using System.Collections.Generic;
using monogame.Debugging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Utils;

namespace monogame.Lod;

internal sealed class Sphere : IDisposable {
    private const int CubeFaceCount = 6;
    private const int LatitudeSegmentCount = 256;
    private const float TropicLatitudeDegrees = 23.44f;
    private const float GuideSurfaceOffset = 0.001f;
    private const float WireframeDepthBias = -0.00001f;
    private const float RotationAxisHalfLength = 1.15f;
    private const int LodColorCycleLength = 6;
    private const float LodColorSaturation = 0.72f;
    private const float LodColorBrightness = 0.9f;
    private const double LodHysteresisRatio = 0.15;

    private readonly List<Chunk> _activeChunks = [];
    private readonly Effect _surfaceEffect;
    private readonly EffectTechnique _surfaceTechnique;
    private readonly EffectTechnique _wireframeTechnique;
    private readonly EffectParameter _worldViewProjectionParameter;
    private readonly EffectParameter _faceOrientationParameter;
    private readonly EffectParameter _chunkPositionParameter;
    private readonly EffectParameter _chunkSizeParameter;
    private readonly EffectParameter _solidColorParameter;
    private readonly BasicEffect _guideLineEffect;
    private readonly VertexBuffer _chunkMeshVertexBuffer;
    private readonly IndexBuffer _chunkMeshIndexBuffer;
    private readonly VertexBuffer _guideLineVertexBuffer;
    private readonly int _chunkMeshPrimitiveCount;
    private readonly int _guideLinePrimitiveCount;
    private readonly RasterizerState _solidRasterizerState;
    private readonly RasterizerState _wireframeRasterizerState;
    private readonly int _meshResolution;
    private readonly double _pixelsPerCell;
    private SelectionCounters _selectionCounters;
    private SelectionMetrics _selectionMetrics;
    private int _targetLodLevel = -1;

    internal Sphere(in SphereConfiguration configuration) {
        ValidateConfiguration(configuration);

        _meshResolution = configuration.MeshResolution;
        _pixelsPerCell = configuration.PixelsPerCell;

        var meshVertexCount = checked(_meshResolution + 1);
        _chunkMeshVertexBuffer = CreateChunkMeshVertexBuffer(meshVertexCount);
        var indices = Mesh.CreateTriangleIndices(meshVertexCount);
        _chunkMeshIndexBuffer = new IndexBuffer(
            GameManager.GraphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly
        );
        _chunkMeshIndexBuffer.SetData(indices);
        _chunkMeshPrimitiveCount = indices.Length / 3;

        _surfaceEffect = GameManager.SurfaceEffect;
        _surfaceTechnique = GetRequiredTechnique(_surfaceEffect, "TileSurface");
        _wireframeTechnique = GetRequiredTechnique(_surfaceEffect, "SolidColor");
        _worldViewProjectionParameter = GetRequiredParameter(_surfaceEffect, "WorldViewProjection");
        _faceOrientationParameter = GetRequiredParameter(_surfaceEffect, "FaceOrientation");
        _chunkPositionParameter = GetRequiredParameter(_surfaceEffect, "ChunkPosition");
        _chunkSizeParameter = GetRequiredParameter(_surfaceEffect, "ChunkSize");
        _solidColorParameter = GetRequiredParameter(_surfaceEffect, "SurfaceColor");

        _guideLineEffect = new BasicEffect(GameManager.GraphicsDevice) {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _guideLineVertexBuffer = CreateGuideLineVertexBuffer();
        _guideLinePrimitiveCount = _guideLineVertexBuffer.VertexCount / 2;

        _solidRasterizerState = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.Solid,
        };
        _wireframeRasterizerState = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.WireFrame,
            DepthBias = WireframeDepthBias,
        };
    }

    internal SelectionMetrics Metrics => _selectionMetrics;

    internal void Update(in View view, double minimumHeight) {
        using var timing = Debugger.Measure("Chunk selection");
        var maxLod = SelectTargetLodLevel(view.FocalLength, minimumHeight, 0);
        _targetLodLevel = SelectTargetLodLevel(view.FocalLength,
            Math.Max(view.CameraLength - 1.0, minimumHeight),
            Math.Clamp(_targetLodLevel, 0, maxLod));
        _activeChunks.Clear();
        _selectionCounters = default;

        for (var face = 0; face < CubeFaceCount; face++) {
            SelectVisibleChunks(new Chunk((FaceId)face, 0, 0, 0), view);
        }

        _selectionMetrics = new SelectionMetrics(
            _targetLodLevel,
            maxLod,
            _selectionCounters.VisitedNodes,
            _activeChunks.Count,
            _selectionCounters.HorizonRejected,
            _selectionCounters.FrustumRejected
        );
    }

    internal void Draw(in View view, in SphereRenderOptions options) {
        _worldViewProjectionParameter.SetValue(view.ViewProjection);
        GameManager.GraphicsDevice.SetVertexBuffer(_chunkMeshVertexBuffer);
        GameManager.GraphicsDevice.Indices = _chunkMeshIndexBuffer;

        if (options.ShowSurface) {
            DrawSurface();
        }

        if (options.ShowWireframe) {
            DrawWireframe();
        }

        if (options.ShowGuideLines) {
            DrawGuideLines(view.ViewProjection);
        }
    }

    public void Dispose() {
        _wireframeRasterizerState.Dispose();
        _solidRasterizerState.Dispose();
        _guideLineVertexBuffer.Dispose();
        _chunkMeshIndexBuffer.Dispose();
        _chunkMeshVertexBuffer.Dispose();
        _guideLineEffect.Dispose();
    }

    private int SelectTargetLodLevel(double focalLength, double height, int previousLevel) {
        var rootCellPixels = MathHelper.PiOver2 * focalLength / (height * _meshResolution);

        var splitThreshold = _pixelsPerCell * (1.0 + LodHysteresisRatio);
        var mergeThreshold = _pixelsPerCell * (1.0 - LodHysteresisRatio);
        var selectedLevel = previousLevel;
        var cellPixels = Math.ScaleB(
            rootCellPixels,
            -selectedLevel
        );
        while (cellPixels > splitThreshold) {
            selectedLevel++;
            cellPixels *= 0.5;
        }

        while (selectedLevel > 0
            && cellPixels * 2.0 < mergeThreshold) {
            selectedLevel--;
            cellPixels *= 2.0;
        }

        return selectedLevel;
    }

    private void SelectVisibleChunks(in Chunk chunk, in View view) {
        _selectionCounters.VisitedNodes++;
        var bounds = ChunkGeometry.CalculateBounds(chunk);
        if (ChunkGeometry.IsFullyBehindHorizon(bounds, view)) {
            _selectionCounters.HorizonRejected++;
            return;
        }

        if (ChunkGeometry.IsOutsideFrustum(bounds, view)) {
            _selectionCounters.FrustumRejected++;
            return;
        }

        if (chunk.Level == _targetLodLevel) {
            _activeChunks.Add(chunk);
            return;
        }

        for (var quadrant = 0; quadrant < 4; quadrant++) {
            SelectVisibleChunks(chunk.GetChild(quadrant), view);
        }
    }

    private void DrawSurface() {
        using var timing = Debugger.Measure("Surface");
        GameManager.GraphicsDevice.BlendState = BlendState.Opaque;
        GameManager.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GameManager.GraphicsDevice.RasterizerState = _solidRasterizerState;
        _surfaceEffect.CurrentTechnique = _surfaceTechnique;

        foreach (var chunk in _activeChunks) {
            ConfigureChunk(chunk);
            DrawChunk();
        }
    }

    private void DrawWireframe() {
        using var timing = Debugger.Measure("Wireframe");
        GameManager.GraphicsDevice.BlendState = BlendState.Opaque;
        GameManager.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        GameManager.GraphicsDevice.RasterizerState = _wireframeRasterizerState;
        _surfaceEffect.CurrentTechnique = _wireframeTechnique;
        _solidColorParameter.SetValue(CreateLodColor(_targetLodLevel));

        foreach (var chunk in _activeChunks) {
            ConfigureChunk(chunk);
            DrawChunk();
        }
    }

    private void ConfigureChunk(in Chunk chunk) {
        chunk.GetFaceRegion(out var position, out var size);
        _faceOrientationParameter.SetValue(CubeFace.GetOrientation(chunk.Face));
        _chunkPositionParameter.SetValue(position);
        _chunkSizeParameter.SetValue(size);
    }

    private void DrawChunk() {
        foreach (var pass in _surfaceEffect.CurrentTechnique.Passes) {
            pass.Apply();
            GameManager.GraphicsDevice.DrawIndexedPrimitives(
                PrimitiveType.TriangleList,
                0,
                0,
                _chunkMeshPrimitiveCount
            );
        }
    }

    private void DrawGuideLines(in Matrix viewProjection) {
        using var timing = Debugger.Measure("Guide lines");
        _guideLineEffect.World = Matrix.Identity;
        _guideLineEffect.View = Matrix.Identity;
        _guideLineEffect.Projection = viewProjection;
        GameManager.GraphicsDevice.BlendState = BlendState.Opaque;
        GameManager.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        GameManager.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
        GameManager.GraphicsDevice.SetVertexBuffer(_guideLineVertexBuffer);

        foreach (var pass in _guideLineEffect.CurrentTechnique.Passes) {
            pass.Apply();
            GameManager.GraphicsDevice.DrawPrimitives(
                PrimitiveType.LineList,
                0,
                _guideLinePrimitiveCount
            );
        }
    }

    private static VertexBuffer CreateChunkMeshVertexBuffer(int meshVertexCount) {
        var vertices = Mesh.CreateGrid(
            meshVertexCount,
            Vector2.Zero,
            1.0f,
            (_, _, point) => new VertexPosition(new Vector3(point, 0.0f))
        );
        var vertexBuffer = new VertexBuffer(
            GameManager.GraphicsDevice,
            VertexPosition.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static void ValidateConfiguration(in SphereConfiguration configuration) {
        var resolution = configuration.MeshResolution;
        if (resolution <= 0 || (resolution & (resolution - 1)) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Mesh resolution must be a positive power of two."
            );
        }

        if (!double.IsFinite(configuration.PixelsPerCell) || configuration.PixelsPerCell <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Pixels per mesh cell must be finite and positive."
            );
        }

        var meshVertexCount = (long)configuration.MeshResolution + 1;
        if (meshVertexCount * meshVertexCount > ushort.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Mesh resolution must fit in a 16-bit indexed mesh."
            );
        }
    }

    private static VertexBuffer CreateGuideLineVertexBuffer() {
        VertexPositionColor[] vertices = [
            .. CreateLatitudeLine(
                1.0f + GuideSurfaceOffset,
                0.0f,
                Color.Gold
            ),
            .. CreateLatitudeLine(
                1.0f + GuideSurfaceOffset,
                TropicLatitudeDegrees,
                Color.OrangeRed
            ),
            .. CreateLatitudeLine(
                1.0f + GuideSurfaceOffset,
                -TropicLatitudeDegrees,
                Color.OrangeRed
            ),
            .. CreateRotationAxis(RotationAxisHalfLength),
        ];
        var vertexBuffer = new VertexBuffer(
            GameManager.GraphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static VertexPositionColor[] CreateLatitudeLine(float radius, float latitudeDegrees, Color color) {
        var latitude = MathHelper.ToRadians(latitudeDegrees);
        var y = MathF.Sin(latitude) * radius;
        var horizontalRadius = MathF.Cos(latitude) * radius;

        return Mesh.CreateLineList(
            LatitudeSegmentCount,
            progress => {
                var longitude = MathHelper.TwoPi * progress;
                var position = new Vector3(
                    MathF.Cos(longitude) * horizontalRadius,
                    y,
                    MathF.Sin(longitude) * horizontalRadius
                );
                return new VertexPositionColor(position, color);
            }
        );
    }

    private static VertexPositionColor[] CreateRotationAxis(float halfLength) {
        return [
            new VertexPositionColor(Vector3.UnitY * -halfLength, Color.Cyan),
            new VertexPositionColor(Vector3.UnitY * halfLength, Color.Cyan),
        ];
    }

    private static Vector3 CreateLodColor(int level) {
        var maximum = LodColorBrightness;
        var minimum = LodColorBrightness * (1.0f - LodColorSaturation);
        return (level % LodColorCycleLength) switch {
            0 => new Vector3(maximum, minimum, minimum),
            1 => new Vector3(maximum, maximum, minimum),
            2 => new Vector3(minimum, maximum, minimum),
            3 => new Vector3(minimum, maximum, maximum),
            4 => new Vector3(minimum, minimum, maximum),
            _ => new Vector3(maximum, minimum, maximum),
        };
    }

    private static EffectParameter GetRequiredParameter(Effect effect, string name) {
        return effect.Parameters[name]
            ?? throw new InvalidOperationException(
                $"The surface effect is missing its {name} parameter."
            );
    }

    private static EffectTechnique GetRequiredTechnique(Effect effect, string name) {
        return effect.Techniques[name]
            ?? throw new InvalidOperationException(
                $"The surface effect is missing its {name} technique."
            );
    }
}

internal readonly record struct SphereRenderOptions(
    bool ShowSurface,
    bool ShowWireframe,
    bool ShowGuideLines
);

internal readonly record struct SphereConfiguration(
    int MeshResolution,
    double PixelsPerCell
);
