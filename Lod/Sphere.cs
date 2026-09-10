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
    private readonly double _hysteresis;

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
    private readonly VertexBuffer _meshVertices;
    private readonly IndexBuffer _meshIndices;
    private readonly VertexBuffer _guideVertices;
    private readonly int _meshPrimitives;
    private readonly int _guidePrimitives;
    private readonly RasterizerState _solidRasterizer;
    private readonly RasterizerState _wireRasterizer;
    private readonly int _meshResolution;
    private readonly double _pixelsPerCell;
    private SelectionCounters _counters;
    private SelectionMetrics _metrics;
    private int _lod = -1;

    internal Sphere(in SphereConfig config) {
        ValidateConfig(config);

        _meshResolution = config.MeshResolution;
        _pixelsPerCell = config.PixelsPerCell;
        _hysteresis = config.Hysteresis;

        var meshVertexCount = checked(_meshResolution + 1);
        _meshVertices = CreateChunkMeshVertexBuffer(meshVertexCount);
        var indices = Mesh.CreateTriangleIndices(meshVertexCount);
        _meshIndices = new IndexBuffer(
            GameManager.GraphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly
        );
        _meshIndices.SetData(indices);
        _meshPrimitives = indices.Length / 3;

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
        _guideVertices = CreateGuideLineVertexBuffer();
        _guidePrimitives = _guideVertices.VertexCount / 2;

        _solidRasterizer = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.Solid,
        };
        _wireRasterizer = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.WireFrame,
            DepthBias = WireframeDepthBias,
        };
    }

    internal SelectionMetrics Metrics => _metrics;

    internal void Update(in View view, double minHeight) {
        using var timing = Debugger.Measure("Chunk selection");
        var maxLod = SelectTargetLodLevel(view.FocalLength, minHeight, 0);
        _lod = SelectTargetLodLevel(view.FocalLength,
            Math.Max(view.CameraLength - 1.0, minHeight),
            Math.Clamp(_lod, 0, maxLod));
        _activeChunks.Clear();
        _counters = default;

        for (var face = 0; face < CubeFaceCount; face++) {
            SelectVisibleChunks(new Chunk((FaceId)face, 0, 0, 0), view);
        }

        _metrics = new SelectionMetrics(
            _lod,
            maxLod,
            _counters.VisitedNodes,
            _activeChunks.Count,
            _counters.HorizonRejected,
            _counters.FrustumRejected
        );
    }

    internal void Draw(in View view, in SphereRenderOptions options) {
        _worldViewProjectionParameter.SetValue(view.ViewProjection);
        GameManager.GraphicsDevice.SetVertexBuffer(_meshVertices);
        GameManager.GraphicsDevice.Indices = _meshIndices;

        if (options.Surface) {
            DrawSurface();
        }

        if (options.Wireframe) {
            DrawWireframe();
        }

        if (options.Guides) {
            DrawGuideLines(view.ViewProjection);
        }
    }

    public void Dispose() {
        _wireRasterizer.Dispose();
        _solidRasterizer.Dispose();
        _guideVertices.Dispose();
        _meshIndices.Dispose();
        _meshVertices.Dispose();
        _guideLineEffect.Dispose();
    }

    private int SelectTargetLodLevel(double focalLength, double height, int previousLevel) {
        var rootCellPixels = MathHelper.PiOver2 * focalLength / (height * _meshResolution);

        var splitThreshold = _pixelsPerCell * (1.0 + _hysteresis);
        var mergeThreshold = _pixelsPerCell * (1.0 - _hysteresis);
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
        _counters.VisitedNodes++;
        var bounds = ChunkGeometry.CalculateBounds(chunk);
        if (ChunkGeometry.IsFullyBehindHorizon(bounds, view)) {
            _counters.HorizonRejected++;
            return;
        }

        if (ChunkGeometry.IsOutsideFrustum(bounds, view)) {
            _counters.FrustumRejected++;
            return;
        }

        if (chunk.Level == _lod) {
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
        GameManager.GraphicsDevice.RasterizerState = _solidRasterizer;
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
        GameManager.GraphicsDevice.RasterizerState = _wireRasterizer;
        _surfaceEffect.CurrentTechnique = _wireframeTechnique;
        _solidColorParameter.SetValue(CreateLodColor(_lod));

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
                _meshPrimitives
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
        GameManager.GraphicsDevice.SetVertexBuffer(_guideVertices);

        foreach (var pass in _guideLineEffect.CurrentTechnique.Passes) {
            pass.Apply();
            GameManager.GraphicsDevice.DrawPrimitives(
                PrimitiveType.LineList,
                0,
                _guidePrimitives
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

    private static void ValidateConfig(in SphereConfig config) {
        if (!double.IsFinite(config.Hysteresis)
            || config.Hysteresis < 0 || config.Hysteresis >= 1) {
            throw new ArgumentOutOfRangeException(nameof(config), "LOD hysteresis ratio must be in [0, 1).");
        }
        var resolution = config.MeshResolution;
        if (resolution <= 0 || (resolution & (resolution - 1)) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                "Mesh resolution must be a positive power of two."
            );
        }

        if (!double.IsFinite(config.PixelsPerCell) || config.PixelsPerCell <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                "Pixels per mesh cell must be finite and positive."
            );
        }

        var meshVertexCount = (long)config.MeshResolution + 1;
        if (meshVertexCount * meshVertexCount > ushort.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(config),
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
    bool Surface,
    bool Wireframe,
    bool Guides
);

internal readonly record struct SphereConfig(
    int MeshResolution,
    double PixelsPerCell,
    double Hysteresis
);
