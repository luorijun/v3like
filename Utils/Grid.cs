using System;

namespace monogame.Utils;

internal static class RegularGrid
{
    public static ushort[] CreateTriangleIndices(int resolution)
    {
        if (resolution < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        if ((long)resolution * resolution > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                "The grid has more vertices than a 16-bit index can address.");
        }

        var indices = new ushort[(resolution - 1) * (resolution - 1) * 6];
        var index = 0;

        for (var y = 0; y < resolution - 1; y++)
        {
            for (var x = 0; x < resolution - 1; x++)
            {
                var upperLeft = (ushort)(y * resolution + x);
                var upperRight = (ushort)(upperLeft + 1);
                var lowerLeft = (ushort)(upperLeft + resolution);
                var lowerRight = (ushort)(lowerLeft + 1);

                indices[index++] = upperLeft;
                indices[index++] = upperRight;
                indices[index++] = lowerLeft;
                indices[index++] = upperRight;
                indices[index++] = lowerRight;
                indices[index++] = lowerLeft;
            }
        }

        return indices;
    }
}
