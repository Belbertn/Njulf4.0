using System;
using System.Collections.Generic;
using System.Reflection;
using Njulf.Core.Math;
using Njulf.Input;
using NUnit.Framework;
using Silk.NET.Input;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class InputManagerTests
    {
        [Test]
        public void FirstMouseMove_InitializesPositionWithoutDelta()
        {
            var input = new InputManager(new FakeInputContext());

            RaiseMouseMove(input, 320f, 180f);

            Assert.Multiple(() =>
            {
                AssertVector(input.MousePosition, new Vector2(320f, 180f));
                AssertVector(input.MouseDelta, Vector2.Zero);
                AssertVector(input.ConsumeMouseDelta(), Vector2.Zero);
            });
        }

        [Test]
        public void MouseMove_AccumulatesUntilConsumed()
        {
            var input = new InputManager(new FakeInputContext());

            RaiseMouseMove(input, 100f, 100f);
            RaiseMouseMove(input, 110f, 103f);
            RaiseMouseMove(input, 115f, 99f);

            Assert.Multiple(() =>
            {
                AssertVector(input.MouseDelta, new Vector2(15f, -1f));
                AssertVector(input.ConsumeMouseDelta(), new Vector2(15f, -1f));
                AssertVector(input.MouseDelta, Vector2.Zero);
                AssertVector(input.ConsumeMouseDelta(), Vector2.Zero);
            });
        }

        [Test]
        public void Update_DoesNotClearUnconsumedMouseDelta()
        {
            var input = new InputManager(new FakeInputContext());

            RaiseMouseMove(input, 20f, 20f);
            RaiseMouseMove(input, 28f, 24f);
            input.Update();

            AssertVector(input.ConsumeMouseDelta(), new Vector2(8f, 4f));
        }

        private static void RaiseMouseMove(InputManager input, float x, float y)
        {
            MethodInfo method = typeof(InputManager).GetMethod(
                "OnMouseMove",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("InputManager.OnMouseMove was not found.");

            method.Invoke(input, new object?[] { null, new System.Numerics.Vector2(x, y) });
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.0001f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.0001f));
        }

        private sealed class FakeInputContext : IInputContext
        {
            public IntPtr Handle => IntPtr.Zero;
            public IReadOnlyList<IGamepad> Gamepads => Array.Empty<IGamepad>();
            public IReadOnlyList<IJoystick> Joysticks => Array.Empty<IJoystick>();
            public IReadOnlyList<IKeyboard> Keyboards => Array.Empty<IKeyboard>();
            public IReadOnlyList<IMouse> Mice => Array.Empty<IMouse>();
            public IReadOnlyList<IInputDevice> OtherDevices => Array.Empty<IInputDevice>();

            public event Action<IInputDevice, bool>? ConnectionChanged
            {
                add { }
                remove { }
            }

            public void Dispose()
            {
            }
        }
    }
}
