using System;
using System.Collections.Generic;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace monogame.Debugging;

internal sealed class DebugPanel {
    internal bool Visible;
    internal bool ShowSurface = true;
    internal bool ShowWireframe;
    internal bool ShowGuideLines;
    internal bool SelectionFrozen;
    private bool _selfTime;
    private float _zoom = 1;
    private float _pan;
    internal void Build(FrameProfiler profiler) {
        if (!Visible) return;

        ImGui.SetNextWindowPos(new Vector2(12, 40), ImGuiCond.FirstUseEver);
        var screen = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(new Vector2(Math.Min(790, screen.X - 24), Math.Min(680, screen.Y - 52)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 240), new Vector2(float.MaxValue));
        var expanded = ImGui.Begin("Debug | Frame profiler", ref Visible);
        if (expanded) {
            ImGui.TextDisabled("F5: show / hide   |   CPU timing breakdown");
            if (ImGui.Button(profiler.Paused ? "Resume capture" : "Pause capture")) {
                profiler.SetPaused(!profiler.Paused);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset slowest")) profiler.ResetPeak();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo("Rank by", profiler.Metric == PeakMetric.FrameInterval ? "Frame interval" : "CPU work")) {
                if (ImGui.Selectable("Frame interval", profiler.Metric == PeakMetric.FrameInterval)) profiler.SetMetric(PeakMetric.FrameInterval);
                if (ImGui.Selectable("CPU work", profiler.Metric == PeakMetric.CpuWork)) profiler.SetMetric(PeakMetric.CpuWork);
                ImGui.EndCombo();
            }

            ImGui.Checkbox("Surface [F1]", ref ShowSurface);
            ImGui.SameLine();
            ImGui.Checkbox("Wireframe [F2]", ref ShowWireframe);
            ImGui.SameLine();
            ImGui.Checkbox("Guides [F3]", ref ShowGuideLines);
            ImGui.Checkbox("Freeze selection [F4]", ref SelectionFrozen);
            ImGui.Separator();

            var last = profiler.Last;
            var peak = profiler.Peak;
            if (ImGui.BeginTable("Frames", 2, ImGuiTableFlags.SizingStretchSame)) {
                ImGui.TableNextColumn();
                FrameDetails("LAST", last);
                ImGui.TableNextColumn();
                FrameDetails("SLOWEST", peak);
                ImGui.EndTable();
            }
            ImGui.Separator();
            if (ImGui.BeginTabBar("Profiler views")) {
                if (ImGui.BeginTabItem("Step comparison")) {
                    ImGui.Checkbox("Show self time (exclude children)", ref _selfTime);
                    ImGui.TextDisabled("Calls are per frame; selection / LOD values describe that frame's state.");
                    if (ImGui.BeginTable("Steps", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable)) {
                        ImGui.TableSetupColumn("Step", ImGuiTableColumnFlags.WidthStretch, 2.4f);
                        ImGui.TableSetupColumn("Last ms");
                        ImGui.TableSetupColumn("Slowest ms");
                        ImGui.TableSetupColumn("Last calls");
                        ImGui.TableSetupColumn("Slowest calls");
                        ImGui.TableHeadersRow();
                        DrawSteps(last.HasFrame ? last.Timings : null, peak.HasFrame ? peak.Timings : null, Math.Max(last.CpuTicks, peak.HasFrame ? peak.CpuTicks : 0));
                        ImGui.EndTable();
                    }
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Frame timelines")) {
                    ImGui.TextWrapped("Same time scale for both frames. Gray gaps include framework waits / unmeasured work. Hover a bar for details.");
                    ImGui.SliderFloat("Zoom", ref _zoom, 1, 20, "%.1fx");
                    if (_zoom > 1) ImGui.SliderFloat("Position", ref _pan, 0, 1, "%.2f");
                    var extent = Math.Max(last.HasFrame ? last.End - last.Start : 0,
                        peak.HasFrame ? peak.End - peak.Start : 0);
                    var window = Math.Max(1, extent / (double)_zoom);
                    var offset = (extent - window) * _pan;
                    ImGui.TextDisabled($"Visible: {FrameSnapshot.Milliseconds((long)offset):F2} - {FrameSnapshot.Milliseconds((long)(offset + window)):F2} ms");
                    Timeline("Last", last, offset, window);
                    Timeline("Slowest", peak, offset, window);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private static void FrameDetails(string label, FrameSnapshot frame) {
        ImGui.TextUnformatted(frame.HasFrame ? $"{label}   #{frame.Number}" : $"{label}   waiting for a frame");
        if (!frame.HasFrame) return;
        ImGui.TextUnformatted($"Interval: {(frame.IntervalTicks.HasValue ? $"{FrameSnapshot.Milliseconds(frame.IntervalTicks.Value):F3} ms" : "-- (first frame)")}");
        ImGui.TextUnformatted($"CPU work: {FrameSnapshot.Milliseconds(frame.CpuTicks):F3} ms");
        var state = frame.Selection;
        ImGui.TextUnformatted($"LOD {state.TargetLod} | Active {state.ActiveChunks} | Visited {state.VisitedNodes}");
        ImGui.TextUnformatted($"Culled: horizon {state.HorizonRejected}, frustum {state.FrustumRejected}");
    }

    private void DrawSteps(Dictionary<string, StepTiming> last, Dictionary<string, StepTiming> peak, long scale) {
        if (last is not null) {
            foreach (var entry in last) {
                StepTiming other = null;
                peak?.TryGetValue(entry.Key, out other);
                DrawStep(entry.Key, entry.Value, other, scale);
            }
        }
        if (peak is not null) {
            foreach (var entry in peak) {
                if (last is null || !last.ContainsKey(entry.Key)) DrawStep(entry.Key, null, entry.Value, scale);
            }
        }
    }

    private void DrawStep(string name, StepTiming last, StepTiming peak, long scale) {
        var hasChildren = (last?.Children.Count ?? 0) > 0 || (peak?.Children.Count ?? 0) > 0;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (!hasChildren) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        // Push the full name as an ID; display text separately so names containing ## remain literal.
        ImGui.PushID(name);
        var open = ImGui.TreeNodeEx("##step", flags);
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        Timing(last, scale);
        ImGui.TableNextColumn();
        Timing(peak, scale);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(last?.Calls.ToString() ?? "--");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(peak?.Calls.ToString() ?? "--");
        if (open && hasChildren) {
            DrawSteps(last?.Children, peak?.Children, scale);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private void Timing(StepTiming timing, long scale) {
        if (timing is null) { ImGui.TextDisabled("--"); return; }
        var ticks = _selfTime ? timing.SelfTicks : timing.TotalTicks;
        ImGui.TextUnformatted($"{FrameSnapshot.Milliseconds(ticks):F3}");
        ImGui.ProgressBar(scale == 0 ? 0 : (float)(ticks / (double)scale), new Vector2(-1, 3), "");
    }
    private static void Timeline(string label, FrameSnapshot frame, double offset, double window) {
        ImGui.TextUnformatted(label);
        if (!frame.HasFrame) { ImGui.TextDisabled("Waiting for a frame"); return; }
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        const float rowHeight = 22;
        var depth = 0;
        foreach (var span in frame.Spans) {
            var level = 1;
            for (var parent = span.Parent; parent >= 0; parent = frame.Buffer[parent].Parent) level++;
            depth = Math.Max(depth, level);
        }
        var size = new Vector2(width, (depth + 1) * rowHeight);
        ImGui.InvisibleButton(label + "Timeline", size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, 0xff242424);
        var rootRight = Math.Clamp((float)((frame.End - frame.Start - offset) / window * width), 0, width);
        draw.AddRectFilled(origin, origin + new Vector2(rootRight, rowHeight - 2), 0xff555555);
        draw.AddText(origin + new Vector2(3, 2), 0xffffffff, "Frame");
        var hoverIndex = -1;
        for (var i = 0; i < frame.Count; i++) {
            var span = frame.Buffer[i];
            var level = 1;
            for (var parent = span.Parent; parent >= 0; parent = frame.Buffer[parent].Parent) level++;
            var left = (float)((span.Start - frame.Start - offset) / window * width);
            var right = (float)((span.End - frame.Start - offset) / window * width);
            if (right < 0 || left > width) continue;
            var min = origin + new Vector2(Math.Max(0, left), level * rowHeight);
            var max = origin + new Vector2(Math.Min(width, Math.Max(left + 1, right)), (level + 1) * rowHeight - 2);
            var hue = (uint)StringComparer.Ordinal.GetHashCode(span.Name) / (float)uint.MaxValue;
            ImGui.ColorConvertHSVtoRGB(hue, 0.55f, 0.70f, out var r, out var g, out var b);
            draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 1)), 2);
            if (max.X - min.X > 65) {
                draw.PushClipRect(min, max, true);
                draw.AddText(min + new Vector2(3, 2), 0xffffffff, span.Name);
                draw.PopClipRect();
            }
            if (hovered && ImGui.IsMouseHoveringRect(min, max)) hoverIndex = i;
        }
        draw.PopClipRect();
        if (hoverIndex >= 0) {
            var span = frame.Buffer[hoverIndex];
            ImGui.SetTooltip($"{span.Name}\nStart {FrameSnapshot.Milliseconds(span.Start - frame.Start):F3} ms\nDuration {FrameSnapshot.Milliseconds(span.End - span.Start):F3} ms");
        }
    }
}
