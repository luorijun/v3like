#if DEBUG
if (args.Length > 0) {
    if (args.Length != 2 || (args[0] != "--build-surface" && args[0] != "--build-terrain")) {
        System.Console.Error.WriteLine("Usage: dotnet run -c Debug -- <--build-surface|--build-terrain> <source.nc>");
        System.Environment.ExitCode = 1;
        return;
    }
    try {
        if (args[0] == "--build-surface") await monogame.Asset.SurfaceWriter.Write(args[1]);
        else await monogame.Asset.TerrainWriter.Write(args[1]);
    }
    catch (System.Exception error) {
        System.Console.Error.WriteLine(error.Message);
        System.Environment.ExitCode = 1;
    }
    return;
}
#endif

using var game = new monogame.GameApp();
game.Run();
