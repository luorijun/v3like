using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace monogame;

internal sealed class OrbitCamera {
    public const float FieldOfView = MathHelper.PiOver4;

    internal const float MinimumHeight = 0.01f;
    private const float InitialHeight = 1.8f;
    private const float MaximumHeight = 3.0f;
    private const float RotationPerPixel = 0.005f;

    private float _yaw = 0.75f;
    private float _pitch = 0.35f;
    private float _height = InitialHeight;
    private int _viewportWidth;
    private int _viewportHeight;
    private bool _hasView;
    private bool _rotating;

    public View View { get; private set; }

    public void Update(Viewport viewport) {
        var previousYaw = _yaw;
        var previousPitch = _pitch;
        var previousHeight = _height;

        var zoomExponent = InputManager.Focus == InputFocus.Scene ? -InputManager.ScrollDelta * 0.0012f : 0;
        _height *= MathF.Exp(zoomExponent);
        _height = MathHelper.Clamp(_height, MinimumHeight, MaximumHeight);

        var rotating = InputManager.IsDown(MouseButton.Middle, InputFocus.Scene);
        if (rotating && _rotating) {
            var movement = InputManager.MouseDelta;
            var rotationScale = MathF.Min(_height / InitialHeight, 1.0f);
            _yaw += movement.X * RotationPerPixel * rotationScale;
            _pitch += movement.Y * RotationPerPixel * rotationScale;
        }
        _rotating = rotating;

        _yaw = MathHelper.WrapAngle(_yaw);
        _pitch = MathHelper.Clamp(_pitch, -1.45f, 1.45f);

        if (_hasView
            && _yaw == previousYaw
            && _pitch == previousPitch
            && _height == previousHeight
            && viewport.Width == _viewportWidth
            && viewport.Height == _viewportHeight) {
            return;
        }

        var distance = 1.0f + _height;
        var horizontalRadius = MathF.Cos(_pitch) * distance;
        var position = new Vector3(
            MathF.Cos(_yaw) * horizontalRadius,
            MathF.Sin(_pitch) * distance,
            MathF.Sin(_yaw) * horizontalRadius
        );
        var viewMatrix = Matrix.CreateLookAt(position, Vector3.Zero, Vector3.Up);

        var aspectRatio = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);
        var nearPlane = MathHelper.Clamp(_height * 0.2f, 0.0001f, 0.1f);
        var projectionMatrix = Matrix.CreatePerspectiveFieldOfView(
            FieldOfView,
            aspectRatio,
            nearPlane,
            10.0f
        );
        View = new View(
            position,
            FieldOfView,
            viewport.Height,
            viewMatrix * projectionMatrix
        );
        _viewportWidth = viewport.Width;
        _viewportHeight = viewport.Height;
        _hasView = true;
    }
}
