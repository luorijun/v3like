using System;
using System.IO;
using System.Security.Cryptography;
using monogame.Planet;
using monogame.Terrain;

return AssetBuilderCommand.Run(args);

internal static class AssetBuilderCommand {
    public static int Run(string[] args) {
        if (args.Length != 1 || args[0] is "-h" or "--help") {
            PrintUsage();
            return args.Length == 1 ? 0 : 2;
        }

        if (!string.Equals(args[0], "planet", StringComparison.OrdinalIgnoreCase)) {
            Console.Error.WriteLine($"Unknown asset: {args[0]}");
            PrintUsage();
            return 2;
        }

        try {
            GeneratePlanetAsset();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine($"Planet asset generation failed: {exception.Message}");
            return 1;
        }
    }

    private static void GeneratePlanetAsset() {
        var projectRoot = FindProjectRoot();
        var outputPath = Path.Combine(projectRoot, PlanetAssetRecipe.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp"
        );

        try {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan
            )) {
                Asset.Write(
                    destination,
                    PlanetAssetRecipe.ReferenceRadius,
                    PlanetAssetRecipe.ChunkResolution,
                    PlanetAssetRecipe.MaximumLod,
                    PlanetAssetRecipe.ElevationQuantizationStep,
                    PlanetAssetRecipe.CreateElevationSource()
                );
                destination.Flush(flushToDisk: true);
            }

            Validate(temporaryPath);
            File.Move(temporaryPath, outputPath, overwrite: true);

            var file = new FileInfo(outputPath);
            Console.WriteLine($"Generated {Path.GetRelativePath(projectRoot, outputPath)}");
            Console.WriteLine($"Size: {file.Length:N0} bytes");
            Console.WriteLine($"SHA-256: {CalculateSha256(outputPath)}");
        }
        finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(string path) {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan
        );
        _ = Asset.Read(source);
        if (source.Position != source.Length) {
            throw new InvalidDataException("The generated asset contains trailing data.");
        }
    }

    private static string CalculateSha256(string path) {
        using var source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static string FindProjectRoot() {
        var root = FindProjectRoot(new DirectoryInfo(Directory.GetCurrentDirectory()))
            ?? FindProjectRoot(new DirectoryInfo(AppContext.BaseDirectory));
        return root?.FullName
            ?? throw new DirectoryNotFoundException("Cannot locate monogame.csproj.");
    }

    private static DirectoryInfo? FindProjectRoot(DirectoryInfo? directory) {
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "monogame.csproj"))) {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void PrintUsage() {
        Console.WriteLine("Usage: dotnet run --project tools/AssetBuilder -- planet");
    }
}

internal static class PlanetAssetRecipe {
    internal const string OutputPath = "Content/sphere.asset";
    internal const float ReferenceRadius = 1.0f;
    internal const int ChunkResolution = 17;
    internal const int MaximumLod = 5;
    internal const float ElevationQuantizationStep = 0.00001f;
    private const float MaximumAbsoluteElevation = 0.0015f;

    internal static IReferenceElevationSource CreateElevationSource() {
        return new MultiScaleReferenceElevationSource(MaximumAbsoluteElevation);
    }
}
