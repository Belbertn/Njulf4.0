using System;
using Njulf.Core.Math;
using Njulf.Core.Vfx;

namespace Njulf.Core.Scene
{
    public sealed class ParticleEffectInstance
    {
        public ParticleEffectInstance(ParticleEffect effect)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect));
            Name = effect.Name;
        }

        public string Name { get; set; }
        public ParticleEffect Effect { get; }
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public bool Visible { get; set; } = true;
        public bool Playing { get; private set; } = true;
        public bool Paused { get; private set; }
        public bool Stopped { get; private set; }
        public uint RandomSeed { get; set; } = 1;
        public ulong Version { get; private set; }
        public bool ClearRequested { get; private set; }

        public void Play()
        {
            Playing = true;
            Paused = false;
            Stopped = false;
            Version++;
        }

        public void Pause()
        {
            Playing = false;
            Paused = true;
            Version++;
        }

        public void Stop(bool clearParticles)
        {
            Playing = false;
            Paused = false;
            Stopped = true;
            ClearRequested |= clearParticles;
            Version++;
        }

        public void Restart(uint? seed = null)
        {
            if (seed.HasValue)
                RandomSeed = seed.Value;

            Playing = true;
            Paused = false;
            Stopped = false;
            ClearRequested = true;
            Version++;
        }

        public void ConsumeClearRequest()
        {
            ClearRequested = false;
        }
    }
}
