using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace monogame;

internal sealed class OrbitCamera {
    private readonly OrbitCameraConfig _config;
    private float _yaw;
    private float _pitch;
    private float _height;
    private bool _hasView;
    private bool _rotating;

    public View View { get; private set; }
    internal float MinHeight => _config.MinHeight;

    internal OrbitCamera(in OrbitCameraConfig config) {
        if (!float.IsFinite(config.Fov)
            || config.Fov <= 0 || config.Fov >= MathHelper.Pi) {
            throw new ArgumentOutOfRangeException(nameof(config), "Field of view must be between zero and pi radians.");
        }
        if (!float.IsFinite(config.MinHeight) || config.MinHeight <= 0
            || !float.IsFinite(config.MaxHeight) || config.MaxHeight < config.MinHeight
            || !float.IsFinite(config.Height)
            || config.Height < config.MinHeight || config.Height > config.MaxHeight) {
            throw new ArgumentOutOfRangeException(nameof(config), "Heights must be finite, positive and ordered: minimum <= initial <= maximum.");
        }
        if (!float.IsFinite(config.PitchLimit) || config.PitchLimit <= 0 || config.PitchLimit >= MathHelper.PiOver2
            || !float.IsFinite(config.Pitch) || MathF.Abs(config.Pitch) > config.PitchLimit
            || !float.IsFinite(config.Yaw)) {
            throw new ArgumentOutOfRangeException(nameof(config), "Initial angles must be finite and pitch must be within a limit below pi/2 radians.");
        }
        if (!float.IsFinite(config.RotateSpeed) || config.RotateSpeed < 0
            || !float.IsFinite(config.ZoomSpeed) || config.ZoomSpeed < 0
            || !float.IsFinite(config.RotateHeight) || config.RotateHeight <= 0) {
            throw new ArgumentOutOfRangeException(nameof(config), "Sensitivities must be finite and nonnegative; rotation reference height must be finite and positive.");
        }
        _config = config;
        _yaw = config.Yaw;
        _pitch = config.Pitch;
        _height = config.Height;
    }

    public void Update(Viewport viewport) {
        var prevYaw = _yaw;
        var prevPitch = _pitch;
        var prevHeight = _height;

        var zoomExponent = InputManager.Focus == InputFocus.Scene ? -InputManager.ScrollDelta * _config.ZoomSpeed : 0;
        _height *= MathF.Exp(zoomExponent);
        _height = MathHelper.Clamp(_height, MinHeight, _config.MaxHeight);

        var rotating = InputManager.IsDown(MouseButton.Middle, InputFocus.Scene);
        if (rotating && _rotating) {
            var movement = InputManager.MouseDelta;
            var rotationScale = MathF.Min(_height / _config.RotateHeight, 1.0f);
            _yaw += movement.X * _config.RotateSpeed * rotationScale;
            _pitch += movement.Y * _config.RotateSpeed * rotationScale;
        }
        _rotating = rotating;

        _yaw = MathHelper.WrapAngle(_yaw);
        _pitch = MathHelper.Clamp(_pitch, -_config.PitchLimit, _config.PitchLimit);

        if (_hasView
            && _yaw == prevYaw
            && _pitch == prevPitch
            && _height == prevHeight
            && viewport.Equals(View.Viewport)) {
            return;
        }

        var distance = 1.0f + _height;
        var horizontalRadius = MathF.Cos(_pitch) * distance;
        var position = new Vector3(
            MathF.Cos(_yaw) * horizontalRadius,
            MathF.Sin(_pitch) * distance,
            MathF.Sin(_yaw) * horizontalRadius
        );
        var view = Matrix.CreateLookAt(position, Vector3.Zero, Vector3.Up);

        var aspect = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);
        var nearPlane = MathHelper.Clamp(_height * 0.2f, 0.0001f, 0.1f);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            _config.Fov,
            aspect,
            nearPlane,
            10.0f
        );
        View = new View(
            position,
            _config.Fov,
            viewport,
            view * projection
        );
        _hasView = true;
    }
}

// Angles are radians; heights are measured above the unit sphere.
// RotateSpeed is radians per mouse pixel; ZoomSpeed is the exponent per scroll unit.
// RotateHeight is the height at which rotation reaches full speed.
internal readonly record struct OrbitCameraConfig(
    float Fov,
    float Yaw,
    float Pitch,
    float Height,
    float MinHeight,
    float MaxHeight,
    float RotateSpeed,
    float ZoomSpeed,
    float RotateHeight,
    float PitchLimit
);
