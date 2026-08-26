using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame;
using monogame.Utils;

namespace monogame.Planet;

internal sealed class Sphere : IDisposable {
    internal const int FaceCount = 6;

    private const double RelativeDistanceFloor = 1e-7;

    private readonly SphereData _data;
    private readonly Face[] _faces;
    private readonly Cache _cache;
    private readonly BasicEffect _effect;
    private readonly IndexBuffer _indexBuffer;
    private readonly RasterizerState _rasterizerState;
    private readonly float _splitThreshold;
    private readonly float _mergeThreshold;
    private readonly double _distanceFloor;

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
        _effect = new BasicEffect(graphicsDevice) {
            DiffuseColor = new Color(82, 142, 96).ToVector3(),
            VertexColorEnabled = false,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        _rasterizerState = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.Solid,
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

    public void Update(in View view) {
        foreach (var face in _faces) {
            face.Update(view);
        }
    }

    public void Draw(in View view) {
        GraphicsDevice.RasterizerState = _rasterizerState;
        GraphicsDevice.Indices = _indexBuffer;
        _effect.World = Matrix.Identity;
        _effect.View = Matrix.Identity;
        _effect.Projection = view.ViewProjection;

        foreach (var face in _faces) {
            face.TouchCachedChunks(_cache);
        }

        foreach (var pass in _effect.CurrentTechnique.Passes) {
            pass.Apply();
            foreach (var face in _faces) {
                face.Draw(_cache);
            }
        }
    }

    public void Dispose() {
        _cache.Dispose();
        _rasterizerState.Dispose();
        _indexBuffer.Dispose();
        _effect.Dispose();
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

}
