using System;
using Microsoft.Xna.Framework;

namespace monogame;

internal readonly record struct View {
    public View(
        Vector3 cameraPosition,
        float verticalFieldOfView,
        int viewportHeight,
        Matrix viewProjection
    ) {
        CameraPosition = cameraPosition;
        VerticalFieldOfView = verticalFieldOfView;
        ViewportHeight = viewportHeight;
        ViewProjection = viewProjection;
        EnsureValid();

        CameraLengthSquared = LengthSquared(cameraPosition);
        CameraLength = Math.Sqrt(CameraLengthSquared);
        FocalLength = viewportHeight * 0.5 / Math.Tan(verticalFieldOfView * 0.5);
        Frustum = new BoundingFrustum(viewProjection);
    }

    public Vector3 CameraPosition { get; }

    public float VerticalFieldOfView { get; }

    public int ViewportHeight { get; }

    public Matrix ViewProjection { get; }

    internal double CameraLength { get; }

    internal double CameraLengthSquared { get; }

    internal double FocalLength { get; }

    internal BoundingFrustum Frustum { get; }

    internal void EnsureValid() {
        if (!IsFinite(CameraPosition) || CameraPosition.LengthSquared() <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(CameraPosition));
        }

        if (!float.IsFinite(VerticalFieldOfView)
            || VerticalFieldOfView <= 0.0f
            || VerticalFieldOfView >= MathF.PI) {
            throw new ArgumentOutOfRangeException(nameof(VerticalFieldOfView));
        }

        if (ViewportHeight <= 0) {
            throw new ArgumentOutOfRangeException(nameof(ViewportHeight));
        }

        if (!IsFinite(ViewProjection)) {
            throw new ArgumentOutOfRangeException(nameof(ViewProjection));
        }
    }

    private static double LengthSquared(in Vector3 value) {
        return (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
    }

    private static bool IsFinite(in Vector3 value) {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(in Matrix value) {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12)
            && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22)
            && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32)
            && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42)
            && float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }
}
