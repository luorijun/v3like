using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Planet;

namespace monogame;

public sealed class GameApp : Game {
    private readonly GraphicsDeviceManager _graphics;
    private readonly OrbitCamera _camera = new();
    private Sphere _sphere;

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
        using var stream = TitleContainer.OpenStream("Content/sphere.asset");
        var data = Asset.Read(stream);
        _sphere = Sphere.Create(
            data,
            GraphicsDevice,
            splitThreshold: 1.5f,
            mergeThreshold: 1.2f,
            cacheCapacity: 8192
        );
    }

    protected override void Update(GameTime gameTime) {
        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape)) {
            Exit();
        }

        if (_camera.Update(gameTime, GraphicsDevice.Viewport)) {
            _sphere.Update(_camera.View);
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime) {
        GraphicsDevice.Clear(new Color(7, 11, 18));
        _sphere.Draw(_camera.View);
        base.Draw(gameTime);
    }

    protected override void UnloadContent() {
        _sphere.Dispose();
        base.UnloadContent();
    }
}
