using System;
using System.IO;
using monogame.Grid;
#if DEBUG
using System.Globalization;
using System.Threading.Tasks;
#endif

namespace monogame.Asset;

internal static class TerrainFormat {
    // Little-endian: ASCII TERR, UInt32 version, then Int16 elevations in meters.
    // Version 1 also fixes the tile count, numbering and geographic convention.
    // Geographic axes: +Y north, +X (0°, 0°), -Z (90° east, 0°).
    internal const uint Magic = 0x52524554;
    internal const uint Version = 1;
    internal const int HeaderSize = sizeof(uint) * 2;
}

internal static class TerrainReader {
    internal static TerrainData Read() {
        var path = Path.Combine(AppContext.BaseDirectory, "Content", "terrain.bin");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < TerrainFormat.HeaderSize) {
            throw new InvalidDataException($"Terrain asset '{path}' has an incomplete header.");
        }
        if (reader.ReadUInt32() != TerrainFormat.Magic) {
            throw new InvalidDataException($"Terrain asset '{path}' has an invalid file identifier.");
        }
        var version = reader.ReadUInt32();
        if (version != TerrainFormat.Version) {
            throw new InvalidDataException($"Terrain asset '{path}' has unsupported version {version}; expected {TerrainFormat.Version}.");
        }
        var length = TerrainFormat.HeaderSize + (long)Map.TileCount * sizeof(short);
        if (stream.Length != length) {
            throw new InvalidDataException($"Terrain asset '{path}' has length {stream.Length}; expected {length} bytes.");
        }

        var heights = new short[Map.TileCount];
        for (var tile = 0; tile < heights.Length; tile++) {
            heights[tile] = reader.ReadInt16();
        }
        return new TerrainData(heights);
    }
}

#if DEBUG
internal static class TerrainWriter {
    internal static async Task Write(string source) {
        // The command runs from the project root; output must not target the executable directory.
        if (!File.Exists("monogame.csproj")) {
            throw new InvalidOperationException("Run --build-terrain from the project root.");
        }
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Terrain source was not found.", source);

        var directory = Path.GetFullPath(Path.Combine("artifacts", "terrain"));
        Directory.CreateDirectory(directory);
        var tiles = Path.Combine(directory, "tiles.tsv");
        var samples = Path.Combine(directory, "heights.tsv");
        Console.WriteLine($"Exporting {Map.TileCount} tile centers...");
        Gmt.ExportCenters(tiles);
        Console.WriteLine($"Sampling {source} with GMT...");
        await Gmt.Sample(source, tiles, samples);
        var heights = ReadHeights(samples);

        var path = Path.GetFullPath(Path.Combine("Content", "terrain.bin"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var writer = new BinaryWriter(File.Create(temp))) {
                writer.Write(TerrainFormat.Magic);
                writer.Write(TerrainFormat.Version);
                foreach (var height in heights) writer.Write(height);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Console.WriteLine($"Wrote {heights.Length} heights to {path} ({new FileInfo(path).Length} bytes).");
    }

    private static short[] ReadHeights(string path) {
        var heights = new short[Map.TileCount];
        var seen = new bool[Map.TileCount];
        var count = 0;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path)) {
            lineNumber++;
            var columns = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 4
                || !int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tile)
                || tile < 0 || tile >= heights.Length) {
                throw new InvalidDataException($"Invalid sample or TileId at {path}:{lineNumber}.");
            }
            if (seen[tile]) throw new InvalidDataException($"Duplicate TileId {tile} at {path}:{lineNumber}.");
            if (!double.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                || !double.IsFinite(height)) {
                throw new InvalidDataException($"Invalid height for TileId {tile} at {path}:{lineNumber}.");
            }
            var rounded = Math.Round(height, MidpointRounding.AwayFromZero);
            if (rounded < short.MinValue || rounded > short.MaxValue) {
                throw new InvalidDataException($"Height {height} for TileId {tile} exceeds Int16 range.");
            }
            heights[tile] = (short)rounded;
            seen[tile] = true;
            count++;
        }
        if (count != heights.Length) {
            throw new InvalidDataException($"Terrain samples contain {count} tiles; expected {heights.Length}.");
        }
        return heights;
    }
}
#endif

internal sealed class TerrainData {
    private readonly short[] _heights;

    internal int Count => _heights.Length;

    internal TerrainData(short[] heights) {
        _heights = heights;
    }

    internal short GetHeight(int tileId) => _heights[tileId];
}
