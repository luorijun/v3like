using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using monogame;
using monogame.Utils;

namespace monogame.Planet;

internal sealed class Sphere : IDisposable {
    internal const int FaceCount = AssetData.FaceCount;

    private const double RelativeDistanceFloor = 1e-7;

    private readonly Face[] _faces;
    private readonly BasicEffect _effect;
    private readonly IndexBuffer _indexBuffer;
    private readonly RasterizerState _rasterizerState;
    private readonly int _minimumLod;
    private readonly float _splitThreshold;
    private readonly double _distanceFloor;
    private Chunk[] _visibleChunks = Array.Empty<Chunk>();
    private Matrix _viewProjection = Matrix.Identity;

    private Sphere(
        AssetData data,
        GraphicsDevice graphicsDevice,
        int minimumLod,
        float splitThreshold
    ) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ValidateGeometry(
            data.ReferenceRadius,
            data.ChunkResolution,
            data.MaximumLod,
            data.ElevationQuantizationStep
        );
        ValidateSelection(data.MaximumLod, minimumLod, splitThreshold);
        if (data.Elevations.Length != FaceCount || data.Chunks.Length != FaceCount) {
            throw new ArgumentException("A sphere must contain six faces.", nameof(data));
        }

        ReferenceRadius = data.ReferenceRadius;
        ChunkResolution = data.ChunkResolution;
        MaximumLod = data.MaximumLod;
        ElevationQuantizationStep = data.ElevationQuantizationStep;
        if (!float.IsFinite(data.OccluderRadius) || data.OccluderRadius <= 0.0f) {
            throw new InvalidDataException("The sphere has an invalid occluder radius.");
        }

        GraphicsDevice = graphicsDevice;
        _faces = new Face[FaceCount];
        for (var index = 0; index < _faces.Length; index++) {
            _faces[index] = new Face(
                this,
                (CubeFace)index,
                data.Elevations[index],
                data.Chunks[index]
            );
        }

        OccluderRadius = data.OccluderRadius;
        _minimumLod = minimumLod;
        _splitThreshold = splitThreshold;
        _distanceFloor = Math.Max(ReferenceRadius * RelativeDistanceFloor, double.Epsilon);
        ValidateHierarchy();
        _effect = new BasicEffect(graphicsDevice) {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
        var indices = Mesh.CreateTriangleIndices(ChunkResolution);
        _indexBuffer = new IndexBuffer(
            graphicsDevice,
            IndexElementSize.SixteenBits,
            indices.Length,
            BufferUsage.WriteOnly
        );
        _indexBuffer.SetData(indices);
        _rasterizerState = new RasterizerState {
            CullMode = CullMode.CullClockwiseFace,
            FillMode = FillMode.Solid,
        };
    }

    public float ReferenceRadius { get; }

    public int ChunkResolution { get; }

    public int MaximumLod { get; }

    public float ElevationQuantizationStep { get; }

    public float OccluderRadius { get; }

    internal GraphicsDevice GraphicsDevice { get; }

    internal int TriangleCount => _indexBuffer.IndexCount / 3;

    internal int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

    internal int FinestResolution => checked(FinestIntervals + 1);

    internal ReadOnlySpan<Face> Faces => _faces;

    public Face GetFace(CubeFace face) {
        if (!Enum.IsDefined(face)) {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        return _faces[(int)face];
    }

    public float GetElevation(CubeFace face, int x, int y) {
        return GetFace(face).GetElevation(x, y);
    }

    public Chunk GetChunk(in ChunkId id) {
        return GetFace(id.Face).GetChunk(id);
    }

    public void Update(in View view) {
        _visibleChunks = Select(view);
        _viewProjection = view.ViewProjection;
    }

    public void Draw() {
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = _rasterizerState;
        GraphicsDevice.Indices = _indexBuffer;
        _effect.World = Matrix.Identity;
        _effect.View = Matrix.Identity;
        _effect.Projection = _viewProjection;

        foreach (var pass in _effect.CurrentTechnique.Passes) {
            pass.Apply();
            foreach (var chunk in _visibleChunks) {
                chunk.Draw();
            }
        }
    }

    public void Dispose() {
        foreach (var face in _faces) {
            face.Dispose();
        }

        _rasterizerState.Dispose();
        _indexBuffer.Dispose();
        _effect.Dispose();
    }

    public static Sphere Create(
        AssetData data,
        GraphicsDevice graphicsDevice,
        int minimumLod,
        float splitThreshold
    ) {
        return new Sphere(data, graphicsDevice, minimumLod, splitThreshold);
    }

    private Chunk[] Select(in View view) {
        view.EnsureValid();
        var selection = new Selection(
            view,
            MaximumLod,
            _minimumLod,
            _splitThreshold,
            OccluderRadius,
            _distanceFloor
        );

        foreach (var face in _faces) {
            face.Select(selection);
        }

        return selection.Complete();
    }

    private static void ValidateSelection(int maximumLod, int minimumLod, float splitThreshold) {
        if (minimumLod < 0 || minimumLod > maximumLod) {
            throw new ArgumentOutOfRangeException(nameof(minimumLod));
        }

        if (!float.IsFinite(splitThreshold) || splitThreshold <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(splitThreshold));
        }
    }

