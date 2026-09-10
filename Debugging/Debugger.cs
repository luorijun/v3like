using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using monogame.Lod;

namespace monogame.Debugging;

// Static facade; GameApp owns the GPU resource lifetime.
internal static class Debugger {
    private static FrameProfiler s_profiler;
    private static DebugPanel s_panel;
    private static ImGuiRenderer s_renderer;
    private static GpuTimer s_gpuTimer;
    private static Microsoft.Xna.Framework.Graphics.GraphicsDevice s_device;

    internal static bool SelectionFrozen => s_panel.SelectionFrozen;
    internal static SphereRenderOptions RenderOptions => new(
        s_panel.Surface, s_panel.Wireframe, s_panel.Guides);

    internal static void Initialize(Game game, in DebuggerConfig config) {
        if (!float.IsFinite(config.UiScale) || config.UiScale <= 0) {
            throw new System.ArgumentOutOfRangeException(nameof(config), "UI scale must be finite and positive.");
        }
        if (!System.Enum.IsDefined(config.PeakMetric)) {
            throw new System.ArgumentOutOfRangeException(nameof(config), "Unknown peak metric.");
        }
        s_profiler = new FrameProfiler();
        s_profiler.SetMetric(config.PeakMetric);
        s_panel = new DebugPanel {
            Visible = config.Visible,
            Surface = config.Render.Surface,
            Wireframe = config.Render.Wireframe,
            Guides = config.Render.Guides,
        };
        s_renderer = new ImGuiRenderer(game, config.UiScale);
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
        if (InputManager.IsClick(Keys.F1)) s_panel.Surface = !s_panel.Surface;
        if (InputManager.IsClick(Keys.F2)) s_panel.Wireframe = !s_panel.Wireframe;
        if (InputManager.IsClick(Keys.F3)) s_panel.Guides = !s_panel.Guides;
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

internal readonly record struct DebuggerConfig(
    float UiScale,
    bool Visible,
    SphereRenderOptions Render,
    PeakMetric PeakMetric
);
