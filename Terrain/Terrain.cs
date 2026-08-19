using System;
using Microsoft.Xna.Framework;

namespace monogame.Terrain;

internal readonly record struct TerrainSample(float Elevation, Color Color);

internal static class ProceduralTerrain {
    public const float MaximumElevation = 0.0015f;

    public static TerrainSample Sample(Vector3 direction) {
        var continental = ContinentalNoise(direction);
        if (continental <= 0.035) {
            var waterColor = continental < -0.12
                ? new Color(4, 30, 62)
                : new Color(20, 92, 132);
            return new TerrainSample(0.0f, waterColor);
        }

        var land = (float)Math.Min(1.0, (continental - 0.035) * 1.8);
        var detail = (float)(Math.Sin((direction.X + direction.Z) * 31.0 + direction.Y * 7.0) * 0.5
            + Math.Cos(direction.Y * 43.0 - direction.X * 11.0) * 0.5);
        var elevation = MathHelper.Clamp(0.00005f + land * 0.00135f + Math.Max(0.0f, detail) * 0.00012f, 0.0f, MaximumElevation);

        var color = land < 0.08f
            ? new Color(194, 174, 111)
            : elevation < 0.00052f
                ? Color.Lerp(
                    new Color(67, 126, 72),
                    new Color(100, 142, 73),
                    ElevationRatio(elevation, 0.00052f))
                : elevation < 0.00105f
                    ? Color.Lerp(
                        new Color(111, 105, 77),
                        new Color(128, 119, 99),
                        ElevationRatio(elevation, 0.00105f))
                    : elevation < 0.00132f
                        ? new Color(116, 111, 105)
                        : new Color(232, 235, 231);

        return new TerrainSample(elevation, color);
    }

    private static float ElevationRatio(float elevation, float upperBound) {
        return MathHelper.Clamp(elevation / upperBound, 0.0f, 1.0f);
    }

    private static double ContinentalNoise(Vector3 direction) {
        var x = (double)direction.X;
        var y = (double)direction.Y;
        var z = (double)direction.Z;

        return 0.42 * Math.Sin(2.6 * x + 1.4 * Math.Sin(2.1 * z))
            + 0.30 * Math.Cos(3.1 * z - 0.8 * y)
            + 0.18 * Math.Sin(6.7 * (x + y - z))
            + 0.10 * Math.Cos(13.0 * y + 4.0 * x);
    }
}
