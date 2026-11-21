using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MonoGame.AgentTools.Runtime;

namespace MonoGame.AgentTools.Input;

/// <summary>
/// Feeds scripted input to an unmodified DesktopGL game through MonoGame's own input path, and keeps the
/// machine's real input out of captures. MonoGame reaches SDL through delegates in mutable static fields;
/// the mouse and lock-key ones are swapped for the run, the keyboard's key list is written directly with
/// <c>Window.KeyDown</c>/<c>KeyUp</c> raised as <c>SdlGamePlatform</c> raises them, and gamepads are SDL
/// virtual game controllers registered with <c>GamePad</c>, so <c>Keyboard</c>, <c>Mouse</c> and
/// <c>GamePad.GetState</c> work unchanged. Experimental: it depends on MonoGame internals, bound once
/// here; a MonoGame version that renames one fails up front with its name. One per process.
/// </summary>
internal sealed unsafe class InputInjector : IInputSink, IDisposable
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int WheelDelta = 120;

    // SDL_GameControllerButton and SDL_GameControllerAxis values.
    private const int SdlButtonCount = 15;
    private const int SdlAxisLeftX = 0, SdlAxisLeftY = 1, SdlAxisRightX = 2, SdlAxisRightY = 3, SdlAxisTriggerLeft = 4, SdlAxisTriggerRight = 5;
    private static readonly (Buttons Button, int Sdl)[] PadButtonMap =
    [
        (Buttons.A, 0), (Buttons.B, 1), (Buttons.X, 2), (Buttons.Y, 3), (Buttons.Back, 4), (Buttons.BigButton, 5),
        (Buttons.Start, 6), (Buttons.LeftStick, 7), (Buttons.RightStick, 8), (Buttons.LeftShoulder, 9),
        (Buttons.RightShoulder, 10), (Buttons.DPadUp, 11), (Buttons.DPadDown, 12), (Buttons.DPadLeft, 13), (Buttons.DPadRight, 14),
    ];

    // Read by the swapped-in SDL delegates, which are static.
    private static GameWindow? s_window;
    private static int s_mouseX, s_mouseY, s_mouseButtons;
    private static int s_left, s_middle, s_right, s_x1, s_x2;

    private readonly Game _game;
    private readonly List<(FieldInfo Field, object? Original)> _swapped = [];
    private readonly (FieldInfo Field, Delegate Replacement)[] _replacements;
    private readonly List<Keys> _keyList;
    private readonly bool[] _keyDown = new bool[256];
    private readonly Action<InputKeyEventArgs> _onKeyDown;
    private readonly Action<InputKeyEventArgs> _onKeyUp;
    private readonly Action<TextInputEventArgs> _onTextInput;
    private readonly Action<int> _setScrollX;
    private readonly Action<int> _setScrollY;
    private readonly IDictionary _gamepads;
    private readonly Action<int> _addGamepad;
    private readonly Action _refreshGamepads;
    private readonly FieldInfo _gamepadDevice;
    private readonly IntPtr _sdl;
    private readonly Pad[] _pads = new Pad[InputControl.MaxPads];
    private readonly List<DictionaryEntry> _realGamepads = [];
    private int _padCount;
    private int _scrollX, _scrollY;
    private IntPtr _padName;

    private delegate* unmanaged[Cdecl]<VirtualJoystickDesc*, int> _attachVirtual;
    private delegate* unmanaged[Cdecl]<int, int> _detachVirtual;
    private delegate* unmanaged[Cdecl]<IntPtr, int, short, int> _setVirtualAxis;
    private delegate* unmanaged[Cdecl]<IntPtr, int, byte, int> _setVirtualButton;
    private delegate* unmanaged[Cdecl]<void> _joystickUpdate;
    private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _controllerGetJoystick;
    private delegate* unmanaged[Cdecl]<IntPtr, void> _controllerClose;
    private delegate* unmanaged[Cdecl]<IntPtr> _getError;

    private struct Pad
    {
        public IntPtr Controller;
        public IntPtr Joystick;
        public int DeviceIndex;
        public int Buttons;
        public float LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
        public bool Dirty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualJoystickDesc
    {
        public ushort Version, Type, Axes, ButtonCount, Hats, VendorId, ProductId, Padding;
        public uint ButtonMask, AxisMask;
        public IntPtr Name, UserData, Update, SetPlayerIndex, Rumble, RumbleTriggers, SetLed, SendEffect;
    }

    private InputInjector(Game game)
    {
        _game = game;
        var assembly = typeof(Game).Assembly;
        var sdl = Type(assembly, "Sdl");
        var sdlMouse = Type(assembly, "Sdl+Mouse");
        var sdlKeyboard = Type(assembly, "Sdl+Keyboard");

        var mouseButton = Type(assembly, "Sdl+Mouse+Button");
        s_left = Flag(mouseButton, "Left");
        s_middle = Flag(mouseButton, "Middle");
        s_right = Flag(mouseButton, "Right");
        s_x1 = Flag(mouseButton, "X1Mask");
        s_x2 = Flag(mouseButton, "X2Mask");

        _replacements =
        [
            Replace(Field(sdlMouse, "GetGlobalState"), nameof(GlobalMouseState)),
            Replace(Field(sdlMouse, "GetState"), nameof(WindowMouseState)),
            Replace(Field(sdlMouse, "WarpInWindow"), nameof(WarpMouse)),
            Replace(Field(sdlKeyboard, "GetModState"), nameof(NoModifiers)),
        ];

        _keyList = Field(typeof(Keyboard), "_keys").GetValue(null) as List<Keys>
            ?? throw GameHooks.Incompatible("Keyboard._keys");
        var window = game.Window;
        _onKeyDown = Method<Action<InputKeyEventArgs>>(typeof(GameWindow), "OnKeyDown", window, typeof(InputKeyEventArgs));
        _onKeyUp = Method<Action<InputKeyEventArgs>>(typeof(GameWindow), "OnKeyUp", window, typeof(InputKeyEventArgs));
        _onTextInput = Method<Action<TextInputEventArgs>>(typeof(GameWindow), "OnTextInput", window, typeof(TextInputEventArgs));
        _setScrollX = StaticSetter(Field(typeof(Mouse), "ScrollX"));
        _setScrollY = StaticSetter(Field(typeof(Mouse), "ScrollY"));

        _gamepads = Field(typeof(GamePad), "Gamepads").GetValue(null) as IDictionary ?? throw GameHooks.Incompatible("GamePad.Gamepads");
        _addGamepad = StaticMethod<Action<int>>(typeof(GamePad), "AddDevice", typeof(int));
        _refreshGamepads = StaticMethod<Action>(typeof(GamePad), "RefreshTranslationTable");
        _gamepadDevice = Field(Type(assembly, "Microsoft.Xna.Framework.Input.GamePad+GamePadInfo"), "Device", Instance);
        _sdl = (IntPtr)(Field(sdl, "NativeLibrary").GetValue(null) ?? IntPtr.Zero);
        if (_sdl == IntPtr.Zero)
            throw GameHooks.Incompatible("Sdl.NativeLibrary");
    }

    /// <summary>Binds every MonoGame member this needs, without changing anything yet.</summary>
    public static InputInjector Bind(Game game) => new(game);

    /// <summary>Swaps the mouse and lock-key bindings. Call before the game initializes.</summary>
    public void Install()
    {
        s_window = _game.Window;
        s_mouseX = s_mouseY = s_mouseButtons = 0;
        foreach (var (field, replacement) in _replacements)
        {
            _swapped.Add((field, field.GetValue(null)));
            field.SetValue(null, replacement);
        }
    }

    /// <summary>
    /// After the game initializes (MonoGame reads SDL's events once then, so keys held and gamepads
    /// connected at startup register): empties the keyboard, hides real gamepads and connects a virtual
    /// one for each pad index up to the highest in <paramref name="padMask"/>.
    /// </summary>
    public void Isolate(int padMask)
    {
        _keyList.Clear();
        _keyList.Capacity = Math.Max(_keyList.Capacity, 256);
        _setScrollX(0);
        _setScrollY(0);

        foreach (DictionaryEntry entry in _gamepads)
            _realGamepads.Add(entry);
        _gamepads.Clear();
        _padCount = padMask == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)padMask);
        if (_padCount > 0)
            ConnectPads();
        _refreshGamepads();
    }

    private void ConnectPads()
    {
        _attachVirtual = (delegate* unmanaged[Cdecl]<VirtualJoystickDesc*, int>)Export("SDL_JoystickAttachVirtualEx");
        _detachVirtual = (delegate* unmanaged[Cdecl]<int, int>)Export("SDL_JoystickDetachVirtual");
        _setVirtualAxis = (delegate* unmanaged[Cdecl]<IntPtr, int, short, int>)Export("SDL_JoystickSetVirtualAxis");
        _setVirtualButton = (delegate* unmanaged[Cdecl]<IntPtr, int, byte, int>)Export("SDL_JoystickSetVirtualButton");
        _joystickUpdate = (delegate* unmanaged[Cdecl]<void>)Export("SDL_JoystickUpdate");
        _controllerGetJoystick = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export("SDL_GameControllerGetJoystick");
        _controllerClose = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("SDL_GameControllerClose");
        _getError = (delegate* unmanaged[Cdecl]<IntPtr>)Export("SDL_GetError");

        _padName = Marshal.StringToHGlobalAnsi("MonoGame.AgentTools virtual gamepad");
        for (int i = 0; i < _padCount; ++i)
        {
            var desc = new VirtualJoystickDesc
            {
                Version = 1,          // SDL_VIRTUAL_JOYSTICK_DESC_VERSION
                Type = 1,             // SDL_JOYSTICK_TYPE_GAMECONTROLLER
                Axes = 6,
                ButtonCount = SdlButtonCount,
                ButtonMask = (1u << SdlButtonCount) - 1,
                AxisMask = (1u << 6) - 1,
                Name = _padName,
            };
            var device = _attachVirtual(&desc);
            if (device < 0)
                throw new InvalidOperationException($"SDL couldn't create a virtual gamepad: {SdlError()}");
            _addGamepad(device);
            var info = _gamepads[i] ?? throw new InvalidOperationException("MonoGame didn't register the virtual gamepad.");
            var controller = (IntPtr)_gamepadDevice.GetValue(info)!;
            if (controller == IntPtr.Zero)
                throw new InvalidOperationException($"SDL couldn't open the virtual gamepad as a game controller: {SdlError()}");
            _pads[i] = new Pad { Controller = controller, Joystick = _controllerGetJoystick(controller), DeviceIndex = device, Dirty = true };
        }
        EndFrame();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(in ScriptEvent e)
    {
        var control = e.Control;
        switch (control.Kind)
        {
            case ControlKind.Key:
                SetKey((Keys)control.Code, e.Action == InputAction.Down);
                break;
            case ControlKind.MouseButton:
                if (e.HasPosition)
                    (s_mouseX, s_mouseY) = ((int)e.X, (int)e.Y);
                var bit = (MouseButton)control.Code switch
                {
                    MouseButton.Left => s_left,
                    MouseButton.Right => s_right,
                    MouseButton.Middle => s_middle,
                    MouseButton.X1 => s_x1,
                    _ => s_x2,
                };
                s_mouseButtons = e.Action == InputAction.Down ? s_mouseButtons | bit : s_mouseButtons & ~bit;
                break;
            case ControlKind.MousePosition:
                (s_mouseX, s_mouseY) = ((int)e.X, (int)e.Y);
                break;
            case ControlKind.MouseWheel:
                _setScrollY(_scrollY += (int)Math.Round(e.X * WheelDelta));
                break;
            case ControlKind.MouseWheelHorizontal:
                _setScrollX(_scrollX += (int)Math.Round(e.X * WheelDelta));
                break;
            case ControlKind.PadButton:
                ref var pad = ref _pads[control.Pad];
                pad.Buttons = e.Action == InputAction.Down ? pad.Buttons | control.Code : pad.Buttons & ~control.Code;
                pad.Dirty = true;
                break;
            case ControlKind.PadStick:
                ref var stick = ref _pads[control.Pad];
                if ((PadSide)control.Code == PadSide.Left)
                    (stick.LeftX, stick.LeftY) = ((float)e.X, (float)e.Y);
                else
                    (stick.RightX, stick.RightY) = ((float)e.X, (float)e.Y);
                stick.Dirty = true;
                break;
            case ControlKind.PadTrigger:
                ref var trigger = ref _pads[control.Pad];
                if ((PadSide)control.Code == PadSide.Left)
                    trigger.LeftTrigger = (float)e.X;
                else
                    trigger.RightTrigger = (float)e.X;
                trigger.Dirty = true;
                break;
        }
    }

    /// <summary>Sends this frame's gamepad changes to SDL. Call after the frame's events, before <c>Update</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EndFrame()
    {
        if (_padCount == 0)
            return;
        for (int i = 0; i < _padCount; ++i)
        {
            ref var pad = ref _pads[i];
            if (!pad.Dirty)
                continue;
            pad.Dirty = false;
            foreach (var (button, sdlButton) in PadButtonMap)
                _setVirtualButton(pad.Joystick, sdlButton, (pad.Buttons & (int)button) != 0 ? (byte)1 : (byte)0);
            _setVirtualAxis(pad.Joystick, SdlAxisLeftX, Stick(pad.LeftX));
            _setVirtualAxis(pad.Joystick, SdlAxisLeftY, Stick(-pad.LeftY));   // SDL's Y points down; MonoGame flips it
            _setVirtualAxis(pad.Joystick, SdlAxisRightX, Stick(pad.RightX));
            _setVirtualAxis(pad.Joystick, SdlAxisRightY, Stick(-pad.RightY));
            _setVirtualAxis(pad.Joystick, SdlAxisTriggerLeft, Trigger(pad.LeftTrigger));
            _setVirtualAxis(pad.Joystick, SdlAxisTriggerRight, Trigger(pad.RightTrigger));
        }
        _joystickUpdate();
    }

    /// <summary>The inverse of MonoGame's <c>GetFromSdlAxis</c>: negative values over 32768, positive over 32767.</summary>
    internal static short Stick(float value) =>
        (short)Math.Clamp(Math.Round(value < 0 ? value * 32768.0 : value * 32767.0), short.MinValue, short.MaxValue);

    /// <summary>SDL maps a virtual trigger axis's full range onto the controller's 0 to 32767.</summary>
    internal static short Trigger(float value) =>
        (short)Math.Clamp(Math.Round(value * 65535.0 - 32768.0), short.MinValue, short.MaxValue);

    private void SetKey(Keys key, bool down)
    {
        var index = (int)key;
        if (_keyDown[index] == down)
            return;
        _keyDown[index] = down;
        if (down)
        {
            _keyList.Add(key);
            _onKeyDown(new InputKeyEventArgs(key));
            // SdlGamePlatform also raises TextInput for keys whose SDL keycode is a control character.
            var control = key switch { Keys.Back => '\b', Keys.Tab => '\t', Keys.Enter => '\r', Keys.Escape => '\u001b', Keys.Delete => '\u007f', _ => '\0' };
            if (control != '\0')
                _onTextInput(new TextInputEventArgs(control, key));
        }
        else
        {
            _keyList.Remove(key);
            _onKeyUp(new InputKeyEventArgs(key));
        }
    }

    public void Dispose()
    {
        foreach (var (field, original) in _swapped)
            field.SetValue(null, original);
        _swapped.Clear();
        if (_padCount > 0)
        {
            _gamepads.Clear();
            for (int i = 0; i < _padCount; ++i)
            {
                _controllerClose(_pads[i].Controller);
                _detachVirtual(_pads[i].DeviceIndex);
            }
            _padCount = 0;
        }
        foreach (var entry in _realGamepads)
            _gamepads[entry.Key] = entry.Value;
        _realGamepads.Clear();
        _refreshGamepads();
        if (_padName != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_padName);
            _padName = IntPtr.Zero;
        }
        s_window = null;
    }

    // The swapped-in SDL bindings. MonoGame subtracts the window's position from the "global" position.
    private static int GlobalMouseState(out int x, out int y)
    {
        var origin = s_window?.ClientBounds.Location ?? Point.Zero;
        x = s_mouseX + origin.X;
        y = s_mouseY + origin.Y;
        return s_mouseButtons;
    }

    private static int WindowMouseState(out int x, out int y)
    {
        x = s_mouseX;
        y = s_mouseY;
        return s_mouseButtons;
    }

    /// <summary><c>Mouse.SetPosition</c> moves the scripted cursor, never the user's.</summary>
    private static void WarpMouse(IntPtr window, int x, int y) => (s_mouseX, s_mouseY) = (x, y);

    private static ushort NoModifiers() => 0;

    private string SdlError() => _getError == null ? "" : Marshal.PtrToStringUTF8(_getError()) ?? "";

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_sdl, name, out var address) ? address
        : throw new PlatformNotSupportedException($"The SDL library MonoGame loaded has no {name} (SDL 2.24 or later is needed for virtual gamepads).");

    private static Type Type(Assembly assembly, string name) => assembly.GetType(name) ?? throw GameHooks.Incompatible(name);

    private static FieldInfo Field(Type type, string name, BindingFlags flags = Static) =>
        type.GetField(name, flags) ?? throw GameHooks.Incompatible($"{type.Name}.{name}");

    private static int Flag(Type enumType, string name) =>
        Enum.TryParse(enumType, name, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : throw GameHooks.Incompatible($"{enumType.Name}.{name}");

    private static T Method<T>(Type type, string name, object target, params Type[] parameters) where T : Delegate =>
        (type.GetMethod(name, Instance, binder: null, parameters, modifiers: null) ?? throw GameHooks.Incompatible($"{type.Name}.{name}"))
            .CreateDelegate<T>(target);

    private static T StaticMethod<T>(Type type, string name, params Type[] parameters) where T : Delegate =>
        (type.GetMethod(name, Static, binder: null, parameters, modifiers: null) ?? throw GameHooks.Incompatible($"{type.Name}.{name}"))
            .CreateDelegate<T>();

    /// <summary>
    /// A delegate of the field's own (internal) type that calls one of the methods above. The return types
    /// differ only as enum and underlying integer, which IL treats alike; built once, so nothing per call.
    /// </summary>
    private static (FieldInfo, Delegate) Replace(FieldInfo field, string method)
    {
        var target = typeof(InputInjector).GetMethod(method, Static)!;
        var invoke = field.FieldType.GetMethod("Invoke") ?? throw GameHooks.Incompatible(field.Name);
        var parameters = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
        var returnType = invoke.ReturnType.IsEnum ? Enum.GetUnderlyingType(invoke.ReturnType) : invoke.ReturnType;
        if (!parameters.SequenceEqual(target.GetParameters().Select(p => p.ParameterType)) || returnType != target.ReturnType)
            throw GameHooks.Incompatible($"{field.DeclaringType?.Name}.{field.Name}'s signature");
        var adapter = new DynamicMethod(method + "Adapter", invoke.ReturnType, parameters, typeof(InputInjector).Module, skipVisibility: true);
        var il = adapter.GetILGenerator();
        for (short i = 0; i < parameters.Length; ++i)
            il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return (field, adapter.CreateDelegate(field.FieldType));
    }

    private static Action<int> StaticSetter(FieldInfo field)
    {
        var setter = new DynamicMethod(field.Name + "Setter", null, [typeof(int)], typeof(InputInjector).Module, skipVisibility: true);
        var il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        return setter.CreateDelegate<Action<int>>();
    }
}
