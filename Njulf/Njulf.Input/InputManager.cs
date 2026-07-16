using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Silk.NET.Input;

namespace Njulf.Input
{
    public class InputManager : IInputManager, IDisposable
    {
        private readonly IInputContext _inputContext;
        private readonly Dictionary<string, Njulf.Input.Action> _actions = new Dictionary<string, Njulf.Input.Action>();
        private readonly List<IKeyboard> _keyboards = new List<IKeyboard>();
        private readonly List<IMouse> _mice = new List<IMouse>();
        private readonly List<IJoystick> _joysticks = new List<IJoystick>();
        
        private Vector2 _mousePosition;
        private Vector2 _mouseDelta;
        private Vector2 _lastMousePosition;
        private float _mouseScrollDelta;
        private bool _hasLastMousePosition;
        private bool _isInitialized;
        
        public event System.Action<string>? OnActionPressed;
        public event System.Action<string>? OnActionReleased;
        /// <summary>Raw input events for optional UI integrations. Game actions remain unchanged.</summary>
        public event System.Action<Key, char>? RawKeyDown;
        public event System.Action<Key>? RawKeyUp;
        public event System.Action<char>? RawTextInput;
        public event System.Action<int, bool>? RawMouseButtonChanged;
        public event System.Action<Vector2>? RawMouseMoved;
        public event System.Action<Vector2>? RawMouseScrolled;
        
        public Vector2 MousePosition => _mousePosition;
        public Vector2 MouseDelta => _mouseDelta;
        public float MouseScrollDelta => _mouseScrollDelta;
        
        public InputManager(IInputContext inputContext)
        {
            _inputContext = inputContext ?? throw new ArgumentNullException(nameof(inputContext));
        }
        
        public void Initialize()
        {
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
        
        public Njulf.Input.Action CreateAction(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Action name cannot be null or whitespace.", nameof(name));
            
            if (_actions.ContainsKey(name))
                throw new ArgumentException("Action '" + name + "' already exists.");
            
            var action = new Action(name);
            action.OnPressed += () => OnActionPressed?.Invoke(name);
            action.OnReleased += () => OnActionReleased?.Invoke(name);
            _actions[name] = action;
            return action;
        }
        
        public Njulf.Input.Action? GetAction(string name)
        {
            _actions.TryGetValue(name, out var action);
            return action;
        }
        
        public void Bind(string actionName, InputBinding binding)
        {
            var action = GetAction(actionName);
            if (action == null)
                throw new ArgumentException("Action '" + actionName + "' not found.");
            
            action.AddBinding(binding);
        }
        
        public bool IsKeyDown(string action)
        {
            var act = GetAction(action);
            return act?.IsPressed ?? false;
        }
        
        public bool IsKeyPressed(string action)
        {
            var act = GetAction(action);
            return act?.WasPressed ?? false;
        }
        
        public bool IsKeyReleased(string action)
        {
            var act = GetAction(action);
            return act?.WasReleased ?? false;
        }
        
        public bool IsMouseButtonDown(int button)
        {
            if (!Enum.IsDefined(typeof(MouseButton), button))
                return false;
            
            foreach (var mouse in _mice)
            {
                if (mouse.IsButtonPressed((MouseButton)button))
                    return true;
            }
            return false;
        }

        public bool IsPhysicalKeyDown(Key key, int keyboardIndex = 0)
        {
            if (keyboardIndex < 0 || keyboardIndex >= _keyboards.Count)
                return false;

            IKeyboard keyboard = _keyboards[keyboardIndex];
            return keyboard != null && keyboard.IsKeyPressed(key);
        }
        
        public bool IsMouseButtonPressed(int button)
        {
            return IsMouseButtonDown(button);
        }
        
        public bool IsMouseButtonReleased(int button)
        {
            return !IsMouseButtonDown(button);
        }
        
        public void Update()
        {
            if (!_isInitialized)
                Initialize();

            _mouseScrollDelta = 0f;

            foreach (var action in _actions.Values)
            {
                action.Update(_keyboards, _mice, _joysticks);
            }
        }

        public Vector2 ConsumeMouseDelta()
        {
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
        private void OnMouseDown(IMouse mouse, MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, true);
        private void OnMouseUp(IMouse mouse, MouseButton button) => RawMouseButtonChanged?.Invoke((int)button, false);
        
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
            _mouseScrollDelta += scrollWheel.Y;
            RawMouseScrolled?.Invoke(new Vector2(scrollWheel.X, scrollWheel.Y));
        }
        
        public void Dispose()
        {
            OnActionPressed = null;
            OnActionReleased = null;
            RawKeyDown = null;
            RawKeyUp = null;
            RawTextInput = null;
            RawMouseButtonChanged = null;
            RawMouseMoved = null;
            RawMouseScrolled = null;
            
            foreach (var action in _actions.Values)
            {
                action.Dispose();
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

        public void SetCursorMode(CursorMode mode)
        {
            foreach (IMouse mouse in _mice)
                mouse.Cursor.CursorMode = mode;
        }
    }
}
