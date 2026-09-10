using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Vector2 = System.Numerics.Vector2;

namespace monogame.Debugging;

// Font-backed debug widgets only. Owns the ImGui context, input bridge, and GPU resources.
internal sealed class ImGuiRenderer : IDisposable {
    private readonly Game _game;
    private readonly GraphicsDevice _device;
    private readonly IntPtr _context;
    private readonly Texture2D _font;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _rasterizer = new() {
        CullMode = CullMode.None,
        ScissorTestEnable = true,
    };
    private readonly VertexDeclaration _vertexDeclaration = new(20,
        new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
        new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));
    private DynamicVertexBuffer _vertices;
    private DynamicIndexBuffer _indices;
    private byte[] _vertexBytes = [];
    private short[] _indexData = [];

    private bool _layoutReady;
    private readonly List<char> _textInput = [];

    internal unsafe ImGuiRenderer(Game game, float uiScale) {
        _game = game;
        _device = game.GraphicsDevice;
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.NativePtr->IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.FontGlobalScale = uiScale;
        ImGui.StyleColorsDark();
        ImGui.GetStyle().ScaleAllSizes(uiScale);
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        var colors = new byte[width * height * 4];
        Marshal.Copy(pixels, colors, 0, colors.Length);
        _font = new Texture2D(_device, width, height, false, SurfaceFormat.Color);
        _font.SetData(colors);
        io.Fonts.SetTexID(new IntPtr(1));
        io.Fonts.ClearTexData();
        _effect = new BasicEffect(_device) {
            TextureEnabled = true,
            VertexColorEnabled = true,
            World = Matrix.Identity,
            View = Matrix.Identity,
            Texture = _font,
        };
        game.Window.TextInput += OnTextInput;
    }

    private void OnTextInput(object sender, TextInputEventArgs e) {
        if (_game.IsActive && !char.IsControl(e.Character)) _textInput.Add(e.Character);
    }

    internal void BeginLayout(GameTime gameTime, bool visible) {
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_device.Viewport.Width, _device.Viewport.Height);
        io.DeltaTime = Math.Max((float)gameTime.ElapsedGameTime.TotalSeconds, 0.000001f);
        var mouse = InputManager.MousePosition;
        var active = _game.IsActive && visible;
        if (active) {
            foreach (var character in _textInput) io.AddInputCharacter(character);
        }
        _textInput.Clear();
        io.AddFocusEvent(active);
        io.AddMousePosEvent(active ? mouse.X : -float.MaxValue, active ? mouse.Y : -float.MaxValue);
        io.AddMouseButtonEvent(0, active && InputManager.IsDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, active && InputManager.IsDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, active && InputManager.IsDown(MouseButton.Middle));
        io.AddMouseWheelEvent(0, active ? InputManager.ScrollDelta / 120f : 0);

        for (var i = 0; i < 26; i++) Key((ImGuiKey)((int)ImGuiKey.A + i), (Keys)((int)Keys.A + i));
        for (var i = 0; i < 10; i++) Key((ImGuiKey)((int)ImGuiKey._0 + i), (Keys)((int)Keys.D0 + i));
        Key(ImGuiKey.Tab, Keys.Tab);
        Key(ImGuiKey.LeftArrow, Keys.Left);
        Key(ImGuiKey.RightArrow, Keys.Right);
        Key(ImGuiKey.UpArrow, Keys.Up);
        Key(ImGuiKey.DownArrow, Keys.Down);
        Key(ImGuiKey.PageUp, Keys.PageUp);
        Key(ImGuiKey.PageDown, Keys.PageDown);
        Key(ImGuiKey.Home, Keys.Home);
        Key(ImGuiKey.End, Keys.End);
        Key(ImGuiKey.Insert, Keys.Insert);
        Key(ImGuiKey.Delete, Keys.Delete);
        Key(ImGuiKey.Backspace, Keys.Back);
        Key(ImGuiKey.Space, Keys.Space);
        Key(ImGuiKey.Enter, Keys.Enter);
        Key(ImGuiKey.Escape, Keys.Escape);
        io.AddKeyEvent(ImGuiKey.ModCtrl, active && (InputManager.IsDown(Keys.LeftControl) || InputManager.IsDown(Keys.RightControl)));
        io.AddKeyEvent(ImGuiKey.ModShift, active && (InputManager.IsDown(Keys.LeftShift) || InputManager.IsDown(Keys.RightShift)));
        io.AddKeyEvent(ImGuiKey.ModAlt, active && (InputManager.IsDown(Keys.LeftAlt) || InputManager.IsDown(Keys.RightAlt)));
        io.AddKeyEvent(ImGuiKey.ModSuper, active && (InputManager.IsDown(Keys.LeftWindows) || InputManager.IsDown(Keys.RightWindows)));
        ImGui.NewFrame();

        void Key(ImGuiKey imgui, Keys key) => io.AddKeyEvent(imgui, active && InputManager.IsDown(key));
    }

    // End each Update's layout, even when the fixed-step loop skips its Draw.
    internal void EndLayout() {
        ImGui.EndFrame();
        _layoutReady = true;
    }

    internal void Draw() {
        if (!_layoutReady) return;
        _layoutReady = false;
        ImGui.Render();
        var data = ImGui.GetDrawData();
        if (data.TotalVtxCount == 0) return;

        if (_vertices is null || _vertices.VertexCount < data.TotalVtxCount) {
            _vertices?.Dispose();
            var capacity = Math.Max(4096, data.TotalVtxCount * 2);
            _vertices = new DynamicVertexBuffer(_device, _vertexDeclaration, capacity, BufferUsage.WriteOnly);
            _vertexBytes = new byte[capacity * 20];
        }
        if (_indices is null || _indices.IndexCount < data.TotalIdxCount) {
            _indices?.Dispose();
            var capacity = Math.Max(8192, data.TotalIdxCount * 2);
            _indices = new DynamicIndexBuffer(_device, IndexElementSize.SixteenBits, capacity, BufferUsage.WriteOnly);
            _indexData = new short[capacity];
        }
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var i = 0; i < data.CmdListsCount; i++) {
            var list = data.CmdLists[i];
            Marshal.Copy(list.VtxBuffer.Data, _vertexBytes, vertexOffset * 20, list.VtxBuffer.Size * 20);
            Marshal.Copy(list.IdxBuffer.Data, _indexData, indexOffset, list.IdxBuffer.Size);
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }
        _vertices.SetData(0, _vertexBytes, 0, data.TotalVtxCount * 20, 1, SetDataOptions.Discard);
        _indices.SetData(_indexData, 0, data.TotalIdxCount, SetDataOptions.Discard);

        var blend = _device.BlendState;
        var depth = _device.DepthStencilState;
        var rasterizer = _device.RasterizerState;
        var scissor = _device.ScissorRectangle;
        var sampler = _device.SamplerStates[0];
        var texture = _device.Textures[0];
        var indices = _device.Indices;
        try {
            _device.BlendState = BlendState.NonPremultiplied;
            _device.DepthStencilState = DepthStencilState.None;
            _device.RasterizerState = _rasterizer;
            _device.SamplerStates[0] = SamplerState.LinearClamp;
            _device.SetVertexBuffer(_vertices);
            _device.Indices = _indices;
            _effect.Projection = Matrix.CreateOrthographicOffCenter(
                data.DisplayPos.X, data.DisplayPos.X + data.DisplaySize.X,
                data.DisplayPos.Y + data.DisplaySize.Y, data.DisplayPos.Y, 0, 1);
            vertexOffset = 0;
            indexOffset = 0;
            for (var i = 0; i < data.CmdListsCount; i++) {
                var list = data.CmdLists[i];
                for (var j = 0; j < list.CmdBuffer.Size; j++) {
                    var command = list.CmdBuffer[j];
                    if (command.UserCallback != IntPtr.Zero || command.TextureId != new IntPtr(1)) {
                        throw new NotSupportedException("The debug renderer supports font-atlas widgets without callbacks.");
                    }
                    var clip = command.ClipRect;
                    var left = Math.Clamp((int)(clip.X - data.DisplayPos.X), 0, _device.Viewport.Width);
                    var top = Math.Clamp((int)(clip.Y - data.DisplayPos.Y), 0, _device.Viewport.Height);
                    var right = Math.Clamp((int)(clip.Z - data.DisplayPos.X), 0, _device.Viewport.Width);
                    var bottom = Math.Clamp((int)(clip.W - data.DisplayPos.Y), 0, _device.Viewport.Height);
                    if (right <= left || bottom <= top) continue;
                    _device.ScissorRectangle = new Rectangle(left, top, right - left, bottom - top);
                    _effect.CurrentTechnique.Passes[0].Apply();
                    _device.DrawIndexedPrimitives(PrimitiveType.TriangleList,
                        vertexOffset + (int)command.VtxOffset,
                        indexOffset + (int)command.IdxOffset, (int)command.ElemCount / 3);
                }
                vertexOffset += list.VtxBuffer.Size;
                indexOffset += list.IdxBuffer.Size;
            }
        }
        finally {
            _device.BlendState = blend;
            _device.DepthStencilState = depth;
            _device.RasterizerState = rasterizer;
            _device.ScissorRectangle = scissor;
            _device.SamplerStates[0] = sampler;
            _device.Textures[0] = texture;
            // UI is the final pass; the scene binds its own vertices on the next Draw.
            _device.SetVertexBuffer(null);
            _device.Indices = indices;
        }
    }

    public void Dispose() {
        _game.Window.TextInput -= OnTextInput;
        _vertices?.Dispose();
        _indices?.Dispose();
        _font.Dispose();
        _effect.Dispose();
        _rasterizer.Dispose();
        _vertexDeclaration.Dispose();
        ImGui.DestroyContext(_context);
    }
}
