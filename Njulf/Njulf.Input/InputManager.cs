using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Silk.NET.Input;

namespace Njulf.Input
{
    /// <summary>Silk-backed game-thread input service. The host owns its lifetime.</summary>
    public sealed class InputManager : IInputManager, IDisposable
    {
        private readonly IInputContext _inputContext;
        private readonly Dictionary<string, Njulf.Input.InputAction> _actions = new Dictionary<string, Njulf.Input.InputAction>();
        private readonly List<IKeyboard> _keyboards = new List<IKeyboard>();
        private readonly List<IMouse> _mice = new List<IMouse>();
        private readonly List<IJoystick> _joysticks = new List<IJoystick>();

        private Vector2 _mousePosition;
        private Vector2 _mouseDelta;
        private Vector2 _lastMousePosition;
        private float _mouseScrollDelta;
        private bool _hasLastMousePosition;
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
        internal bool IsActive(InputBinding binding) => binding.IsActive(_keyboards, _mice, _joysticks);
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

        /// <summary>Creates a service on the game thread, borrowing the supplied native context.</summary>
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

            _keyboards.Clear();
            _mice.Clear();
            _joysticks.Clear();

            foreach (var keyboard in _inputContext.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
                _keyboards.Add(keyboard);
            }

            foreach (var mouse in _inputContext.Mice)
            {
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMouseMove;
                mouse.Scroll += OnMouseWheel;
                _mice.Add(mouse);
            }

            foreach (var joystick in _inputContext.Joysticks)
            {
                _joysticks.Add(joystick);
            }

            _isInitialized = true;
        }

        /// <inheritdoc />
        public InputAction CreateAction(string name)
        {
            EnsureUsable();
            if (_updating) throw new InvalidOperationException("Create actions outside input callbacks.");
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_actions.ContainsKey(name)) throw new ArgumentException($"Action '{name}' already exists.", nameof(name));
            var action = new InputAction(name, this);
            _actions.Add(name, action);
            return action;
        }
        /// <inheritdoc />
        public InputAction? GetAction(string name)
        { EnsureUsable(); ArgumentNullException.ThrowIfNull(name); return _actions.GetValueOrDefault(name); }
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
            return keyboard != null && keyboard.IsKeyPressed(key);
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
                _mouseScrollDelta = _pendingScroll;
                _pendingScroll = 0;
                _previousMouseMask = _mouseMask;
                _mouseMask = 0;
                foreach (var mouse in _mice)
                    foreach (var button in MouseButtons)
                        if (mouse.IsButtonPressed((Silk.NET.Input.MouseButton)button)) _mouseMask |= 1u << (int)button;
                foreach (var action in _actions.Values) action.Sample();
                foreach (var action in _actions.Values) action.Notify();
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
        private void OnKeyChar(IKeyboard keyboard, char character) => RawTextInput?.Invoke(character);
        private void OnMouseDown(IMouse mouse, Silk.NET.Input.MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, true);
        private void OnMouseUp(IMouse mouse, Silk.NET.Input.MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, false);

        private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
        {
            var newPosition = new Vector2(position.X, position.Y);
            if (_hasLastMousePosition)
            {
                _mouseDelta += newPosition - _lastMousePosition;
            }
            else
            {
                _hasLastMousePosition = true;
            }

            _mousePosition = newPosition;
            _lastMousePosition = newPosition;
            RawMouseMoved?.Invoke(newPosition);
        }

        private void OnMouseWheel(IMouse mouse, ScrollWheel scrollWheel)
        {
            _pendingScroll += scrollWheel.Y;
            RawMouseScrolled?.Invoke(new Vector2(scrollWheel.X, scrollWheel.Y));
        }

        /// <summary>Unsubscribes devices and invalidates owned actions on the game thread. Does not dispose the borrowed context.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            EnsureUsable();
            if (_updating) throw new InvalidOperationException("Dispose input outside input callbacks.");
            _disposed = true;
            ActionPressed = null;
            ActionReleased = null;
            RawKeyDown = null;
            RawKeyUp = null;
            RawTextInput = null;
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
        }

        /// <summary>Sets the native cursor mode for all mice on the game thread.</summary>
        public void SetCursorMode(CursorMode mode)
        {
            EnsureUsable();
            foreach (IMouse mouse in _mice)
                mouse.Cursor.CursorMode = mode;
        }
    }
}
