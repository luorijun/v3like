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
    private View? _prevView;

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
        Debugger.Initialize(this, new(
            UiScale: 1,
            Visible: false,
            Render: new(
                Surface: true,
                Wireframe: false,
                Guides: false
            ),
            PeakMetric: PeakMetric.FrameInterval
        ));
        _camera = new OrbitCamera(new(
            Fov: MathHelper.PiOver4,
            Yaw: 0.75f,
            Pitch: 0.35f,
            Height: 1.8f,
            MinHeight: 0.01f,
            MaxHeight: 3.0f,
            RotateSpeed: 0.005f,
            ZoomSpeed: 0.0012f,
            RotateHeight: 1.8f,
            PitchLimit: 1.45f
        ));
        _map = new Map(new(
            Mode: MapMode.Terrain,
            BorderWidth: 0.15f
        ));
        _sphere = new Sphere(new(
            MeshResolution: 16,
            PixelsPerCell: 32,
            Hysteresis: 0.15
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

        if (!Debugger.SelectionFrozen && (!_prevView.HasValue || !_prevView.Value.Same(_camera.View))) {
            _sphere.Update(_camera.View, _camera.MinHeight);
            _prevView = _camera.View;
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
