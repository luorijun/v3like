using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using monogame.Asset;

namespace monogame;

// Owns shared assets. MonoGame owns the content manager and graphics device.
internal static class GameManager {
    private static ContentManager s_content;

    internal static GraphicsDevice GraphicsDevice { get; private set; }
    internal static Effect SurfaceEffect { get; private set; }
    internal static TerrainData Terrain { get; private set; }

    internal static void Initialize(Game game) {
        if (s_content != null) {
            throw new InvalidOperationException("GameManager is already initialized.");
        }

        game.Content.RootDirectory = "Content";
        var terrain = TerrainReader.Read();
        SurfaceEffect = game.Content.Load<Effect>("Effects/TileSurface");
        Terrain = terrain;
        GraphicsDevice = game.GraphicsDevice;
        s_content = game.Content;
    }

    // Called after all consumers have released their own resources.
    internal static void Dispose() {
        s_content?.Unload();
        s_content = null;
        SurfaceEffect = null;
        Terrain = null;
        GraphicsDevice = null;
    }
}
