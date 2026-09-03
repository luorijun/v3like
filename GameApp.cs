using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Planet;

namespace monogame;

public sealed class GameApp : Game {
    private static readonly SphereConfiguration s_sphereConfiguration = new(
        MeshResolution: 16,
        TextureResolution: 256,
        ChunkCacheCapacity: 2000
    );

    private OrbitCamera _camera;
    private Sphere _sphere;
    private PerformanceMonitor _performance;
    private KeyboardState _previousKeyboardState = Keyboard.GetState();
    private bool _showSurface = true;
    private bool _showWireframe;
    private bool _showGuideLines;
    private bool _selectionFrozen;
    private View? _previousSelectionView;
    private string _performanceTitle = "Cube Sphere";

    public GameApp() {
        _ = new GraphicsDeviceManager(this) {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Cube Sphere";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent() {
        var surfaceEffect = Content.Load<Effect>("Effects/TileSurface");
        _sphere = Sphere.Create(
            s_sphereConfiguration,
            GraphicsDevice,
            surfaceEffect
        );
        _camera = new OrbitCamera();
        _performance = new PerformanceMonitor(GraphicsDevice);
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
            _selectionFrozen = !_selectionFrozen;
            if (!_selectionFrozen) {
                _previousSelectionView = null;
            }
        }

        _previousKeyboardState = keyboard;

        _camera.Update(gameTime, GraphicsDevice.Viewport);
        if (!_selectionFrozen
            && (!_previousSelectionView.HasValue
                || !_previousSelectionView.Value.Same(_camera.View))) {
            _sphere.Update(_camera.View);
            _previousSelectionView = _camera.View;
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime) {
        GraphicsDevice.Clear(new Color(7, 11, 18));
        _performance.BeginDraw();
        _sphere.Draw(_camera.View, new PlanetRenderOptions(
            _showSurface,
            _showWireframe,
            _showGuideLines
        ));
        var title = _performance.Observe(_sphere.Metrics, gameTime);
        if (title is not null) {
            _performanceTitle = title;
        }

        var selectionState = _selectionFrozen ? "frozen" : "running";
        Window.Title = $"{_performanceTitle} | F1 surface:{OnOff(_showSurface)} F2 wireframe:{OnOff(_showWireframe)} F3 guides:{OnOff(_showGuideLines)} F4 selection:{selectionState}";
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
