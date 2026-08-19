using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace monogame;

public sealed class GameApp : Game {
    private readonly GraphicsDeviceManager _graphics;
    private readonly OrbitCamera _camera = new();
    private Planet.Sphere _sphere;
    private KeyboardState _previousKeyboardState = Keyboard.GetState();
    private bool _showSurface = true;
    private bool _showWireframe;
    private bool _showGuideLines;

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
        _sphere = new Planet.Sphere(GraphicsDevice);
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

        _previousKeyboardState = keyboard;

        _camera.Update(gameTime, GraphicsDevice.Viewport);
        _sphere.Update(_camera.Position, OrbitCamera.FieldOfView, GraphicsDevice.Viewport.Height);

        Window.Title = $"Cube Sphere Terrain | LOD {_sphere.DeepestLod} | {_sphere.VisibleChunkCount} chunks | {_sphere.VisibleTriangleCount:N0} triangles | F1 surface:{OnOff(_showSurface)} F2 mesh:{OnOff(_showWireframe)} F3 guides:{OnOff(_showGuideLines)} | middle drag rotate, wheel zoom";

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime) {
        GraphicsDevice.Clear(new Color(7, 11, 18));
        var renderOptions = new Planet.PlanetRenderOptions(_showSurface, _showWireframe, _showGuideLines);
        _sphere.Draw(_camera.View, _camera.Projection, renderOptions);
        base.Draw(gameTime);
    }

    protected override void UnloadContent() {
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
