using System;
using Microsoft.Xna.Framework;

namespace monogame.Planet;

internal enum FaceId : byte {
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

internal sealed class Face : IDisposable {
    private static readonly Matrix[] s_orientations = [
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Right),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Left),
        Matrix.Identity,
        Matrix.CreateWorld(Vector3.Zero, Vector3.Backward, Vector3.Down),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Backward),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Forward),
    ];

    private readonly FaceData _data;
    private bool _rootVisible;

    internal Face(
        Sphere sphere,
        FaceId id,
        FaceData data
    ) {
        ArgumentNullException.ThrowIfNull(sphere);
        if (!Enum.IsDefined(id)) {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        Sphere = sphere;
        Id = id;
        _data = data;
        Root = new Chunk(this, data.Chunks[0]);
    }

    public FaceId Id { get; }

    public Chunk Root { get; }

    internal Sphere Sphere { get; }

    internal Matrix Orientation => GetOrientation(Id);

    internal float GetElevation(int x, int y) {
        return _data.GetElevationUnchecked(x, y);
    }

    internal Chunk CreateChild(Chunk parent, ChunkQuadrant quadrant) {
        var id = Chunk.GetChildId(parent.Id, quadrant);
        return new Chunk(parent, quadrant, id, _data.Chunks[checked((int)id)]);
    }

    internal void Update(in View view) {
        _rootVisible = Root.Update(view);
    }

    internal void Draw() {
        if (_rootVisible) {
            Root.Draw();
        }
    }

    public void Dispose() {
        Root.Dispose();
    }

    internal static Matrix GetOrientation(FaceId face) {
        return s_orientations[(int)face];
    }
}
