using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using monogame.Lod;

namespace monogame.Debugging;

// Static facade; GameApp owns the GPU resource lifetime.
internal static class Debugger {
    private static readonly FrameProfiler s_profiler = new();
    private static readonly DebugPanel s_panel = new();
    private static ImGuiRenderer s_renderer;
    private static GpuTimer s_gpuTimer;
    private static Microsoft.Xna.Framework.Graphics.GraphicsDevice s_device;

    internal static bool SelectionFrozen => s_panel.SelectionFrozen;
    internal static SphereRenderOptions RenderOptions => new(
        s_panel.ShowSurface, s_panel.ShowWireframe, s_panel.ShowGuideLines);

    internal static void Initialize(Game game) {
        s_renderer = new ImGuiRenderer(game);
        s_device = game.GraphicsDevice;
        s_gpuTimer = new GpuTimer(s_device);
        s_device.DeviceResetting += OnDeviceResetting;
        s_device.DeviceReset += OnDeviceReset;
    }

    private static void OnDeviceResetting(object sender, System.EventArgs e) {
        s_gpuTimer.Dispose();
    }

    private static void OnDeviceReset(object sender, System.EventArgs e) {
        s_gpuTimer = new GpuTimer((Microsoft.Xna.Framework.Graphics.GraphicsDevice)sender);
    }

    internal static GpuTimer.Scope MeasureGpu() => s_profiler.Paused ? default : s_gpuTimer.Measure();
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
        DrawStatusBar();
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
    internal static void Dispose() {
        s_device.DeviceResetting -= OnDeviceResetting;
        s_device.DeviceReset -= OnDeviceReset;
        s_gpuTimer.Dispose();
        s_renderer.Dispose();
    }

    private static void DrawStatusBar() {
        var frame = s_profiler.Last;
        var fps = frame.IntervalTicks is > 0
            ? $"{1000.0 / FrameSnapshot.Milliseconds(frame.IntervalTicks.Value):F0}"
            : "--";
        var cpu = frame.HasFrame ? $"{FrameSnapshot.Milliseconds(frame.CpuTicks):F2}" : "--";
        var gpu = s_gpuTimer.Milliseconds is double milliseconds ? $"{milliseconds:F2}" : "--";
        var lod = frame.HasFrame ? $"{frame.Selection.TargetLod}/{frame.Selection.MaxLod}" : "--/--";

        // Foreground primitives remain visible with the panel closed and never capture input.
        var draw = ImGui.GetForegroundDrawList();
        var width = ImGui.GetIO().DisplaySize.X;
        var height = ImGui.GetTextLineHeight() + 8;
        draw.PushClipRect(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(width, height), true);
        var x = 12.0f;
        Column("FPS", fps, "00000", "", false);
        Column("CPU", cpu, "0000.00", "ms");
        Column("GPU", gpu, "0000.00", "ms");
        Column("LOD", lod, "00/00", "");
        if (s_panel.SelectionFrozen) Column("", "frozen", "frozen", "");
        if (s_profiler.Paused) Column("", "paused", "paused", "");
        draw.PopClipRect();

        void Column(string label, string value, string reference, string suffix, bool separator = true) {
            if (separator) {
                x += 12;
                draw.AddText(new System.Numerics.Vector2(x, 4), 0xffffffff, "|");
                x += ImGui.CalcTextSize("|").X + 12;
            }
            if (label.Length > 0) {
                draw.AddText(new System.Numerics.Vector2(x, 4), 0xffffffff, label);
                x += ImGui.CalcTextSize(label + " ").X;
            }
            var columnWidth = ImGui.CalcTextSize(reference).X;
            draw.PushClipRect(new System.Numerics.Vector2(x, 0),
                new System.Numerics.Vector2(x + columnWidth, height), true);
            draw.AddText(new System.Numerics.Vector2(x + columnWidth - ImGui.CalcTextSize(value).X, 4),
                0xffffffff, value);
            draw.PopClipRect();
            x += columnWidth;
            draw.AddText(new System.Numerics.Vector2(x, 4), 0xffffffff, suffix);
            x += ImGui.CalcTextSize(suffix).X;
        }
    }
}
