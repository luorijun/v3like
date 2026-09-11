using System;
using System.IO;
using monogame.Grid;
#if DEBUG
using System.Globalization;
using System.Threading.Tasks;
#endif

namespace monogame.Asset;

internal enum SurfaceKind : byte { Ocean = 0, Land = 1, Lake = 2 }

internal static class SurfaceFormat {
    // Little-endian: ASCII SURF, UInt32 version, then one SurfaceKind byte per TileId.
    // Version fixes the layout, category codes, tile numbering and geographic convention:
    // +Y north, +X (0°, 0°), -Z (90° east, 0°).
    internal const uint Magic = 0x46525553;
    internal const uint Version = 1;
    internal const int HeaderSize = sizeof(uint) * 2;
}

internal static class SurfaceReader {
    internal static SurfaceData Read() {
        var path = Path.Combine(AppContext.BaseDirectory, "Content", "surface.bin");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < SurfaceFormat.HeaderSize) {
            throw new InvalidDataException($"Surface asset '{path}' has an incomplete header.");
        }
        if (reader.ReadUInt32() != SurfaceFormat.Magic) {
            throw new InvalidDataException($"Surface asset '{path}' has an invalid file identifier.");
        }
        var version = reader.ReadUInt32();
        if (version != SurfaceFormat.Version) {
            throw new InvalidDataException($"Surface asset '{path}' has unsupported version {version}; expected {SurfaceFormat.Version}.");
        }
        var length = SurfaceFormat.HeaderSize + (long)Map.TileCount;
        if (stream.Length != length) {
            throw new InvalidDataException($"Surface asset '{path}' has length {stream.Length}; expected {length} bytes.");
        }
        var kinds = new SurfaceKind[Map.TileCount];
        for (var tile = 0; tile < kinds.Length; tile++) {
            var value = reader.ReadByte();
            if (value > (byte)SurfaceKind.Lake) {
                throw new InvalidDataException($"Surface asset '{path}' has invalid category {value} for TileId {tile}.");
            }
            kinds[tile] = (SurfaceKind)value;
        }
        return new SurfaceData(kinds);
    }
}

#if DEBUG
internal static class SurfaceWriter {
    internal static async Task Write(string source) {
        if (!File.Exists("monogame.csproj")) {
            throw new InvalidOperationException("Run --build-surface from the project root.");
        }
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Surface source was not found.", source);

        var directory = Path.GetFullPath(Path.Combine("artifacts", "surface"));
        Directory.CreateDirectory(directory);
        var tiles = Path.Combine(directory, "tiles.tsv");
        var samples = Path.Combine(directory, "kinds.tsv");
        Console.WriteLine($"Exporting {Map.TileCount} tile centers...");
        Gmt.ExportCenters(tiles);
        Console.WriteLine($"Sampling {source} with GMT...");
        await Gmt.Sample(source, tiles, samples);
        var kinds = ReadKinds(samples);

        var path = Path.GetFullPath(Path.Combine("Content", "surface.bin"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var writer = new BinaryWriter(File.Create(temp))) {
                writer.Write(SurfaceFormat.Magic);
                writer.Write(SurfaceFormat.Version);
                foreach (var kind in kinds) writer.Write((byte)kind);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Console.WriteLine($"Wrote {kinds.Length} surface categories to {path} ({new FileInfo(path).Length} bytes).");
    }

    private static SurfaceKind[] ReadKinds(string path) {
        var kinds = new SurfaceKind[Map.TileCount];
        var seen = new bool[Map.TileCount];
        var count = 0;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path)) {
            lineNumber++;
            var columns = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 4
                || !int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tile)
                || tile < 0 || tile >= kinds.Length) {
                throw new InvalidDataException($"Invalid sample or TileId at {path}:{lineNumber}.");
            }
            if (seen[tile]) throw new InvalidDataException($"Duplicate TileId {tile} at {path}:{lineNumber}.");
            if (!double.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value)) {
                throw new InvalidDataException($"Invalid surface category for TileId {tile} at {path}:{lineNumber}.");
            }
            kinds[tile] = value switch {
                0 => SurfaceKind.Ocean,
                1 or 3 => SurfaceKind.Land,
                2 or 4 => SurfaceKind.Lake,
                _ => throw new InvalidDataException($"Unknown GSHHG category {value} for TileId {tile} at {path}:{lineNumber}."),
            };
            seen[tile] = true;
            count++;
        }
        if (count != kinds.Length) {
            throw new InvalidDataException($"Surface samples contain {count} tiles; expected {kinds.Length}.");
        }
        return kinds;
    }
}
#endif

internal sealed class SurfaceData {
    private readonly SurfaceKind[] _kinds;

    internal int Count => _kinds.Length;

    internal SurfaceData(SurfaceKind[] kinds) {
        _kinds = kinds;
    }

    internal SurfaceKind GetKind(int tileId) => _kinds[tileId];
}
