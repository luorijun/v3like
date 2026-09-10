using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Planet;
using monogame.Debugging;

namespace monogame;

public sealed class GameApp : Game {
    private static readonly SphereConfiguration s_sphereConfiguration = new(
        MeshResolution: 16,
        PixelsPerCell: 32
    );

    private OrbitCamera _camera;
    private Sphere _sphere;
    private View? _prevCameraView;

    public GameApp() {
        _ = new GraphicsDeviceManager(this) {
            GraphicsProfile = GraphicsProfile.HiDef,
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Cube Sphere | GPU surface | F5 Debug";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent() {
        InputManager.Initialize(this);
        var surfaceEffect = Content.Load<Effect>("Effects/TileSurface");
        Debugger.Initialize(this);
        _sphere = Sphere.Create(s_sphereConfiguration, GraphicsDevice, surfaceEffect);
        _camera = new OrbitCamera();
    }

    protected override void Update(GameTime gameTime) {
        using var timing = Debugger.Measure("Update");

        InputManager.BeforeUpdate();
        Debugger.Update(gameTime);
        if (InputManager.IsClick(Keys.Escape, InputFocus.Scene)) {
            Exit();
        }

        _camera.Update(GraphicsDevice.Viewport);

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
                _sphere.Draw(_camera.View, Debugger.RenderOptions);
                base.Draw(gameTime);
            }
            Debugger.Draw();
        }
        Debugger.CompleteFrame(_sphere.Metrics);
    }

    protected override void UnloadContent() {
        Debugger.Dispose();
        InputManager.Dispose();
        _sphere.Dispose();
        base.UnloadContent();
    }

}
