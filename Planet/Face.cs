using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace monogame.Planet;

internal sealed class Face : IDisposable {
    private readonly CubeFaceBasis _basis;
    private readonly Chunk _root;

    public Face(GraphicsDevice graphicsDevice, CubeFaceBasis basis, int chunkResolution, int maximumLod, float splitThresholdPixels, int triangleCount) {
        _basis = basis;
        _root = new Chunk(
            graphicsDevice,
            this,
            level: 0,
            x: 0,
            y: 0,
            chunkResolution,
            maximumLod,
            splitThresholdPixels,
            triangleCount
        );
    }

    public Vector3 Project(float u, float v) {
        return CubeSphereProjection.ToDirection(_basis, u, v);
    }

    public void Update(in SphereView view, ref int visibleChunkCount, ref int deepestLod) {
        _root.Update(view, ref visibleChunkCount, ref deepestLod);
    }

    public void Draw() {
        _root.Draw();
    }

    public void Dispose() {
        _root.Dispose();
    }
}
