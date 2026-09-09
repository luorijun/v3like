using System;
using System.Collections.Generic;
using monogame.Debugging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame.Utils;

namespace monogame.Planet;

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
    private const double TargetPixelsPerTexel = 1.0;
    private const double LodHysteresisPixelsPerTexel = 0.15;
    private const double SplitPixelsPerTexel =
        TargetPixelsPerTexel + LodHysteresisPixelsPerTexel;
    private const double MergePixelsPerTexel =
        TargetPixelsPerTexel - LodHysteresisPixelsPerTexel;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly LogicalGrid _logicalGrid;
    private readonly ChunkCache _chunkCache;
    private readonly List<ChunkAddress> _activeChunkAddresses = [];
    private readonly Effect _surfaceEffect;
    private readonly EffectTechnique _surfaceTechnique;
    private readonly EffectTechnique _wireframeTechnique;
    private readonly EffectParameter _worldViewProjectionParameter;
    private readonly EffectParameter _faceOrientationParameter;
    private readonly EffectParameter _chunkPositionParameter;
    private readonly EffectParameter _chunkSizeParameter;
    private readonly EffectParameter _indexTextureParameter;
    private readonly EffectParameter _solidColorParameter;
    private readonly BasicEffect _guideLineEffect;
    private readonly VertexBuffer _chunkMeshVertexBuffer;
    private readonly IndexBuffer _chunkMeshIndexBuffer;
    private readonly VertexBuffer _guideLineVertexBuffer;
    private readonly int _chunkMeshPrimitiveCount;
    private readonly int _guideLinePrimitiveCount;
    private readonly RasterizerState _solidRasterizerState;
    private readonly RasterizerState _wireframeRasterizerState;
    private readonly int _textureResolution;
    private readonly int _textureSampleCount;
    private readonly int _maximumLodLevel;
    private SelectionCounters _selectionCounters;
    private SelectionMetrics _selectionMetrics;
    private int _targetLodLevel = -1;

    private Sphere(
        GraphicsDevice graphicsDevice,
        Effect surfaceEffect,
        int meshResolution,
        int textureResolution,
        int chunkCacheCapacity
    ) {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentNullException.ThrowIfNull(surfaceEffect);

        _graphicsDevice = graphicsDevice;
        _logicalGrid = new LogicalGrid();
        _chunkCache = new ChunkCache(chunkCacheCapacity);

        _textureResolution = textureResolution;
        _textureSampleCount = checked(textureResolution + 1);

        _maximumLodLevel = CalculateMaximumLodLevel(meshResolution);

        var meshVertexCount = checked(meshResolution + 1);
        _chunkMeshVertexBuffer = CreateChunkMeshVertexBuffer(graphicsDevice, meshVertexCount, _textureSampleCount);
        var indices = Mesh.CreateTriangleIndices(meshVertexCount);
        _chunkMeshIndexBuffer = new IndexBuffer(
            graphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly
        );
        _chunkMeshIndexBuffer.SetData(indices);
        _chunkMeshPrimitiveCount = indices.Length / 3;

        _surfaceEffect = surfaceEffect.Clone();
        _surfaceTechnique = GetRequiredTechnique(_surfaceEffect, "TileSurface");
        _wireframeTechnique = GetRequiredTechnique(_surfaceEffect, "SolidColor");
        _worldViewProjectionParameter = GetRequiredParameter(_surfaceEffect, "WorldViewProjection");
        _faceOrientationParameter = GetRequiredParameter(_surfaceEffect, "FaceOrientation");
        _chunkPositionParameter = GetRequiredParameter(_surfaceEffect, "ChunkPosition");
        _chunkSizeParameter = GetRequiredParameter(_surfaceEffect, "ChunkSize");
        _indexTextureParameter = GetRequiredParameter(_surfaceEffect, "TileIndexTexture");
        _solidColorParameter = GetRequiredParameter(_surfaceEffect, "SurfaceColor");

        _guideLineEffect = new BasicEffect(graphicsDevice) {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _guideLineVertexBuffer = CreateGuideLineVertexBuffer(graphicsDevice);
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

    internal static Sphere Create(
        in SphereConfiguration configuration,
        GraphicsDevice graphicsDevice,
        Effect surfaceEffect
    ) {
        ValidateConfiguration(configuration);
        return new Sphere(
            graphicsDevice,
            surfaceEffect,
            configuration.MeshResolution,
            configuration.TextureResolution,
            configuration.ChunkCacheCapacity
        );
    }

    internal void Update(in View view) {
        using var timing = Debugger.Measure("Chunk selection");
        _targetLodLevel = SelectTargetLodLevel(view, _targetLodLevel);
        _activeChunkAddresses.Clear();
        _selectionCounters = default;

        for (var face = 0; face < CubeFaceCount; face++) {
            SelectVisibleChunks(new ChunkAddress((FaceId)face, 0, 0, 0), view);
        }

        _selectionMetrics = new SelectionMetrics(
            _targetLodLevel,
            _selectionCounters.VisitedNodes,
            _activeChunkAddresses.Count,
            _selectionCounters.HorizonRejected,
            _selectionCounters.FrustumRejected
        );
    }

    internal void Draw(in View view, in PlanetRenderOptions options) {
        _worldViewProjectionParameter.SetValue(view.ViewProjection);
        _graphicsDevice.SetVertexBuffer(_chunkMeshVertexBuffer);
        _graphicsDevice.Indices = _chunkMeshIndexBuffer;

        if (options.ShowSurface) {
            foreach (var address in _activeChunkAddresses) {
                _chunkCache.Touch(address);
            }

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
        _chunkCache.Dispose();
        _wireframeRasterizerState.Dispose();
        _solidRasterizerState.Dispose();
        _guideLineVertexBuffer.Dispose();
        _chunkMeshIndexBuffer.Dispose();
        _chunkMeshVertexBuffer.Dispose();
        _guideLineEffect.Dispose();
        _surfaceEffect.Dispose();
    }

    private int SelectTargetLodLevel(in View view, int previousLevel) {
        var surfaceDistance = Math.Max(
            view.CameraLength - 1.0,
            double.Epsilon
        );
        var rootPixelsPerTexel = MathHelper.PiOver2
            * view.FocalLength
            / (surfaceDistance * _textureResolution);

        if (previousLevel < 0) {
            var level = 0;
            var pixelsPerTexel = rootPixelsPerTexel;
            while (level < _maximumLodLevel
                && pixelsPerTexel > TargetPixelsPerTexel) {
                level++;
                pixelsPerTexel *= 0.5;
            }

            return level;
        }

        var selectedLevel = previousLevel;
        var selectedPixelsPerTexel = Math.ScaleB(
            rootPixelsPerTexel,
            -selectedLevel
        );
        while (selectedLevel < _maximumLodLevel
            && selectedPixelsPerTexel > SplitPixelsPerTexel) {
            selectedLevel++;
            selectedPixelsPerTexel *= 0.5;
        }

        while (selectedLevel > 0
            && selectedPixelsPerTexel * 2.0 < MergePixelsPerTexel) {
            selectedLevel--;
            selectedPixelsPerTexel *= 2.0;
        }

        return selectedLevel;
    }

    private void SelectVisibleChunks(in ChunkAddress address, in View view) {
        _selectionCounters.VisitedNodes++;
        var bounds = ChunkGeometry.CalculateBounds(address);
        if (ChunkGeometry.IsFullyBehindHorizon(bounds, view)) {
            _selectionCounters.HorizonRejected++;
            return;
        }

        if (ChunkGeometry.IsOutsideFrustum(bounds, view)) {
            _selectionCounters.FrustumRejected++;
            return;
        }

        if (address.Level == _targetLodLevel) {
            _activeChunkAddresses.Add(address);
            return;
        }

        for (var quadrant = 0; quadrant < 4; quadrant++) {
            SelectVisibleChunks(address.GetChild(quadrant), view);
        }
    }

    private void DrawSurface() {
        using var timing = Debugger.Measure("Surface");
        _graphicsDevice.BlendState = BlendState.Opaque;
        _graphicsDevice.DepthStencilState = DepthStencilState.Default;
        _graphicsDevice.RasterizerState = _solidRasterizerState;
        _graphicsDevice.SamplerStates[0] = SamplerState.PointClamp;
        _surfaceEffect.CurrentTechnique = _surfaceTechnique;

        foreach (var address in _activeChunkAddresses) {
            ConfigureChunk(address);
            _indexTextureParameter.SetValue(GetIndexTexture(address));
            DrawChunk();
        }
    }

    private void DrawWireframe() {
        using var timing = Debugger.Measure("Wireframe");
        _graphicsDevice.BlendState = BlendState.Opaque;
        _graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        _graphicsDevice.RasterizerState = _wireframeRasterizerState;
        _surfaceEffect.CurrentTechnique = _wireframeTechnique;
        _solidColorParameter.SetValue(CreateLodColor(_targetLodLevel));

        foreach (var address in _activeChunkAddresses) {
            ConfigureChunk(address);
            DrawChunk();
        }
    }

    private void ConfigureChunk(in ChunkAddress address) {
        address.GetFaceRegion(out var position, out var size);
        _faceOrientationParameter.SetValue(CubeFace.GetOrientation(address.Face));
        _chunkPositionParameter.SetValue(position);
        _chunkSizeParameter.SetValue(size);
    }

    private Texture2D GetIndexTexture(in ChunkAddress address) {
        var chunk = _chunkCache.Get(address);
        if (chunk is not null) {
            return chunk.IndexTexture;
        }

        using var timing = Debugger.Measure("Chunk creation");
        address.GetFaceRegion(out var position, out var size);
        Color[] colors;
        using (Debugger.Measure("Index colors")) {
            colors = _logicalGrid.CreateIndexColors(
                CubeFace.GetOrientation(address.Face),
                position,
                size,
                _textureSampleCount
            );
        }
        Texture2D texture = null;
        try {
            using (Debugger.Measure("Texture upload")) {
                texture = new Texture2D(
                    _graphicsDevice,
                    _textureSampleCount,
                    _textureSampleCount,
                    false,
                    SurfaceFormat.Color
                );
                texture.SetData(colors);
            }
            chunk = new Chunk(address, texture);
            _chunkCache.Add(chunk);
        }
        catch {
            texture?.Dispose();
            throw;
        }

        return texture;
    }

    private void DrawChunk() {
        foreach (var pass in _surfaceEffect.CurrentTechnique.Passes) {
            pass.Apply();
            _graphicsDevice.DrawIndexedPrimitives(
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
        _graphicsDevice.BlendState = BlendState.Opaque;
        _graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        _graphicsDevice.RasterizerState = RasterizerState.CullNone;
        _graphicsDevice.SetVertexBuffer(_guideLineVertexBuffer);

        foreach (var pass in _guideLineEffect.CurrentTechnique.Passes) {
            pass.Apply();
            _graphicsDevice.DrawPrimitives(
                PrimitiveType.LineList,
                0,
                _guideLinePrimitiveCount
            );
        }
    }

    private static VertexBuffer CreateChunkMeshVertexBuffer(GraphicsDevice graphicsDevice, int meshVertexCount, int textureSampleCount) {
        var vertices = Mesh.CreateGrid(
            meshVertexCount,
            Vector2.Zero,
            1.0f,
            (_, _, point) => new VertexPositionTexture(
                new Vector3(point, 0.0f),
                new Vector2(
                    (0.5f + point.X * (textureSampleCount - 1)) / textureSampleCount,
                    (0.5f + point.Y * (textureSampleCount - 1)) / textureSampleCount
                )
            )
        );
        var vertexBuffer = new VertexBuffer(
            graphicsDevice,
            VertexPositionTexture.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static int CalculateMaximumLodLevel(int meshResolution) {
        var intervalBits = 0;
        for (var value = meshResolution; value > 1; value >>= 1) {
            intervalBits++;
        }

        return ChunkAddress.MaximumPrecisionLevel - intervalBits;
    }

    private static void ValidateConfiguration(
        in SphereConfiguration configuration
    ) {
        if (!IsPowerOfTwo(configuration.MeshResolution)) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Mesh resolution must be a positive power of two."
            );
        }

        var meshVertexCount = (long)configuration.MeshResolution + 1;
        if (meshVertexCount * meshVertexCount > ushort.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Mesh resolution must fit in a 16-bit indexed mesh."
            );
        }

        if (!IsPowerOfTwo(configuration.TextureResolution)) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Texture resolution must be a positive power of two."
            );
        }

        var textureSampleCount = (long)configuration.TextureResolution + 1;
        if (textureSampleCount * textureSampleCount > int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The configured texture is too large."
            );
        }

        if (configuration.ChunkCacheCapacity <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Chunk cache capacity must be positive."
            );
        }
    }

    private static bool IsPowerOfTwo(int value) {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static VertexBuffer CreateGuideLineVertexBuffer(
        GraphicsDevice graphicsDevice
    ) {
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
            graphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static VertexPositionColor[] CreateLatitudeLine(
        float radius,
        float latitudeDegrees,
        Color color
    ) {
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

internal readonly record struct PlanetRenderOptions(
    bool ShowSurface,
    bool ShowWireframe,
    bool ShowGuideLines
);

internal readonly record struct SphereConfiguration(
    int MeshResolution,
    int TextureResolution,
    int ChunkCacheCapacity
);
