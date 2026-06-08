using System;
using Njulf.Core.Math;

namespace Njulf.Core.Interfaces
{
    public interface IInputManager
    {
        bool IsKeyDown(string action);
        bool IsKeyPressed(string action);
        bool IsKeyReleased(string action);
        bool IsMouseButtonDown(int button);
        bool IsMouseButtonPressed(int button);
        bool IsMouseButtonReleased(int button);
        Vector2 MousePosition { get; }
        Vector2 MouseDelta { get; }
        float MouseScrollDelta { get; }
        
        event Action<string> OnActionPressed;
        event Action<string> OnActionReleased;
        
        void Update();
    }
}
