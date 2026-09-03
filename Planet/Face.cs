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

internal static class CubeFace {
    private static readonly Matrix[] s_orientations = [
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Right),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Left),
        Matrix.Identity,
        Matrix.CreateWorld(Vector3.Zero, Vector3.Backward, Vector3.Down),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Backward),
        Matrix.CreateWorld(Vector3.Zero, Vector3.Up, Vector3.Forward),
    ];

    internal static Matrix GetOrientation(FaceId face) {
        if (!Enum.IsDefined(face)) {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        return s_orientations[(int)face];
    }
}
