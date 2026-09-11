#if DEBUG
if (args.Length > 0) {
    if (args.Length != 2 || args[0] != "--build-terrain") {
        System.Console.Error.WriteLine("Usage: dotnet run -c Debug -- --build-terrain <source.nc>");
        System.Environment.ExitCode = 1;
        return;
    }
    try {
        await monogame.Asset.TerrainWriter.Write(args[1]);
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
