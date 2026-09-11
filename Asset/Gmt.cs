#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using monogame.Grid;

namespace monogame.Asset;

// Shared coordinate export and nearest-neighbor sampling for offline asset writers.
internal static class Gmt {
    internal static void ExportCenters(string path) {
        var centers = Map.CreateTileCenters();
        using var writer = new StreamWriter(path);
        for (var tile = 0; tile < centers.Length; tile++) {
            var p = centers[tile];
            var lon = Math.Atan2(-p.Z, p.X) * 180.0 / Math.PI;
            var lat = Math.Atan2(p.Y, Math.Sqrt((double)p.X * p.X + (double)p.Z * p.Z))
                * 180.0 / Math.PI;
            writer.WriteLine(FormattableString.Invariant($"{lon:R}\t{lat:R}\t{tile}"));
        }
    }

    internal static async Task Sample(string source, string tiles, string samples) {
        var start = new ProcessStartInfo("gmt") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(samples)!,
        };
        foreach (var argument in new[] {
            "grdtrack", tiles, "-G" + source, "-nn", "-N", "-fg",
            "--FORMAT_GEO_OUT=D", "--FORMAT_FLOAT_OUT=%.17g", "--IO_HEADER=false",
        }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start GMT.");
        // Drain both pipes concurrently so diagnostics cannot block the sample output.
        var errors = process.StandardError.ReadToEndAsync();
        using (var output = File.Create(samples)) {
            await process.StandardOutput.BaseStream.CopyToAsync(output);
        }
        await process.WaitForExitAsync();
        var diagnostics = await errors;
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"GMT failed with exit code {process.ExitCode}: {diagnostics.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(diagnostics)) Console.Error.WriteLine(diagnostics.Trim());
    }
}
#endif
