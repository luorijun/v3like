using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace monogame;

internal readonly record struct View {
    public View(
        Vector3 cameraPosition,
        float verticalFieldOfView,
        Viewport viewport,
        Matrix viewProjection
    ) {
        CameraPosition = cameraPosition;
        VerticalFieldOfView = verticalFieldOfView;
        Viewport = viewport;
        ViewProjection = viewProjection;
        EnsureValid();

        CameraLength = Math.Sqrt(LengthSquared(cameraPosition));
        FocalLength = viewport.Height * 0.5 / Math.Tan(verticalFieldOfView * 0.5);
        Frustum = new BoundingFrustum(viewProjection);
    }

    public Vector3 CameraPosition { get; }

    public float VerticalFieldOfView { get; }

    public Viewport Viewport { get; }

    public Matrix ViewProjection { get; }

    internal double CameraLength { get; }

    internal double FocalLength { get; }

    internal BoundingFrustum Frustum { get; }

    // Perspective ray from the camera through a render-target pixel coordinate.
    // Viewport containment and intersection with scene geometry belong to the caller.
    internal Ray CreateRay(Point screenPosition) {
        var farPoint = Viewport.Unproject(
            new Vector3(screenPosition.X, screenPosition.Y, Viewport.MaxDepth),
            ViewProjection, Matrix.Identity, Matrix.Identity);
        return new Ray(CameraPosition, Vector3.Normalize(farPoint - CameraPosition));
    }

    internal bool Same(in View other) {
        return CameraPosition == other.CameraPosition
            && VerticalFieldOfView == other.VerticalFieldOfView
            && Viewport.Equals(other.Viewport)
            && ViewProjection == other.ViewProjection;
    }

    private void EnsureValid() {
        if (!IsFinite(CameraPosition) || CameraPosition.LengthSquared() <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(CameraPosition));
        }

        if (!float.IsFinite(VerticalFieldOfView)
            || VerticalFieldOfView <= 0.0f
            || VerticalFieldOfView >= MathF.PI) {
            throw new ArgumentOutOfRangeException(nameof(VerticalFieldOfView));
        }

        if (Viewport.Width <= 0 || Viewport.Height <= 0
            || !float.IsFinite(Viewport.MinDepth) || !float.IsFinite(Viewport.MaxDepth)
            || Viewport.MinDepth < 0.0f || Viewport.MaxDepth > 1.0f
            || Viewport.MinDepth >= Viewport.MaxDepth) {
            throw new ArgumentOutOfRangeException(nameof(Viewport));
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
