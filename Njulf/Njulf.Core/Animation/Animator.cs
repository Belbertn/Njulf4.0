using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public sealed class Animator : IUpdateable
    {
        private readonly Matrix4x4[][] _skinMatrices;
        private AnimationClip? _fadeFromClip;
        private AnimationClip? _fadeToClip;
        private float _fadeDurationSeconds;
        private float _fadeTimeSeconds;
        private float _fadeFromTimeSeconds;
        private bool _playing;

        public Animator(Skeleton skeleton, IReadOnlyList<Skin>? skins = null, IReadOnlyList<AnimationClip>? clips = null)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            Skins = skins ?? Array.Empty<Skin>();
            Clips = clips ?? Array.Empty<AnimationClip>();
            CurrentPose = new AnimationPose(Skeleton.Joints.Count);
            CurrentPose.ResetToBindPose(Skeleton);
            CurrentPose.BuildGlobalMatrices(Skeleton);

            _skinMatrices = new Matrix4x4[Skins.Count][];
            for (int i = 0; i < _skinMatrices.Length; i++)
                _skinMatrices[i] = new Matrix4x4[Skins[i].JointIndices.Count];

            BuildSkinMatrices();
        }

        public Skeleton Skeleton { get; }
        public IReadOnlyList<Skin> Skins { get; }
        public IReadOnlyList<AnimationClip> Clips { get; }
        public AnimationPose CurrentPose { get; }
        public bool Enabled { get; set; } = true;
        public int UpdateOrder { get; set; }
        public AnimationClip? CurrentClip { get; private set; }
        public float TimeSeconds { get; private set; }
        public float Speed { get; set; } = 1.0f;
        public bool Looping { get; private set; } = true;
        public bool IsPlaying => _playing && CurrentClip != null;
        public bool IsPaused => CurrentClip != null && !_playing;

        public void Play(AnimationClip clip, bool loop = true)
        {
            CurrentClip = clip ?? throw new ArgumentNullException(nameof(clip));
            TimeSeconds = 0f;
            Looping = loop;
            _playing = true;
            ClearCrossFade();
            EvaluateCurrentPose();
        }

        public void Pause()
        {
            _playing = false;
        }

        public void Resume()
        {
            if (CurrentClip != null)
                _playing = true;
        }

        public void Stop()
        {
            CurrentClip = null;
            TimeSeconds = 0f;
            _playing = false;
            ClearCrossFade();
            CurrentPose.ResetToBindPose(Skeleton);
            CurrentPose.BuildGlobalMatrices(Skeleton);
            BuildSkinMatrices();
        }

        public void Seek(float timeSeconds)
        {
            TimeSeconds = NormalizeClipTime(CurrentClip, timeSeconds, Looping);
            EvaluateCurrentPose();
        }

        public void CrossFade(AnimationClip nextClip, float durationSeconds)
        {
            if (nextClip == null)
                throw new ArgumentNullException(nameof(nextClip));
            if (durationSeconds <= 0f || CurrentClip == null)
            {
                Play(nextClip, Looping);
                return;
            }

            _fadeFromClip = CurrentClip;
            _fadeToClip = nextClip;
            _fadeDurationSeconds = durationSeconds;
            _fadeTimeSeconds = 0f;
            _fadeFromTimeSeconds = TimeSeconds;
            CurrentClip = nextClip;
            TimeSeconds = 0f;
            _playing = true;
        }

        public ReadOnlySpan<Matrix4x4> GetSkinMatrices(int skinIndex)
        {
            if (skinIndex < 0 || skinIndex >= _skinMatrices.Length)
                throw new ArgumentOutOfRangeException(nameof(skinIndex));

            return _skinMatrices[skinIndex];
        }

        public void Update(float deltaTime)
        {
            if (!Enabled || !_playing || CurrentClip == null)
                return;

            float scaledDelta = deltaTime * Speed;
            if (_fadeToClip != null && _fadeFromClip != null)
            {
                _fadeTimeSeconds += System.Math.Max(0f, scaledDelta);
                _fadeFromTimeSeconds = NormalizeClipTime(_fadeFromClip, _fadeFromTimeSeconds + scaledDelta, Looping);
                TimeSeconds = NormalizeClipTime(_fadeToClip, TimeSeconds + scaledDelta, Looping);

                if (_fadeTimeSeconds >= _fadeDurationSeconds)
                    ClearCrossFade();
            }
            else
            {
                TimeSeconds = NormalizeClipTime(CurrentClip, TimeSeconds + scaledDelta, Looping);
                if (!Looping && TimeSeconds >= CurrentClip.DurationSeconds)
                    _playing = false;
            }

            EvaluateCurrentPose();
        }

        private void EvaluateCurrentPose()
        {
            CurrentPose.ResetToBindPose(Skeleton);

            if (_fadeFromClip != null && _fadeToClip != null && _fadeDurationSeconds > 0f)
            {
                float fade = System.Math.Clamp(_fadeTimeSeconds / _fadeDurationSeconds, 0f, 1f);
                ApplyBlendedClips(_fadeFromClip, _fadeFromTimeSeconds, _fadeToClip, TimeSeconds, fade);
            }
            else if (CurrentClip != null)
            {
                ApplyClip(CurrentClip, TimeSeconds, weight: 1f);
            }

            CurrentPose.BuildGlobalMatrices(Skeleton);
            BuildSkinMatrices();
        }

        private void ApplyBlendedClips(AnimationClip fromClip, float fromTime, AnimationClip toClip, float toTime, float amount)
        {
            var blended = new AnimationTransform[Skeleton.Joints.Count];
            for (int i = 0; i < blended.Length; i++)
                blended[i] = Skeleton.Joints[i].LocalBindPose;

            SampleClipInto(fromClip, fromTime, blended);
            var target = new AnimationTransform[Skeleton.Joints.Count];
            for (int i = 0; i < target.Length; i++)
                target[i] = Skeleton.Joints[i].LocalBindPose;
            SampleClipInto(toClip, toTime, target);

            for (int i = 0; i < blended.Length; i++)
                CurrentPose.SetLocalTransform(i, AnimationTransform.Lerp(blended[i], target[i], amount));
        }

        private void ApplyClip(AnimationClip clip, float timeSeconds, float weight)
        {
            if (weight <= 0f)
                return;

            for (int i = 0; i < clip.Channels.Count; i++)
            {
                AnimationChannel channel = clip.Channels[i];
                if (channel.TargetJointIndex < 0 || channel.TargetJointIndex >= Skeleton.Joints.Count)
                    continue;

                AnimationTransform current = CurrentPose.GetLocalTransform(channel.TargetJointIndex);
                CurrentPose.SetLocalTransform(channel.TargetJointIndex, ApplyChannel(channel, current, timeSeconds));
            }
        }

        private void SampleClipInto(AnimationClip clip, float timeSeconds, AnimationTransform[] transforms)
        {
            for (int i = 0; i < clip.Channels.Count; i++)
            {
                AnimationChannel channel = clip.Channels[i];
                if (channel.TargetJointIndex < 0 || channel.TargetJointIndex >= transforms.Length)
                    continue;

                transforms[channel.TargetJointIndex] = ApplyChannel(channel, transforms[channel.TargetJointIndex], timeSeconds);
            }
        }

        private static AnimationTransform ApplyChannel(AnimationChannel channel, AnimationTransform current, float timeSeconds)
        {
            if (channel.Path == AnimationChannelPath.Rotation)
            {
                Quaternion rotation = SampleRotation(channel.Sampler, timeSeconds);
                return new AnimationTransform(current.Translation, rotation, current.Scale);
            }

            Vector4 value = Sample(channel.Sampler, timeSeconds);
            return channel.Path switch
            {
                AnimationChannelPath.Translation => new AnimationTransform(new Vector3(value.X, value.Y, value.Z), current.Rotation, current.Scale),
                AnimationChannelPath.Scale => new AnimationTransform(current.Translation, current.Rotation, new Vector3(value.X, value.Y, value.Z)),
                _ => current
            };
        }

        private static Quaternion SampleRotation(AnimationSampler sampler, float timeSeconds)
        {
            if (sampler.Interpolation == AnimationInterpolation.CubicSpline)
                throw new NotSupportedException("Cubic spline animation interpolation is imported for diagnostics but is not sampled yet.");

            if (sampler.InputTimes.Count == 0 || sampler.OutputValues.Count == 0)
                return Quaternion.Identity;
            if (sampler.InputTimes.Count == 1 || timeSeconds <= sampler.InputTimes[0])
                return ToQuaternion(sampler.OutputValues[0]);

            int last = sampler.InputTimes.Count - 1;
            if (timeSeconds >= sampler.InputTimes[last])
                return ToQuaternion(sampler.OutputValues[System.Math.Min(last, sampler.OutputValues.Count - 1)]);

            int key = 0;
            while (key + 1 < sampler.InputTimes.Count && sampler.InputTimes[key + 1] < timeSeconds)
                key++;

            Quaternion a = ToQuaternion(sampler.OutputValues[key]);
            if (sampler.Interpolation == AnimationInterpolation.Step)
                return a;

            Quaternion b = ToQuaternion(sampler.OutputValues[key + 1]);
            float start = sampler.InputTimes[key];
            float end = sampler.InputTimes[key + 1];
            float t = end > start ? (timeSeconds - start) / (end - start) : 0f;
            return Quaternion.Slerp(a, b, t).Normalized();
        }

        private static Quaternion ToQuaternion(Vector4 value)
        {
            return new Quaternion(value.X, value.Y, value.Z, value.W).Normalized();
        }

        private static Vector4 Sample(AnimationSampler sampler, float timeSeconds)
        {
            if (sampler.Interpolation == AnimationInterpolation.CubicSpline)
                throw new NotSupportedException("Cubic spline animation interpolation is imported for diagnostics but is not sampled yet.");

            if (sampler.InputTimes.Count == 0 || sampler.OutputValues.Count == 0)
                return Vector4.Zero;
            if (sampler.InputTimes.Count == 1 || timeSeconds <= sampler.InputTimes[0])
                return sampler.OutputValues[0];

            int last = sampler.InputTimes.Count - 1;
            if (timeSeconds >= sampler.InputTimes[last])
                return sampler.OutputValues[System.Math.Min(last, sampler.OutputValues.Count - 1)];

            int key = 0;
            while (key + 1 < sampler.InputTimes.Count && sampler.InputTimes[key + 1] < timeSeconds)
                key++;

            if (sampler.Interpolation == AnimationInterpolation.Step)
                return sampler.OutputValues[key];

            float start = sampler.InputTimes[key];
            float end = sampler.InputTimes[key + 1];
            float t = end > start ? (timeSeconds - start) / (end - start) : 0f;
            return Vector4.Lerp(sampler.OutputValues[key], sampler.OutputValues[key + 1], t);
        }

        private void BuildSkinMatrices()
        {
            ReadOnlySpan<Matrix4x4> global = CurrentPose.GlobalMatrices;
            for (int skinIndex = 0; skinIndex < Skins.Count; skinIndex++)
            {
                Skin skin = Skins[skinIndex];
                Matrix4x4[] matrices = _skinMatrices[skinIndex];
                for (int i = 0; i < skin.JointIndices.Count; i++)
                {
                    int jointIndex = skin.JointIndices[i];
                    matrices[i] = skin.InverseBindMatrices[i] * global[jointIndex];
                }
            }
        }

        private static float NormalizeClipTime(AnimationClip? clip, float timeSeconds, bool looping)
        {
            if (clip == null)
                return 0f;
            if (clip.DurationSeconds <= 0f)
                return 0f;
            if (!looping)
                return System.Math.Clamp(timeSeconds, 0f, clip.DurationSeconds);

            float wrapped = timeSeconds % clip.DurationSeconds;
            return wrapped < 0f ? wrapped + clip.DurationSeconds : wrapped;
        }

        private void ClearCrossFade()
        {
            _fadeFromClip = null;
            _fadeToClip = null;
            _fadeDurationSeconds = 0f;
            _fadeTimeSeconds = 0f;
            _fadeFromTimeSeconds = 0f;
        }
    }
}
