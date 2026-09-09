using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace monogame;

internal enum InputFocus { Scene, UI }
internal enum MouseButton { Left, Right, Middle, X1, X2 }

internal static class InputManager {
    private struct InputRecord {
        internal bool? State;
        internal InputFocus? PressFocus;
    }

    private static readonly InputRecord[] s_keyboard = new InputRecord[256];
    private static readonly InputRecord[] s_mouse = new InputRecord[5];
    private static Game s_game;
    private static Point s_previousPosition;
    private static int s_previousScroll;
    private static bool s_previousActive;
    private static KeyboardState s_keyboardState;
    private static MouseState s_mouseState;

    internal static InputFocus Focus { get; set; } = InputFocus.Scene;
    internal static Point MousePosition => s_mouseState.Position;
    internal static Point MouseDelta { get; private set; }
    internal static int ScrollDelta { get; private set; }

    internal static void Initialize(Game game) {
        s_game = game;
        game.Deactivated += OnDeactivated;
        OnDeactivated(game, EventArgs.Empty);
    }

    internal static void Dispose() {
        s_game.Deactivated -= OnDeactivated;
        OnDeactivated(s_game, EventArgs.Empty);
        s_game = null;
    }

    private static void OnDeactivated(object sender, EventArgs args) {
        s_previousActive = false;
        Array.Clear(s_keyboard);
        Array.Clear(s_mouse);
        MouseDelta = Point.Zero;
        ScrollDelta = 0;
    }

    internal static void BeforeUpdate() {
        s_keyboardState = Keyboard.GetState();
        s_mouseState = Mouse.GetState();
        MouseDelta = s_game.IsActive && s_previousActive ? MousePosition - s_previousPosition : Point.Zero;
        ScrollDelta = s_game.IsActive && s_previousActive ? s_mouseState.ScrollWheelValue - s_previousScroll : 0;
        if (!s_game.IsActive) {
            Array.Clear(s_keyboard);
            Array.Clear(s_mouse);
            return;
        }
        UpdateMouse();
        UpdateKeyboard();
    }

    private static void UpdateMouse() {
        for (var i = 0; i < s_mouse.Length; i++) {
            var down = ((MouseButton)i switch {
                MouseButton.Left => s_mouseState.LeftButton,
                MouseButton.Right => s_mouseState.RightButton,
                MouseButton.Middle => s_mouseState.MiddleButton,
                MouseButton.X1 => s_mouseState.XButton1,
                _ => s_mouseState.XButton2,
            }) == ButtonState.Pressed;
            if (down && s_mouse[i].State is null) {
                s_mouse[i].State = true;
            }
            else if (!down && s_mouse[i].State == true) {
                s_mouse[i].State = false;
            }
        }
    }

    private static void UpdateKeyboard() {
        for (var i = 0; i < s_keyboard.Length; i++) {
            var down = s_keyboardState.IsKeyDown((Keys)i);
            if (down && s_keyboard[i].State is null) {
                s_keyboard[i].State = true;
            }
            else if (!down && s_keyboard[i].State == true) {
                s_keyboard[i].State = false;
            }
        }
    }

    internal static bool IsDown(Keys key, InputFocus? focus = null) {
        var state = s_keyboard[(int)key];
        return state.State == true && (focus is null || Focus == focus);
    }

    internal static bool IsDown(MouseButton button, InputFocus? focus = null) {
        var state = s_mouse[(int)button];
        return state.State == true && (focus is null || Focus == focus);
    }

    internal static bool IsClick(Keys key, InputFocus? focus = null) {
        var state = s_keyboard[(int)key];
        return state.State == false && (focus is null || Focus == focus);
    }

    internal static bool IsClick(MouseButton button, InputFocus? focus = null) {
        var state = s_mouse[(int)button];
        return state.State == false && (focus is null || Focus == focus);
    }

    internal static void AfterUpdate() {
        for (var i = 0; i < s_keyboard.Length; i++) {
            if (s_keyboard[i].State == true) s_keyboard[i].PressFocus ??= Focus;
            if (s_keyboard[i].State == false) s_keyboard[i] = default;
        }
        for (var i = 0; i < s_mouse.Length; i++) {
            if (s_mouse[i].State == true) s_mouse[i].PressFocus ??= Focus;
            if (s_mouse[i].State == false) s_mouse[i] = default;
        }
        s_previousPosition = MousePosition;
        s_previousScroll = s_mouseState.ScrollWheelValue;
        s_previousActive = s_game.IsActive;
    }
}
