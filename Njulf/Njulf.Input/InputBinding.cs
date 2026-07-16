using System;
using System.Collections.Generic;
using Silk.NET.Input;

namespace Njulf.Input
{
    /// <summary>
    /// Defines the type of input device for a binding.
    /// </summary>
    public enum BindingDeviceType
    {
        Keyboard,
        Mouse,
        Joystick
    }
    
    /// <summary>
    /// Joystick button codes.
    /// </summary>
    public enum JoystickButton
    {
        Button0 = 0,
        Button1,
        Button2,
        Button3,
        Button4,
        Button5,
        Button6,
        Button7,
        Button8,
        Button9,
        Button10,
        Button11,
        Button12,
        Button13,
        Button14,
        Button15
    }
    
    /// <summary>
    /// Joystick axis codes.
    /// </summary>
    public enum JoystickAxis
    {
        X = 0,
        Y,
        Z,
        Rx,
        Ry,
        Rz,
        Button0 = 100,
        Button1,
        Button2,
        Button3,
        Button4,
        Button5,
        Button6,
        Button7,
        Button8,
        Button9,
        Button10,
        Button11,
        Button12,
        Button13,
        Button14,
        Button15
    }
    
    /// <summary>
    /// Represents a binding between an input action and a physical input (key, mouse button, etc.).
    /// </summary>
    public class InputBinding : IDisposable
    {
        private readonly BindingDeviceType _deviceType;
        private readonly int _deviceIndex;
        private readonly int _inputCode;
        private readonly bool _isNegative;
        
        /// <summary>
        /// Gets the type of device this binding targets.
        /// </summary>
        public BindingDeviceType DeviceType => _deviceType;
        
        /// <summary>
        /// Gets the device index (0 for primary, 1+ for additional devices).
        /// </summary>
        public int DeviceIndex => _deviceIndex;
        
        /// <summary>
        /// Gets the input code (key code, mouse button, joystick button/axis).
        /// </summary>
        public int InputCode => _inputCode;
        
        /// <summary>
        /// Gets whether this binding represents a negative axis value (e.g., left on a joystick axis).
        /// </summary>
        public bool IsNegative => _isNegative;
        
        /// <summary>
        /// Initializes a new keyboard binding.
        /// </summary>
        /// <param name="key">The key to bind.</param>
        /// <param name="deviceIndex">The keyboard index (default: 0 for primary keyboard).</param>
        public InputBinding(Key key, int deviceIndex = 0)
            : this(BindingDeviceType.Keyboard, deviceIndex, (int)key, false)
        {
        }
        
        /// <summary>
        /// Initializes a new mouse button binding.
        /// </summary>
        /// <param name="button">The mouse button to bind.</param>
        /// <param name="deviceIndex">The mouse index (default: 0 for primary mouse).</param>
        public InputBinding(MouseButton button, int deviceIndex = 0)
            : this(BindingDeviceType.Mouse, deviceIndex, (int)button, false)
        {
        }
        
        /// <summary>
        /// Initializes a new joystick button binding.
        /// </summary>
        /// <param name="button">The joystick button to bind.</param>
        /// <param name="deviceIndex">The joystick index.</param>
        public InputBinding(JoystickButton button, int deviceIndex)
            : this(BindingDeviceType.Joystick, deviceIndex, (int)button, false)
        {
        }
        
        /// <summary>
        /// Initializes a new joystick axis binding.
        /// </summary>
        /// <param name="axis">The joystick axis to bind.</param>
        /// <param name="deviceIndex">The joystick index.</param>
        /// <param name="isNegative">Whether to use the negative direction of the axis.</param>
        public InputBinding(JoystickAxis axis, int deviceIndex, bool isNegative = false)
            : this(BindingDeviceType.Joystick, deviceIndex, (int)axis, isNegative)
        {
        }
        
        /// <summary>
        /// Initializes a new input binding with raw values.
        /// </summary>
        /// <param name="deviceType">The type of device.</param>
        /// <param name="deviceIndex">The device index.</param>
        /// <param name="inputCode">The input code.</param>
        /// <param name="isNegative">Whether this is a negative axis binding.</param>
        public InputBinding(BindingDeviceType deviceType, int deviceIndex, int inputCode, bool isNegative)
        {
            _deviceType = deviceType;
            _deviceIndex = deviceIndex;
            _inputCode = inputCode;
            _isNegative = isNegative;
        }
        
