using System;
using System.Collections.Generic;
using Silk.NET.Input;

namespace Njulf.Input
{
    /// <summary>
    /// Represents a user-defined input action that can be triggered by multiple input bindings.
    /// </summary>
    public class Action : IDisposable
    {
        private readonly string _name;
        private readonly List<InputBinding> _bindings = new List<InputBinding>();
        private bool _isPressed;
        private bool _wasPressed;
        
        /// <summary>
        /// Event triggered when this action is pressed.
        /// </summary>
        public event Action? OnPressed;
        
        /// <summary>
        /// Event triggered when this action is released.
        /// </summary>
        public event Action? OnReleased;
        
        /// <summary>
        /// Gets the name of this action.
        /// </summary>
        public string Name => _name;
        
        /// <summary>
        /// Gets whether this action is currently pressed.
        /// </summary>
        public bool IsPressed => _isPressed;
        
        /// <summary>
        /// Gets whether this action was just pressed in the last frame.
        /// </summary>
        public bool WasPressed => _wasPressed && !_isPressed;
        
        /// <summary>
        /// Gets whether this action was just released in the last frame.
        /// </summary>
        public bool WasReleased => !_wasPressed && _isPressed;
        
        /// <summary>
        /// Initializes a new input action.
        /// </summary>
        /// <param name="name">The unique name of this action.</param>
        public Action(string name)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
        }
        
        /// <summary>
        /// Adds a binding to this action.
        /// </summary>
        /// <param name="binding">The binding to add.</param>
        public void AddBinding(InputBinding binding)
        {
            _bindings.Add(binding);
        }
        
        /// <summary>
        /// Removes a binding from this action.
        /// </summary>
        /// <param name="binding">The binding to remove.</param>
        public void RemoveBinding(InputBinding binding)
        {
            _bindings.Remove(binding);
        }
        
        /// <summary>
        /// Updates the state of this action based on the current input state.
        /// </summary>
        /// <param name="keyboards">Collection of keyboard devices.</param>
        /// <param name="mice">Collection of mouse devices.</param>
        /// <param name="joysticks">Collection of joystick/gamepad devices.</param>
        public void Update(
            IReadOnlyList<IKeyboard> keyboards,
            IReadOnlyList<IMouse> mice,
            IReadOnlyList<IJoystick> joysticks)
        {
            _wasPressed = _isPressed;
            
            foreach (var binding in _bindings)
            {
                if (binding.IsActive(keyboards, mice, joysticks))
                {
                    if (!_isPressed)
                    {
                        _isPressed = true;
                        OnPressed?.Invoke();
                    }
                    return;
                }
            }
            
            if (_isPressed)
            {
                _isPressed = false;
                OnReleased?.Invoke();
            }
        }
        
        /// <summary>
        /// Resets the action state (used when reloading bindings).
        /// </summary>
        public void Reset()
        {
            _isPressed = false;
            _wasPressed = false;
        }
        
        public void Dispose()
        {
            OnPressed = null;
            OnReleased = null;
            _bindings.Clear();
        }
    }
}
