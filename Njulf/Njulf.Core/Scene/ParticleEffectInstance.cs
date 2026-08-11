using System;
using Njulf.Core.Math;
using Njulf.Core.Vfx;

namespace Njulf.Core.Scene
{
    public sealed class ParticleEffectInstance : IIdentifiedSceneEntity
    {
        private string _name;
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private bool _visible = true;
        private uint _randomSeed = 1;

        public ParticleEffectInstance(ParticleEffect effect)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect));
            _name = effect.Name;
        }

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name
        {
            get => _name;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_name, next, StringComparison.Ordinal))
                    return;
                _name = next;
            }
        }
        public ParticleEffect Effect { get; }
        /// <summary>Optional asset identity for serializable effect instances.</summary>
        public SceneAssetReference? AssetReference { get; set; }
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set
            {
                if (_worldMatrix.Equals(value))
                    return;
                _worldMatrix = value;
                PublishChange(SceneMutationKind.Transform);
            }
        }
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value)
                    return;
                _visible = value;
                PublishChange(SceneMutationKind.Visibility);
            }
        }
        public bool Playing { get; private set; } = true;
        public bool Paused { get; private set; }
        public bool Stopped { get; private set; }
        public uint RandomSeed
        {
            get => _randomSeed;
            set
            {
                if (_randomSeed == value)
                    return;
                _randomSeed = value;
                PublishChange(SceneMutationKind.ParticleState);
            }
        }
        public ulong Version { get; private set; }
        public bool ClearRequested { get; private set; }
        public event Action<ParticleEffectInstance, SceneMutationKind>? Changed;

        public void Play()
        {
            if (Playing && !Paused && !Stopped)
                return;
            Playing = true;
            Paused = false;
            Stopped = false;
            PublishChange(SceneMutationKind.ParticleState);
        }

        public void Pause()
        {
            if (!Playing && Paused && !Stopped)
                return;
            Playing = false;
            Paused = true;
            PublishChange(SceneMutationKind.ParticleState);
        }

        public void Stop(bool clearParticles)
        {
            bool nextClearRequested = ClearRequested || clearParticles;
            if (!Playing && !Paused && Stopped &&
                ClearRequested == nextClearRequested)
            {
                return;
            }
            Playing = false;
            Paused = false;
            Stopped = true;
            ClearRequested = nextClearRequested;
            PublishChange(SceneMutationKind.ParticleState);
        }

        public void Restart(uint? seed = null)
        {
            if (seed.HasValue)
                _randomSeed = seed.Value;

            Playing = true;
            Paused = false;
            Stopped = false;
            ClearRequested = true;
            PublishChange(SceneMutationKind.ParticleState);
        }

        /// <summary>
        /// Signals that an animated/procedural emitter definition or authored
        /// energy envelope changed without replacing this instance.
        /// </summary>
        public void MarkContentChanged() =>
            PublishChange(SceneMutationKind.Content | SceneMutationKind.ParticleState);

        public void ConsumeClearRequest()
        {
            ClearRequested = false;
        }

        private void PublishChange(SceneMutationKind kind)
        {
            Version = Version == ulong.MaxValue ? 1UL : Version + 1UL;
            Changed?.Invoke(this, kind);
        }
    }
}
