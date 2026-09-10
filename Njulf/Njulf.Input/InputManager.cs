using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Silk.NET.Input;

namespace Njulf.Input
{
    /// <summary>Silk-backed game-thread input service. The host owns its lifetime.</summary>
    public sealed partial class InputManager : IInputManager, IDisposable
    {
        private readonly IInputContext _inputContext;
        private readonly Dictionary<string, InputActionState> _actions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InputActionContext> _contexts = new(StringComparer.Ordinal);
        private InputActionContext? _activeContext;
        private bool _focused = true;
        private InputRebindSession? _rebind;
        private readonly List<IKeyboard> _keyboards = new List<IKeyboard>();
        private readonly List<IMouse> _mice = new List<IMouse>();
        private readonly List<IJoystick> _joysticks = new List<IJoystick>();

        private Vector2 _mousePosition;
        private Vector2 _mouseDelta;
        private float _mouseScrollDelta;
        private bool _isInitialized;
        private bool _disposed, _updating;
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private float _pendingScroll;
        private uint _mouseMask, _previousMouseMask;
        private static readonly MouseButton[] MouseButtons = Enum.GetValues<MouseButton>().Where(b => (int)b >= 0).ToArray();
        internal void EnsureUsable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Environment.CurrentManagedThreadId != _threadId)
                throw new InvalidOperationException("Input operations require the game thread.");
        }
        internal void NotifyPressed(InputAction action) => ActionPressed?.Invoke(action);
        internal void NotifyReleased(InputAction action) => ActionReleased?.Invoke(action);

        /// <inheritdoc />
        public event System.Action<InputAction>? ActionPressed;
        /// <inheritdoc />
        public event System.Action<InputAction>? ActionReleased;
        /// <summary>Raw input events for optional UI integrations. Game actions remain unchanged.</summary>
        public event System.Action<Key, char>? RawKeyDown;
        /// <summary>Native key release for UI integrations on the game thread.</summary>
        public event System.Action<Key>? RawKeyUp;
        /// <summary>Text characters delivered by the platform on the game thread.</summary>
        public event System.Action<char>? RawTextInput;
        /// <inheritdoc />
        public event System.Action<char>? TextInput;
        /// <summary>Native button code and pressed state for UI integrations.</summary>
        public event System.Action<int, bool>? RawMouseButtonChanged;
        /// <summary>Client-area cursor position in pixels, using Njulf math.</summary>
        public event System.Action<Vector2>? RawMouseMoved;
        /// <summary>Horizontal and vertical platform scroll increments.</summary>
        public event System.Action<Vector2>? RawMouseScrolled;

        /// <inheritdoc />
        public Vector2 MousePosition { get { EnsureUsable(); return _mousePosition; } }
        /// <inheritdoc />
        public Vector2 MouseDelta { get { EnsureUsable(); return _mouseDelta; } }
        /// <inheritdoc />
        public float MouseScrollDelta { get { EnsureUsable(); return _mouseScrollDelta; } }

        /// <summary>Creates a service on the game thread, borrowing a native context using Silk 2.23 GLFW axis conventions.</summary>
        public InputManager(IInputContext inputContext)
        {
            _inputContext = inputContext ?? throw new ArgumentNullException(nameof(inputContext));
        }

        /// <summary>Subscribes to available devices once. Update initializes lazily if needed.</summary>
        public void Initialize()
        {
            EnsureUsable();
            if (_isInitialized)
                return;

            foreach (var keyboard in _inputContext.Keyboards) ConnectDevice(keyboard);
            foreach (var mouse in _inputContext.Mice) ConnectDevice(mouse);
            foreach (var joystick in _inputContext.Joysticks) ConnectDevice(joystick);
            foreach (var gamepad in _inputContext.Gamepads) ConnectDevice(gamepad);
            _inputContext.ConnectionChanged += OnConnectionChanged;
            _isInitialized = true;
        }

        /// <inheritdoc />
        public InputAction CreateAction(string name) => CreateAction(name, null);
        /// <inheritdoc />
        public InputAction CreateAction(string name, InputActionContext? context)
        {
            ValidateRegistration(name, context, InputValueMode.State);
            var action = new InputAction(name, this, context);
            _actions.Add(name, action.State);
            return action;
        }
        /// <inheritdoc />
        public InputFloatAction CreateFloatAction(string name, InputActionContext? context = null, InputValueMode mode = InputValueMode.State)
        {
            ValidateRegistration(name, context, mode);
            var action = new InputFloatAction(name, this, context, mode);
            _actions.Add(name, action.State); return action;
        }
        /// <inheritdoc />
        public InputVector2Action CreateVector2Action(string name, InputActionContext? context = null, InputValueMode mode = InputValueMode.State)
        {
            ValidateRegistration(name, context, mode);
            var action = new InputVector2Action(name, this, context, mode);
            _actions.Add(name, action.State); return action;
        }
        private void ValidateRegistration(string name, InputActionContext? context, InputValueMode mode)
        {
            EnsureUsable();
            if (_updating) throw new InvalidOperationException("Create actions outside input callbacks.");
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_actions.ContainsKey(name)) throw new ArgumentException($"Action '{name}' already exists.", nameof(name));
            ValidateContext(context);
            BindingSpec.CodeOf(mode);
        }
        private void ValidateContext(InputActionContext? context)
        {
            if (context != null && context.Owner != this) throw new ArgumentException("The context belongs to another input manager.", nameof(context));
        }
        /// <inheritdoc />
        public InputActionContext CreateContext(string name)
        {
            EnsureUsable(); ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var context = new InputActionContext(name, this);
            _contexts.Add(name, context); return context;
        }
        /// <inheritdoc />
        public InputActionContext? ActiveContext
        {
            get { EnsureUsable(); return _activeContext; }
            set { EnsureUsable(); ValidateContext(value); _activeContext = value; }
        }
        private object? FindAction(string name)
        { EnsureUsable(); ArgumentNullException.ThrowIfNull(name); return _actions.GetValueOrDefault(name)?.ActionObject; }
        /// <inheritdoc />
        public InputAction? GetAction(string name)
        => FindAction(name) as InputAction;
        /// <inheritdoc />
        public InputFloatAction? GetFloatAction(string name) => FindAction(name) as InputFloatAction;
        /// <inheritdoc />
        public InputVector2Action? GetVector2Action(string name) => FindAction(name) as InputVector2Action;
        private uint MouseBit(MouseButton button)
        {
            EnsureUsable();
            if ((int)button < 0 || !Enum.IsDefined(button)) throw new ArgumentOutOfRangeException(nameof(button));
            return 1u << (int)button;
        }
        /// <inheritdoc />
        public bool IsMouseButtonDown(MouseButton button) => (_mouseMask & MouseBit(button)) != 0;
        /// <inheritdoc />
        public bool IsMouseButtonPressed(MouseButton button) => ((_mouseMask & ~_previousMouseMask) & MouseBit(button)) != 0;
        /// <inheritdoc />
        public bool IsMouseButtonReleased(MouseButton button) => ((~_mouseMask & _previousMouseMask) & MouseBit(button)) != 0;
        /// <summary>Polls a native key directly for UI integration; unavailable devices return false.</summary>
        public bool IsPhysicalKeyDown(Key key, int keyboardIndex = 0)
        {
            EnsureUsable();
            if (keyboardIndex < 0 || keyboardIndex >= _keyboards.Count)
                return false;

            IKeyboard keyboard = _keyboards[keyboardIndex];
            return _connected.Contains(keyboard) && keyboard.IsKeyPressed(key);
        }

        /// <inheritdoc />
        public void Update()
        {
            EnsureUsable();
            if (_updating) throw new InvalidOperationException("Input updates cannot be nested.");
            if (!_isInitialized) Initialize();
            _updating = true;
            try
            {
                PublishMouseDeltas();
                _mouseScrollDelta = _focused ? _pendingScroll : 0;
                _pendingScroll = 0;
                _previousMouseMask = _mouseMask;
                _mouseMask = 0;
                foreach (var mouse in _mice)
                    foreach (var button in MouseButtons)
                        if (_focused && _connected.Contains(mouse) && mouse.IsButtonPressed((Silk.NET.Input.MouseButton)button)) _mouseMask |= 1u << (int)button;
                bool enabled = _focused && _rebind == null;
                var context = _activeContext;
                _rebind?.Sample();
                foreach (var action in _actions.Values) action.Sample(enabled && (action.Context == null || action.Context == context));
                foreach (var action in _actions.Values) action.Notify?.Invoke();
            }
            finally { _updating = false; }
        }
        /// <inheritdoc />
        public Vector2 ConsumeMouseDelta()
        {
            EnsureUsable();
            Vector2 delta = _mouseDelta;
            _mouseDelta = Vector2.Zero;
            return delta;
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
        {
            RawKeyDown?.Invoke(key, arg3 is >= char.MinValue and <= char.MaxValue ? (char)arg3 : '\0');
        }
        private void OnKeyUp(IKeyboard keyboard, Key key, int arg3) => RawKeyUp?.Invoke(key);
        private void OnKeyChar(IKeyboard keyboard, char character)
        {
            RawTextInput?.Invoke(character);
            if (_focused && _rebind == null) TextInput?.Invoke(character);
        }
        private void OnMouseDown(IMouse mouse, Silk.NET.Input.MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, true);
        private void OnMouseUp(IMouse mouse, Silk.NET.Input.MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, false);

        private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
        {
            var newPosition = new Vector2(position.X, position.Y);
            var motion = _mouseMotion[mouse];
            if (_focused && motion.HasPosition)
            {
                Vector2 delta = newPosition - motion.LastPosition;
                _mouseDelta += delta;
                motion.Pending += delta;
            }
            else
            {
                motion.HasPosition = _focused;
            }

            _mousePosition = newPosition;
            motion.LastPosition = newPosition;
            RawMouseMoved?.Invoke(newPosition);
        }

        private void OnMouseWheel(IMouse mouse, ScrollWheel scrollWheel)
        {
            if (_focused)
            {
                _pendingScroll += scrollWheel.Y;
                _mouseMotion[mouse].PendingWheel += new Vector2(scrollWheel.X, scrollWheel.Y);
            }
            RawMouseScrolled?.Invoke(new Vector2(scrollWheel.X, scrollWheel.Y));
        }

        /// <summary>Unsubscribes devices and invalidates owned actions on the game thread. Does not dispose the borrowed context.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            EnsureUsable();
            if (_updating) throw new InvalidOperationException("Dispose input outside input callbacks.");
            _rebind?.Cancel();
            RestoreDeviceSettings();
            _inputContext.ConnectionChanged -= OnConnectionChanged;
            _disposed = true;
            ActionPressed = null;
            ActionReleased = null;
            RawKeyDown = null;
            RawKeyUp = null;
            RawTextInput = null;
            TextInput = null;
            RawMouseButtonChanged = null;
            RawMouseMoved = null;
            RawMouseScrolled = null;

            foreach (var action in _actions.Values)
            {
                action.Invalidate();
            }
            _actions.Clear();

            foreach (IKeyboard keyboard in _keyboards)
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
            }
            foreach (IMouse mouse in _mice)
            {
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.MouseMove -= OnMouseMove;
                mouse.Scroll -= OnMouseWheel;
            }
            _keyboards.Clear();
            _mice.Clear();
            _joysticks.Clear();
            _gamepads.Clear(); _connected.Clear(); _mouseMotion.Clear(); _contexts.Clear();
        }

        /// <summary>Sets the native cursor mode for all mice on the game thread.</summary>
        public void SetCursorMode(Silk.NET.Input.CursorMode mode)
        {
            EnsureUsable();
            if (!_isInitialized) Initialize();
            foreach (IMouse mouse in _mice)
                if (_connected.Contains(mouse)) mouse.Cursor.CursorMode = mode;
            _cursorMode = mode == Silk.NET.Input.CursorMode.Normal ? InputCursorMode.Normal
                : mode == Silk.NET.Input.CursorMode.Hidden ? InputCursorMode.Hidden : InputCursorMode.Captured;
            ClearMotion();
        }
    }
}
