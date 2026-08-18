using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using monogame.Sphere;

namespace monogame;

public sealed class GameApp : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly OrbitCamera _camera = new();
    private CubeSphere _sphere;

    public GameApp()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Cube Sphere Terrain";
        Content.RootDirectory = "Content";
    }

    protected override void LoadContent()
    {
        _sphere = new CubeSphere(GraphicsDevice);
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape))
        {
            Exit();
        }

        _camera.Update(gameTime, GraphicsDevice.Viewport);
        _sphere.Update(
            _camera.Position,
            OrbitCamera.FieldOfView,
            GraphicsDevice.Viewport.Height);

        Window.Title = $"Cube Sphere Terrain | LOD {_sphere.DeepestLod} | {_sphere.VisibleChunkCount} chunks | {_sphere.VisibleTriangleCount:N0} triangles | arrows rotate, wheel zoom";

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(7, 11, 18));
        _sphere.Draw(_camera.View, _camera.Projection);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _sphere.Dispose();
        base.UnloadContent();
    }
}