        /// <summary>
        /// Checks if this binding is currently active based on the current input device states.
        /// </summary>
        /// <param name="keyboards">Collection of keyboard devices.</param>
        /// <param name="mice">Collection of mouse devices.</param>
        /// <param name="joysticks">Collection of joystick/gamepad devices.</param>
        /// <returns>True if the binding is active, false otherwise.</returns>
        public bool IsActive(
            IReadOnlyList<IKeyboard> keyboards,
            IReadOnlyList<IMouse> mice,
            IReadOnlyList<IJoystick> joysticks)
        {
            return _deviceType switch
            {
                BindingDeviceType.Keyboard => IsKeyboardActive(keyboards),
                BindingDeviceType.Mouse => IsMouseActive(mice),
                BindingDeviceType.Joystick => IsJoystickActive(joysticks),
                _ => false
            };
        }
        
        private bool IsKeyboardActive(IReadOnlyList<IKeyboard> keyboards)
        {
            if (_deviceIndex >= keyboards.Count)
                return false;
            
            var keyboard = keyboards[_deviceIndex];
            if (keyboard == null)
                return false;
            
            return keyboard.IsKeyPressed((Key)_inputCode);
        }
        
        private bool IsMouseActive(IReadOnlyList<IMouse> mice)
        {
            if (_deviceIndex >= mice.Count)
                return false;
            
            var mouse = mice[_deviceIndex];
            if (mouse == null)
                return false;
            
            return mouse.IsButtonPressed((MouseButton)_inputCode);
        }
        
        private bool IsJoystickActive(IReadOnlyList<IJoystick> joysticks)
        {
            if (_deviceIndex >= joysticks.Count)
                return false;
            
            var joystick = joysticks[_deviceIndex];
            if (joystick == null)
                return false;
            
            if (_isNegative)
            {
                // For negative axis, check if axis value is less than -0.5
                return GetJoystickAxis(joystick, _inputCode) < -0.5f;
            }
            else if ((JoystickAxis)_inputCode >= JoystickAxis.Button0)
            {
                // This is a button
                return IsJoystickButtonPressed(joystick, _inputCode - (int)JoystickAxis.Button0);
            }
            else
            {
                // This is an axis, check if value is greater than 0.5
                return GetJoystickAxis(joystick, _inputCode) > 0.5f;
            }
        }
        
        /// <summary>
        /// Gets the axis value for this binding (for analog inputs like joystick axes).
        /// </summary>
        /// <param name="keyboards">Collection of keyboard devices.</param>
        /// <param name="mice">Collection of mouse devices.</param>
        /// <param name="joysticks">Collection of joystick/gamepad devices.</param>
        /// <returns>The axis value, or 0 if not applicable.</returns>
        public float GetAxisValue(
            IReadOnlyList<IKeyboard> keyboards,
            IReadOnlyList<IMouse> mice,
            IReadOnlyList<IJoystick> joysticks)
        {
            if (_deviceType != BindingDeviceType.Joystick)
                return IsActive(keyboards, mice, joysticks) ? 1.0f : 0.0f;
            
            if (_deviceIndex >= joysticks.Count)
                return 0.0f;
            
            var joystick = joysticks[_deviceIndex];
            if (joystick == null)
                return 0.0f;
            
            var value = GetJoystickAxis(joystick, _inputCode);
            return _isNegative ? -value : value;
        }

        private static float GetJoystickAxis(IJoystick joystick, int axisIndex)
        {
            return axisIndex >= 0 && axisIndex < joystick.Axes.Count
                ? joystick.Axes[axisIndex].Position
                : 0.0f;
        }

        private static bool IsJoystickButtonPressed(IJoystick joystick, int buttonIndex)
        {
            return buttonIndex >= 0 && buttonIndex < joystick.Buttons.Count &&
                   joystick.Buttons[buttonIndex].Pressed;
        }
        
        public void Dispose()
        {
            // Nothing to dispose - this is a data class
        }
        
        public override string ToString()
        {
            return $"{_deviceType}:{_deviceIndex}:{_inputCode}{(IsNegative ? "(-)" : "")}";
        }
    }
}
