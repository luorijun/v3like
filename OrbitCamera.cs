using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace monogame;

internal sealed class OrbitCamera {
    public const float FieldOfView = MathHelper.PiOver4;

    private const float MinimumHeight = 0.0025f;
    private const float InitialHeight = 1.8f;
    private const float MaximumHeight = 3.0f;
    private const float RotationPerPixel = 0.005f;

    private MouseState _previousMouseState = Mouse.GetState();
    private float _yaw = 0.75f;
    private float _pitch = 0.35f;
    private float _height = InitialHeight;
    private int _viewportWidth;
    private int _viewportHeight;
    private bool _hasView;

    public View View { get; private set; }

    public void Update(GameTime gameTime, Viewport viewport) {
        var previousYaw = _yaw;
        var previousPitch = _pitch;
        var previousHeight = _height;
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (keyboard.IsKeyDown(Keys.Home)) {
            _yaw = 0.75f;
            _pitch = 0.35f;
            _height = InitialHeight;
        }
        else {
            var elapsedSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
            var zoomExponent = -(mouse.ScrollWheelValue - _previousMouseState.ScrollWheelValue) * 0.0012f;
            if (keyboard.IsKeyDown(Keys.PageUp)) {
                zoomExponent -= 1.5f * elapsedSeconds;
            }

            if (keyboard.IsKeyDown(Keys.PageDown)) {
                zoomExponent += 1.5f * elapsedSeconds;
            }

            _height *= MathF.Exp(zoomExponent);
            _height = MathHelper.Clamp(_height, MinimumHeight, MaximumHeight);

            if (mouse.MiddleButton == ButtonState.Pressed && _previousMouseState.MiddleButton == ButtonState.Pressed) {
                var rotationScale = MathF.Min(_height / InitialHeight, 1.0f);
                _yaw += (mouse.X - _previousMouseState.X) * RotationPerPixel * rotationScale;
                _pitch += (mouse.Y - _previousMouseState.Y) * RotationPerPixel * rotationScale;
            }
        }

        _previousMouseState = mouse;
        _yaw = MathHelper.WrapAngle(_yaw);
        _pitch = MathHelper.Clamp(_pitch, -1.45f, 1.45f);
        _height = MathHelper.Clamp(_height, MinimumHeight, MaximumHeight);

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
