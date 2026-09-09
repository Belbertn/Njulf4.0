using System;
using System.Collections.Generic;

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
            var (mouse, state) = InputDeviceProxy.Create<IMouse>();
            state.Methods["IsButtonPressed"] = _ => false;
            using var input = new InputManager(new TestInputContext(mice: [mouse]));
            input.Initialize();

            RaiseMouseMove(state, mouse, 320f, 180f);

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
            var (mouse, state) = InputDeviceProxy.Create<IMouse>();
            state.Methods["IsButtonPressed"] = _ => false;
            using var input = new InputManager(new TestInputContext(mice: [mouse]));
            input.Initialize();

            RaiseMouseMove(state, mouse, 100f, 100f);
            RaiseMouseMove(state, mouse, 110f, 103f);
            RaiseMouseMove(state, mouse, 115f, 99f);

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
            var (mouse, state) = InputDeviceProxy.Create<IMouse>();
            state.Methods["IsButtonPressed"] = _ => false;
            using var input = new InputManager(new TestInputContext(mice: [mouse]));
            input.Initialize();

            RaiseMouseMove(state, mouse, 20f, 20f);
            RaiseMouseMove(state, mouse, 28f, 24f);
            input.Update();

            AssertVector(input.ConsumeMouseDelta(), new Vector2(8f, 4f));
        }

        private static void RaiseMouseMove(InputDeviceProxy state, IMouse mouse, float x, float y) =>
            state.Raise("MouseMove", mouse, new System.Numerics.Vector2(x, y));

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.0001f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.0001f));
        }

    }
}
