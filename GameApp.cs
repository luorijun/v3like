using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Planet;

namespace monogame;

public sealed class GameApp : Game {
    private readonly GraphicsDeviceManager _graphics;
    private readonly OrbitCamera _camera = new();
    private Sphere _sphere;
    private PerformanceMonitor _performance;
    private KeyboardState _previousKeyboardState = Keyboard.GetState();
    private bool _showSurface = true;
    private bool _showWireframe;
    private bool _showGuideLines;
    private bool _lockLod;
    private View? _preLodView;
    private string _performanceTitle = "Cube Sphere Terrain";

    public GameApp() {
        _graphics = new GraphicsDeviceManager(this) {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Cube Sphere Terrain";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent() {
        var surfaceEffect = Content.Load<Effect>("Effects/TileSurface");
        using var stream = TitleContainer.OpenStream("Content/sphere.asset");
        var data = Asset.Read(stream);
        _sphere = Sphere.Create(
            data,
            GraphicsDevice,
            surfaceEffect,
            splitThreshold: 1.5f,
            mergeThreshold: 1.2f,
            // 278 KiB
            cacheCapacity: 2000
        );
        _performance = new PerformanceMonitor();
    }

    protected override void Update(GameTime gameTime) {
        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape)) {
            Exit();
        }

        if (WasPressed(keyboard, Keys.F1)) {
            _showSurface = !_showSurface;
        }

        if (WasPressed(keyboard, Keys.F2)) {
            _showWireframe = !_showWireframe;
        }

        if (WasPressed(keyboard, Keys.F3)) {
            _showGuideLines = !_showGuideLines;
        }

        if (WasPressed(keyboard, Keys.F4)) {
            _lockLod = !_lockLod;
        }

        _previousKeyboardState = keyboard;

        _camera.Update(gameTime, GraphicsDevice.Viewport);
        if (!_lockLod && (!_preLodView.HasValue || !_preLodView.Value.Same(_camera.View))) {
            _sphere.Update(_camera.View);
            _preLodView = _camera.View;
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime) {
        GraphicsDevice.Clear(new Color(7, 11, 18));
        var renderOptions = new PlanetRenderOptions(
            _showSurface,
            _showWireframe,
            _showGuideLines
        );
        _sphere.Draw(_camera.View, renderOptions);
        var title = _performance.Observe(_sphere.Metrics, gameTime);
        if (title is not null) {
            _performanceTitle = title;
        }

        var lodState = _lockLod ? "frozen" : "running";
        Window.Title = $"{_performanceTitle} | F1 surface:{OnOff(_showSurface)} F2 mesh:{OnOff(_showWireframe)} F3 guides:{OnOff(_showGuideLines)} F4 lod:{lodState}";
        base.Draw(gameTime);
    }

    protected override void UnloadContent() {
        _performance.Dispose();
        _sphere.Dispose();
        base.UnloadContent();
    }

    private bool WasPressed(KeyboardState keyboard, Keys key) {
        return keyboard.IsKeyDown(key) && _previousKeyboardState.IsKeyUp(key);
    }

    private static string OnOff(bool value) {
        return value ? "on" : "off";
    }
}
