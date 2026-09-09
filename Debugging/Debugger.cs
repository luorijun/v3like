using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using monogame.Planet;

namespace monogame.Debugging;

// Static facade; GameApp owns the GPU resource lifetime.
internal static class Debugger {
    private static readonly FrameProfiler s_profiler = new();
    private static readonly DebugPanel s_panel = new();
    private static ImGuiRenderer s_renderer;

    internal static bool SelectionFrozen => s_panel.SelectionFrozen;
    internal static PlanetRenderOptions RenderOptions => new(
        s_panel.ShowSurface, s_panel.ShowWireframe, s_panel.ShowGuideLines);

    internal static void Initialize(Game game) => s_renderer = new ImGuiRenderer(game);
    internal static FrameProfiler.Scope Measure(string name) => s_profiler.Measure(name);

    internal static void Update(GameTime gameTime) {
        using var timing = Measure("Debug UI layout");
        if (InputManager.IsClick(Keys.F1)) s_panel.ShowSurface = !s_panel.ShowSurface;
        if (InputManager.IsClick(Keys.F2)) s_panel.ShowWireframe = !s_panel.ShowWireframe;
        if (InputManager.IsClick(Keys.F3)) s_panel.ShowGuideLines = !s_panel.ShowGuideLines;
        if (InputManager.IsClick(Keys.F4)) s_panel.SelectionFrozen = !s_panel.SelectionFrozen;
        if (InputManager.IsClick(Keys.F5)) s_panel.Visible = !s_panel.Visible;

        s_renderer.BeginLayout(gameTime, s_panel.Visible);
        s_panel.Build(s_profiler);
        // The UI handles input first and declares its ownership before scene interaction.
        var io = ImGui.GetIO();
        InputManager.Focus = s_panel.Visible && (io.WantCaptureMouse || io.WantCaptureKeyboard)
            ? InputFocus.UI : InputFocus.Scene;
        s_renderer.EndLayout();
    }

    internal static void Draw() {
        using var timing = Measure("Debug UI draw");
        s_renderer.Draw();
    }

    internal static void CompleteFrame(in SelectionMetrics selection) => s_profiler.CompleteFrame(selection);
    internal static void Dispose() => s_renderer.Dispose();
}
