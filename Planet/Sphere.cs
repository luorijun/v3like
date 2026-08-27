using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame;
using monogame.Utils;

namespace monogame.Planet;

internal sealed class Sphere : IDisposable {
    internal const int FaceCount = 6;

    private static readonly Vector3 s_neutralSurfaceColor = new Color(210, 210, 210).ToVector3();

    private const double RelativeDistanceFloor = 1e-7;
    private const int GuideLineSegments = 256;
    private const int RotationAxisSegments = 64;
    private const float TropicLatitudeDegrees = 23.44f;
    private const float GuideLineSurfaceOffset = 0.001f;
    private const float WireframeDepthBias = -0.00001f;
    private const float RotationAxisHalfLength = 1.15f;

    private readonly SphereData _data;
    private readonly Face[] _faces;
    private readonly Cache _cache;
    private readonly BasicEffect _surfaceEffect;
    private readonly BasicEffect _wireframeEffect;
    private readonly BasicEffect _guideLineEffect;
    private readonly IndexBuffer _indexBuffer;
    private readonly VertexBuffer _guideLineVertexBuffer;
    private readonly int _guideLinePrimitiveCount;
    private readonly RasterizerState _solidRasterizerState;
    private readonly RasterizerState _wireframeRasterizerState;
    private readonly float _splitThreshold;
    private readonly float _mergeThreshold;
    private readonly double _distanceFloor;
    private SelectionMetrics _selectionMetrics;
    private DrawMetrics _drawMetrics;
    private long _selectionRevision;

