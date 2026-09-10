using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Lod;
using monogame.Grid;
using monogame.Debugging;

namespace monogame;

public sealed class GameApp : Game {
    private OrbitCamera _camera;
    private Sphere _sphere;
    private Map _map;
    private View? _prevCameraView;

    public GameApp() {
        var displayMode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        _ = new GraphicsDeviceManager(this) {
            GraphicsProfile = GraphicsProfile.HiDef,
            PreferredBackBufferWidth = displayMode.Width,
            PreferredBackBufferHeight = displayMode.Height,
            HardwareModeSwitch = false,
            IsFullScreen = true,
        };

        IsMouseVisible = true;
        Window.Title = "Cube Sphere | F5 Debug";
    }

    protected override void LoadContent() {
        GameManager.Initialize(this);
        InputManager.Initialize(this);
        Debugger.Initialize(this);
        _camera = new OrbitCamera();
        _map = new Map();
        _sphere = new Sphere(new(
            MeshResolution: 16,
            PixelsPerCell: 32
        ));
    }

    protected override void Update(GameTime gameTime) {
        using var timing = Debugger.Measure("Update");

        InputManager.BeforeUpdate();
        Debugger.Update(gameTime);
        if (InputManager.IsClick(Keys.Escape, InputFocus.Scene)) {
            Exit();
        }

        _camera.Update(GraphicsDevice.Viewport);
        _map.Update(_camera.View);

        if (!Debugger.SelectionFrozen && (!_prevCameraView.HasValue || !_prevCameraView.Value.Same(_camera.View))) {
            _sphere.Update(_camera.View, OrbitCamera.MinimumHeight);
            _prevCameraView = _camera.View;
        }

        base.Update(gameTime);
        InputManager.AfterUpdate();
    }

    protected override void Draw(GameTime gameTime) {
        using (Debugger.MeasureGpu()) {
            using (Debugger.Measure("Draw")) {
                GraphicsDevice.Clear(new Color(7, 11, 18));
                _map.Bind();
                _sphere.Draw(_camera.View, Debugger.RenderOptions);
                base.Draw(gameTime);
            }
            Debugger.Draw();
        }
        Debugger.CompleteFrame(_sphere.Metrics);
    }

    protected override void UnloadContent() {
        _sphere.Dispose();
        _map.Dispose();
        Debugger.Dispose();
        InputManager.Dispose();
        GameManager.Dispose();
        base.UnloadContent();
    }

}
