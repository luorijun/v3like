using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace monogame;

internal sealed class OrbitCamera
{
    public const float FieldOfView = MathHelper.PiOver4;

    private const float MinimumDistance = 1.004f;
    private const float MaximumDistance = 4.0f;
    private const float RotationPerPixel = 0.005f;

    private MouseState _previousMouseState = Mouse.GetState();
    private float _yaw = 0.75f;
    private float _pitch = 0.35f;
    private float _distance = 2.8f;

    public Vector3 Position { get; private set; }

    public Matrix View { get; private set; }

    public Matrix Projection { get; private set; }

    public void Update(GameTime gameTime, Viewport viewport)
    {
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (keyboard.IsKeyDown(Keys.Home))
        {
            _yaw = 0.75f;
            _pitch = 0.35f;
            _distance = 2.8f;
        }
        else
        {
            var elapsedSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (mouse.MiddleButton == ButtonState.Pressed &&
                _previousMouseState.MiddleButton == ButtonState.Pressed)
            {
                _yaw += (mouse.X - _previousMouseState.X) * RotationPerPixel;
                _pitch += (mouse.Y - _previousMouseState.Y) * RotationPerPixel;
            }

            var zoomExponent = -(mouse.ScrollWheelValue - _previousMouseState.ScrollWheelValue) * 0.0012f;
            if (keyboard.IsKeyDown(Keys.PageUp))
            {
                zoomExponent -= 1.5f * elapsedSeconds;
            }

            if (keyboard.IsKeyDown(Keys.PageDown))
            {
                zoomExponent += 1.5f * elapsedSeconds;
            }

            _distance *= MathF.Exp(zoomExponent);
        }

        _previousMouseState = mouse;
        _yaw = MathHelper.WrapAngle(_yaw);
        _pitch = MathHelper.Clamp(_pitch, -1.45f, 1.45f);
        _distance = MathHelper.Clamp(_distance, MinimumDistance, MaximumDistance);

        var horizontalRadius = MathF.Cos(_pitch) * _distance;
        Position = new Vector3(
            MathF.Cos(_yaw) * horizontalRadius,
            MathF.Sin(_pitch) * _distance,
            MathF.Sin(_yaw) * horizontalRadius);

        View = Matrix.CreateLookAt(Position, Vector3.Zero, Vector3.Up);

        var aspectRatio = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);
        var altitude = _distance - 1.0f;
        var nearPlane = MathHelper.Clamp(altitude * 0.2f, 0.0001f, 0.1f);
        Projection = Matrix.CreatePerspectiveFieldOfView(
            FieldOfView,
            aspectRatio,
            nearPlane,
            10.0f);
    }
}