    private Sphere(
        SphereData data,
        GraphicsDevice graphicsDevice,
        float splitThreshold,
        float mergeThreshold,
        int cacheCapacity
    ) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        if (!float.IsFinite(splitThreshold) || splitThreshold <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(splitThreshold));
        }
        if (!float.IsFinite(mergeThreshold)
            || mergeThreshold <= 0.0f
            || mergeThreshold >= splitThreshold) {
            throw new ArgumentOutOfRangeException(nameof(mergeThreshold));
        }

        _data = data;
        _cache = new Cache(
            checked(FaceCount * data.Faces[0].Chunks.Length),
            cacheCapacity
        );
        _faces = new Face[FaceCount];
        for (var index = 0; index < _faces.Length; index++) {
            _faces[index] = new Face(
                this,
                (FaceId)index,
                data.Faces[index]
            );
        }

        _splitThreshold = splitThreshold;
        _mergeThreshold = mergeThreshold;
        _distanceFloor = Math.Max(ReferenceRadius * RelativeDistanceFloor, double.Epsilon);

        GraphicsDevice = graphicsDevice;
        var indices = Mesh.CreateTriangleIndices(ChunkResolution);
        _indexBuffer = new IndexBuffer(
            graphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly
        );
        _indexBuffer.SetData(indices);
        _surfaceEffect = new BasicEffect(graphicsDevice) {
            DiffuseColor = Vector3.One,
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _wireframeEffect = new BasicEffect(graphicsDevice) {
            DiffuseColor = Vector3.One,
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _guideLineEffect = new BasicEffect(graphicsDevice) {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _guideLineVertexBuffer = CreateGuideLineVertexBuffer(graphicsDevice, data);
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

    internal GraphicsDevice GraphicsDevice { get; }

    public float ReferenceRadius => _data.ReferenceRadius;

    public int ChunkResolution => _data.ChunkResolution;

    public int MaximumLod => _data.MaximumLod;

    public float OccluderRadius => _data.OccluderRadius;

    internal float SplitThreshold => _splitThreshold;

    internal float MergeThreshold => _mergeThreshold;

    internal double DistanceFloor => _distanceFloor;

    internal int TriangleCount => _indexBuffer.IndexCount / 3;

    internal SphereMetrics Metrics => new(_selectionMetrics, _drawMetrics);

    public void Update(in View view) {
        var started = Stopwatch.GetTimestamp();
        var counters = new SelectionCounters();
        foreach (var face in _faces) {
            face.Update(view, ref counters);
        }

        _selectionMetrics = new SelectionMetrics(
            ++_selectionRevision,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            counters.VisitedNodes,
            counters.ActiveChunks,
            counters.HorizonRejected,
            counters.FrustumTests,
            counters.FrustumRejected
        );
    }

    public void Draw(in View view, in PlanetRenderOptions options) {
        var started = Stopwatch.GetTimestamp();
        var counters = new DrawCounters();
        _surfaceEffect.DiffuseColor = options.ShowWireframe
            ? s_neutralSurfaceColor
            : Vector3.One;
        _surfaceEffect.VertexColorEnabled = !options.ShowWireframe;
        ConfigureEffect(_surfaceEffect, view.ViewProjection);
        ConfigureEffect(_wireframeEffect, view.ViewProjection);
        ConfigureEffect(_guideLineEffect, view.ViewProjection);

        if (options.ShowSurface || options.ShowWireframe) {
            foreach (var face in _faces) {
                face.TouchCachedChunks(_cache);
            }
        }

        if (options.ShowSurface) {
            DrawSphereGeometry(
                _surfaceEffect,
                DepthStencilState.Default,
                _solidRasterizerState,
                ref counters
            );
        }

        if (options.ShowWireframe) {
            DrawSphereGeometry(
                _wireframeEffect,
                DepthStencilState.DepthRead,
                _wireframeRasterizerState,
                ref counters
            );
        }

        if (options.ShowGuideLines) {
            DrawGuideLines();
        }

        _drawMetrics = new DrawMetrics(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            counters.DrawCalls,
            counters.CacheMisses,
            counters.CacheEvictions,
            counters.MeshBuilds,
            counters.MeshBuildTimestampTicks * 1000.0 / Stopwatch.Frequency
        );
    }

    public void Dispose() {
        _cache.Dispose();
        _wireframeRasterizerState.Dispose();
        _solidRasterizerState.Dispose();
        _guideLineVertexBuffer.Dispose();
        _indexBuffer.Dispose();
        _guideLineEffect.Dispose();
        _wireframeEffect.Dispose();
        _surfaceEffect.Dispose();
    }

    public static Sphere Create(
        SphereData data,
        GraphicsDevice graphicsDevice,
        float splitThreshold,
        float mergeThreshold,
        int cacheCapacity
    ) {
        return new Sphere(data, graphicsDevice, splitThreshold, mergeThreshold, cacheCapacity);
    }

    private static void ConfigureEffect(BasicEffect effect, in Matrix viewProjection) {
        effect.World = Matrix.Identity;
        effect.View = Matrix.Identity;
        effect.Projection = viewProjection;
    }

    private void DrawSphereGeometry(
        BasicEffect effect,
        DepthStencilState depthStencilState,
        RasterizerState rasterizerState,
        ref DrawCounters counters
    ) {
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = depthStencilState;
        GraphicsDevice.RasterizerState = rasterizerState;
        GraphicsDevice.Indices = _indexBuffer;

        foreach (var pass in effect.CurrentTechnique.Passes) {
            pass.Apply();
            foreach (var face in _faces) {
                face.Draw(_cache, ref counters);
            }
        }
    }

    private void DrawGuideLines() {
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        GraphicsDevice.RasterizerState = RasterizerState.CullNone;
        GraphicsDevice.SetVertexBuffer(_guideLineVertexBuffer);

        foreach (var pass in _guideLineEffect.CurrentTechnique.Passes) {
            pass.Apply();
            GraphicsDevice.DrawPrimitives(
                PrimitiveType.LineList,
                0,
                _guideLinePrimitiveCount
            );
        }
    }

    private static VertexBuffer CreateGuideLineVertexBuffer(
        GraphicsDevice graphicsDevice,
        SphereData data
    ) {
        var vertices = CreateGuideLineVertices(data);
        var vertexBuffer = new VertexBuffer(
            graphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly
        );
        vertexBuffer.SetData(vertices);
        return vertexBuffer;
    }

    private static VertexPositionColor[] CreateGuideLineVertices(SphereData data) {
        var radius = GetMaximumRadius(data)
            + data.ReferenceRadius * GuideLineSurfaceOffset;
        var axisHalfLength = data.ReferenceRadius * RotationAxisHalfLength;

        return [
            .. CreateLatitudeLine(radius, 0.0f, Color.Gold),
            .. CreateLatitudeLine(radius, TropicLatitudeDegrees, Color.OrangeRed),
            .. CreateLatitudeLine(radius, -TropicLatitudeDegrees, Color.OrangeRed),
            .. CreateRotationAxis(axisHalfLength),
        ];
    }

    private static float GetMaximumRadius(SphereData data) {
        var maximumRadius = data.ReferenceRadius;
        foreach (var face in data.Faces) {
            maximumRadius = MathF.Max(maximumRadius, face.Chunks[0].MaximumRadius);
        }

        return maximumRadius;
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
            GuideLineSegments,
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
        return Mesh.CreateLineList(
            RotationAxisSegments,
            progress => new VertexPositionColor(
                Vector3.UnitY * MathHelper.Lerp(-halfLength, halfLength, progress),
                Color.Cyan
            )
        );
    }

}

internal readonly record struct PlanetRenderOptions(
    bool ShowSurface,
    bool ShowWireframe,
    bool ShowGuideLines
);