    internal static void ValidateGeometry(
        float referenceRadius,
        int chunkResolution,
        int maximumLod,
        float elevationQuantizationStep
    ) {
        if (!float.IsFinite(referenceRadius) || referenceRadius <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(referenceRadius));
        }

        if (chunkResolution < 2) {
            throw new ArgumentOutOfRangeException(nameof(chunkResolution));
        }

        if (maximumLod is < 0 or > 14) {
            throw new ArgumentOutOfRangeException(nameof(maximumLod));
        }

        if (!float.IsFinite(elevationQuantizationStep) || elevationQuantizationStep <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(elevationQuantizationStep));
        }

        _ = checked((chunkResolution - 1) * (1 << maximumLod) + 1);
        _ = Face.GetChunkCount(maximumLod);
    }

    private void ValidateHierarchy() {
        foreach (var face in _faces) {
            foreach (var chunk in face.Chunks) {
                if (!IsFinite(chunk.CenterDirection)
                    || !float.IsFinite(chunk.AngularRadius)
                    || !float.IsFinite(chunk.MinimumRadius)
                    || !float.IsFinite(chunk.MaximumRadius)
                    || !IsFinite(chunk.BoundingSphere.Center)
                    || !float.IsFinite(chunk.BoundingSphere.Radius)
                    || !float.IsFinite(chunk.HorizonPointRadius)
                    || !float.IsFinite(chunk.GeometricError)) {
                    throw new InvalidDataException("The sphere contains non-finite chunk metadata.");
                }

                if (chunk.AngularRadius < 0.0f
                    || chunk.MinimumRadius <= 0.0f
                    || chunk.MinimumRadius > chunk.MaximumRadius
                    || chunk.BoundingSphere.Radius < 0.0f
                    || chunk.HorizonPointRadius < 0.0f
                    || chunk.GeometricError < 0.0f
                    || OccluderRadius > chunk.MinimumRadius) {
                    throw new InvalidDataException("The sphere contains invalid chunk bounds.");
                }

                if (chunk.Id.Level == MaximumLod) {
                    if (chunk.GeometricError != 0.0f) {
                        throw new InvalidDataException("A highest-LOD chunk has non-zero geometric error.");
                    }

                    continue;
                }

                for (var childY = 0; childY < 2; childY++) {
                    for (var childX = 0; childX < 2; childX++) {
                        var child = GetChunk(chunk.Id.Child(childX, childY));
                        if (chunk.GeometricError < child.GeometricError) {
                            throw new InvalidDataException("A parent chunk has less geometric error than one of its children.");
                        }
                    }
                }
            }
        }
    }

    private static bool IsFinite(in Vector3 value) {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    internal sealed class Selection {
        private readonly List<Chunk> _chunks = new();

        public Selection(
            in View view,
            int maximumLod,
            int minimumLod,
            float splitThreshold,
            float occluderRadius,
            double distanceFloor
        ) {
            View = view;
            MaximumLod = maximumLod;
            MinimumLod = minimumLod;
            SplitThreshold = splitThreshold;
            OccluderRadius = occluderRadius;
            DistanceFloor = distanceFloor;
        }

        public View View { get; }

        public int MaximumLod { get; }

        public int MinimumLod { get; }

        public float SplitThreshold { get; }

        public float OccluderRadius { get; }

        public double DistanceFloor { get; }

        public void Add(Chunk chunk) {
            _chunks.Add(chunk);
        }

        public Chunk[] Complete() => _chunks.ToArray();
    }
}
