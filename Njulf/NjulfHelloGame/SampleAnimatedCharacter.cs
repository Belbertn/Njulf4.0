using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Animation;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal static class SampleAnimatedCharacter
{
    private const string CharacterPath = "Strut.glb";
    private const float TargetHeight = 1.75f;
    private static readonly CoreVector3 TargetGroundCenter = new(1.35f, 0.0f, 3.6f);

    public static SampleBenchmarkActivationFrameState
        PrepareBenchmarkActivationFrame(Model character, int routeFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (routeFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(routeFrameIndex));

        (string Identity, Animator Animator)[] animators =
            ResolveBenchmarkAnimators(character);
        if (animators.Length == 0)
        {
            throw new InvalidOperationException(
                "The authored Sponza activation requires at least one Strut animator.");
        }

        ApplyBenchmarkAnimationFrame(animators, routeFrameIndex);

        return CaptureBenchmarkActivationFrame(character, routeFrameIndex);
    }

    public static SampleBenchmarkActivationFrameState
        PrepareBenchmarkActivationFrame(Scene scene, int routeFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        (string Identity, Animator Animator)[] animators =
            ResolveBenchmarkAnimators(scene);
        ApplyBenchmarkAnimationFrame(animators, routeFrameIndex);
        return CaptureBenchmarkActivationFrame(animators, routeFrameIndex);
    }

    public static SampleBenchmarkActivationFrameState
        CaptureBenchmarkActivationFrame(Model character, int routeFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (routeFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(routeFrameIndex));

        return CaptureBenchmarkActivationFrame(
            ResolveBenchmarkAnimators(character),
            routeFrameIndex);
    }

    public static SampleBenchmarkActivationFrameState
        CaptureBenchmarkActivationFrame(Scene scene, int routeFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return CaptureBenchmarkActivationFrame(
            ResolveBenchmarkAnimators(scene),
            routeFrameIndex);
    }

    private static SampleBenchmarkActivationFrameState
        CaptureBenchmarkActivationFrame(
            (string Identity, Animator Animator)[] animators,
            int routeFrameIndex)
    {
        var states = new List<SampleBenchmarkActivationAnimatorState>(
            animators.Length);
        foreach ((string identity, Animator animator) in animators)
        {
            AnimationClip clip = animator.CurrentClip ??
                throw new InvalidOperationException(
                    $"Activation animator '{identity}' has no current clip.");
            if (animator.Enabled || !animator.IsPlaying || !animator.Looping)
            {
                throw new InvalidOperationException(
                    $"Activation animator '{identity}' left its locked route state.");
            }
            uint[] matrixBits = CapturePoseMatrixBits(animator);
            states.Add(new SampleBenchmarkActivationAnimatorState(
                identity,
                clip.Name,
                clip.DurationSeconds,
                animator.TimeSeconds,
                animator.PoseRevision,
                animator.Skeleton.Joints.Count,
                animator.Skins.Count,
                CreatePoseHash(matrixBits))
            {
                GlobalMatrixComponentBits = Array.AsReadOnly(matrixBits)
            });
        }

        SampleBenchmarkActivationAnimatorState[] immutableStates =
            states.ToArray();
        string configurationFingerprint =
            SampleBenchmarkActivationFrameState.CreateConfigurationFingerprint(
                immutableStates);
        string frameHash = SampleBenchmarkActivationFrameState.CreateFrameHash(
            routeFrameIndex,
            configurationFingerprint,
            immutableStates);
        return new SampleBenchmarkActivationFrameState(
            SampleBenchmarkActivationFrameState.CurrentSchema,
            routeFrameIndex,
            configurationFingerprint,
            frameHash,
            Array.AsReadOnly(immutableStates));
    }

    internal static (string Identity, Animator Animator)[]
        ResolveBenchmarkAnimators(Model character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return ResolveBenchmarkAnimators(character.RenderObjects);
    }

    internal static (string Identity, Animator Animator)[]
        ResolveBenchmarkAnimators(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SampleBenchmarkSponzaSceneAnimationContract.ValidateAuthoredObjects(
            scene);
        RenderObject joints = scene.RenderObjects.Single(renderObject =>
            renderObject.Id ==
                SampleBenchmarkSponzaSceneAnimationContract.JointObjectId);
        RenderObject surface = scene.RenderObjects.Single(renderObject =>
            renderObject.Id ==
                SampleBenchmarkSponzaSceneAnimationContract.SurfaceObjectId);
        (string Identity, Animator Animator)[] animators =
            ResolveBenchmarkAnimators([joints, surface]);
        if (animators.Length != 2 ||
            !string.Equals(
                animators[0].Identity,
                SampleBenchmarkSponzaSceneAnimationContract.JointName,
                StringComparison.Ordinal) ||
            !string.Equals(
                animators[1].Identity,
                SampleBenchmarkSponzaSceneAnimationContract.SurfaceName,
                StringComparison.Ordinal) ||
            ReferenceEquals(animators[0].Animator, animators[1].Animator))
        {
            throw new InvalidDataException(
                "The authored Sponza Strut must resolve to its exact two " +
                "distinct animators in stable object-ID order.");
        }
        return animators;
    }

    private static (string Identity, Animator Animator)[]
        ResolveBenchmarkAnimators(IEnumerable<RenderObject> renderObjects)
    {
        (string Identity, Animator Animator)[] animators = renderObjects
            .OfType<SkinnedRenderObject>()
            .Where(static renderObject =>
                renderObject.Animator != null &&
                renderObject.Name.StartsWith(
                    "AnimatedCharacter.Strut.",
                    StringComparison.Ordinal))
            .Select(static renderObject => (
                Identity: renderObject.Name,
                Animator: renderObject.Animator!))
            .OrderBy(static item => item.Identity, StringComparer.Ordinal)
            .GroupBy(
                static item => item.Animator,
                ReferenceEqualityComparer.Instance)
            .Select(static group => (
                Identity: group.First().Identity,
                Animator: group.First().Animator))
            .ToArray();
        if (animators.Length == 0)
        {
            throw new InvalidOperationException(
                "The authored Sponza activation requires at least one Strut animator.");
        }
        return animators;
    }

    internal static void ApplyBenchmarkAnimationFrame(
        (string Identity, Animator Animator)[] animators,
        int routeFrameIndex)
    {
        foreach ((string identity, Animator animator) in animators)
        {
            if (animator.Clips.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Activation animator '{identity}' has no authored clip.");
            }
            AnimationClip clip = animator.Clips[0];
            if (routeFrameIndex == 0)
                animator.Play(clip, loop: true);
            else if (!ReferenceEquals(animator.CurrentClip, clip) ||
                     !animator.Looping || !animator.IsPlaying ||
                     animator.Enabled)
            {
                throw new InvalidOperationException(
                    $"Activation animator '{identity}' left its locked route state.");
            }

            animator.Speed = 1.0f;
            animator.Enabled = false;
            if (routeFrameIndex != 0)
            {
                animator.Seek(
                    routeFrameIndex * HelloGame.BenchmarkSimulationDeltaSeconds);
            }
        }
    }

    public static Model Configure(Scene scene, IContentManager content)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (content == null)
            throw new ArgumentNullException(nameof(content));

        Model asset = content.Load<Model>(CharacterPath)
            ?? throw new InvalidOperationException($"Content manager returned null for animated character '{CharacterPath}'.");
        Model character = asset.CreateInstance()
            ?? throw new InvalidOperationException($"Animated character '{CharacterPath}' did not create an instance.");

        int playingAnimators = StartFirstAnimationClip(character);
        CoreMatrix4x4 world = CreateCharacterWorld(character);
        if (character.RenderObjects.Count != 2)
        {
            throw new InvalidDataException(
                $"The authored Sponza animation fixture requires exactly two " +
                $"Strut render objects; loaded {character.RenderObjects.Count}.");
        }
        for (int i = 0; i < character.RenderObjects.Count; i++)
        {
            RenderObject renderObject = character.RenderObjects[i];
            renderObject.Name = $"AnimatedCharacter.Strut.{renderObject.Name}";
            renderObject.AssetReference = new SceneAssetReference { Path = CharacterPath, SubObject = i.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            renderObject.Id = i == 0
                ? SampleBenchmarkSponzaSceneAnimationContract.JointObjectId
                : SampleBenchmarkSponzaSceneAnimationContract.SurfaceObjectId;
            renderObject.WorldMatrix = world;
            renderObject.Visible = true;
            scene.Add(renderObject);

            if (renderObject is IUpdateable updateable)
                scene.Add(updateable);
        }

        Console.WriteLine(
            $"Loaded animated character '{CharacterPath}': objects={character.RenderObjects.Count}, " +
            $"skeletons={character.Skeletons.Count}, skins={character.Skins.Count}, clips={character.AnimationClips.Count}, playingAnimators={playingAnimators}.");

        return character;
    }

    private static int StartFirstAnimationClip(Model character)
    {
        int playing = 0;
        foreach (RenderObject renderObject in character.RenderObjects)
        {
            if (renderObject is not SkinnedRenderObject skinned ||
                skinned.Animator == null ||
                skinned.Animator.Clips.Count == 0)
            {
                continue;
            }

            skinned.Animator.Play(skinned.Animator.Clips[0], loop: true);
            playing++;
        }

        return playing;
    }

    private static CoreMatrix4x4 CreateCharacterWorld(Model character)
    {
        CoreVector3 size = character.BoundingBox.Size;
        float sourceHeight = size.Y > 0.0001f ? size.Y : 1.0f;
        float scale = TargetHeight / sourceHeight;
        CoreVector3 center = character.BoundingBox.Center;
        CoreVector3 min = character.BoundingBox.Min;

        var translation = new CoreVector3(
            TargetGroundCenter.X - center.X * scale,
            TargetGroundCenter.Y - min.Y * scale,
            TargetGroundCenter.Z - center.Z * scale);

        return CoreMatrix4x4.CreateScale(new CoreVector3(scale)) *
               CoreMatrix4x4.CreateTranslation(translation);
    }

    internal static string CreatePoseHash(Animator animator)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        foreach (CoreMatrix4x4 matrix in animator.CurrentPose.GlobalMatrices)
        {
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                int bits = BitConverter.SingleToInt32Bits(matrix[row, column]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    bytes,
                    bits);
                hash.AppendData(bytes);
            }
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static string CreatePoseHash(IReadOnlyList<uint> matrixBits)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        foreach (uint bits in matrixBits)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes,
                bits);
            hash.AppendData(bytes);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static uint[] CapturePoseMatrixBits(Animator animator)
    {
        ReadOnlySpan<CoreMatrix4x4> matrices =
            animator.CurrentPose.GlobalMatrices;
        var bits = new uint[checked(matrices.Length * 16)];
        int component = 0;
        for (int matrixIndex = 0; matrixIndex < matrices.Length; matrixIndex++)
        {
            CoreMatrix4x4 matrix = matrices[matrixIndex];
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                bits[component++] =
                    BitConverter.SingleToUInt32Bits(matrix[row, column]);
            }
        }
        return bits;
    }
}

/// <summary>
/// Preallocated activation recorder used by production timing. All topology
/// discovery and backing storage allocation happens during warmup; measured
/// frames only seek the cached animators and copy numeric pose bits.
/// </summary>
internal sealed class SampleBenchmarkActivationAnimationCapture
{
    private readonly (string Identity, Animator Animator)[] _animators;
    private readonly int[] _componentOffsets;
    private readonly int _componentCountPerFrame;
    private readonly float[] _times;
    private readonly ulong[] _revisions;
    private readonly uint[] _matrixBits;
    private readonly bool[] _recorded;
    private readonly int[] _routeFrameIndices;
    private readonly float[] _heldTimes;
    private readonly ulong[] _heldRevisions;
    private readonly uint[] _heldMatrixBits;
    private bool _phaseZeroHoldInitialized;

    public SampleBenchmarkActivationAnimationCapture(
        Scene scene,
        int maximumFrameCount)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrameCount, 1);
        _animators = SampleAnimatedCharacter.ResolveBenchmarkAnimators(scene);
        _componentOffsets = new int[_animators.Length + 1];
        for (int index = 0; index < _animators.Length; index++)
        {
            _componentOffsets[index + 1] = checked(
                _componentOffsets[index] +
                _animators[index].Animator.CurrentPose.GlobalMatrices.Length * 16);
        }
        _componentCountPerFrame = _componentOffsets[^1];
        _times = new float[checked(maximumFrameCount * _animators.Length)];
        _revisions = new ulong[_times.Length];
        _matrixBits = new uint[checked(
            maximumFrameCount * _componentCountPerFrame)];
        _recorded = new bool[maximumFrameCount];
        _routeFrameIndices = new int[maximumFrameCount];
        _heldTimes = new float[_animators.Length];
        _heldRevisions = new ulong[_animators.Length];
        _heldMatrixBits = new uint[_componentCountPerFrame];
    }

    public void InitializePhaseZeroHold()
    {
        if (_phaseZeroHoldInitialized)
        {
            ValidatePhaseZeroHold();
            return;
        }
        SampleAnimatedCharacter.ApplyBenchmarkAnimationFrame(_animators, 0);
        CaptureCurrent(
            _heldTimes,
            _heldRevisions,
            _heldMatrixBits,
            animatorBase: 0,
            matrixBase: 0);
        _phaseZeroHoldInitialized = true;
    }

    public void ValidatePhaseZeroHold()
    {
        if (!_phaseZeroHoldInitialized)
            throw new InvalidOperationException("Phase-zero hold is not initialized.");
        ValidateCurrent(
            _heldTimes,
            _heldRevisions,
            _heldMatrixBits,
            animatorBase: 0,
            matrixBase: 0,
            "phase-zero hold");
    }

    public void PrepareFrame(int routeFrameIndex, int evidenceFrameIndex)
    {
        if ((uint)evidenceFrameIndex >= (uint)_recorded.Length)
            throw new ArgumentOutOfRangeException(nameof(evidenceFrameIndex));
        if (_recorded[evidenceFrameIndex])
        {
            throw new InvalidOperationException(
                $"Activation evidence frame {evidenceFrameIndex} was recorded twice.");
        }
        SampleAnimatedCharacter.ApplyBenchmarkAnimationFrame(
            _animators,
            routeFrameIndex);
        RecordCurrentFrame(routeFrameIndex, evidenceFrameIndex);
    }

    public void RecordCurrentFrame(
        int routeFrameIndex,
        int evidenceFrameIndex)
    {
        if ((uint)evidenceFrameIndex >= (uint)_recorded.Length)
            throw new ArgumentOutOfRangeException(nameof(evidenceFrameIndex));
        if (_recorded[evidenceFrameIndex])
        {
            throw new InvalidOperationException(
                $"Activation evidence frame {evidenceFrameIndex} was recorded twice.");
        }
        _routeFrameIndices[evidenceFrameIndex] = routeFrameIndex;
        int animatorBase = checked(evidenceFrameIndex * _animators.Length);
        int matrixBase = checked(
            evidenceFrameIndex * _componentCountPerFrame);
        CaptureCurrent(
            _times,
            _revisions,
            _matrixBits,
            animatorBase,
            matrixBase);
        _recorded[evidenceFrameIndex] = true;
    }

    public void ValidateRecordedFrame(
        int routeFrameIndex,
        int evidenceFrameIndex)
    {
        if ((uint)evidenceFrameIndex >= (uint)_recorded.Length ||
            !_recorded[evidenceFrameIndex] ||
            _routeFrameIndices[evidenceFrameIndex] != routeFrameIndex)
        {
            throw new InvalidOperationException(
                "The held Sponza animation frame was not recorded by the " +
                "measured route.");
        }
        int animatorBase = checked(evidenceFrameIndex * _animators.Length);
        int matrixBase = checked(
            evidenceFrameIndex * _componentCountPerFrame);
        ValidateCurrent(
            _times,
            _revisions,
            _matrixBits,
            animatorBase,
            matrixBase,
            "post-measurement hold");
    }

    public void PrepareWarmupFrame(int routeFrameIndex) =>
        SampleAnimatedCharacter.ApplyBenchmarkAnimationFrame(
            _animators,
            routeFrameIndex);

    private void CaptureCurrent(
        float[] times,
        ulong[] revisions,
        uint[] matrixBits,
        int animatorBase,
        int matrixBase)
    {
        for (int animatorIndex = 0;
             animatorIndex < _animators.Length;
             animatorIndex++)
        {
            Animator animator = _animators[animatorIndex].Animator;
            times[animatorBase + animatorIndex] = animator.TimeSeconds;
            revisions[animatorBase + animatorIndex] = animator.PoseRevision;
            CopyPoseBits(
                animator,
                matrixBits,
                matrixBase + _componentOffsets[animatorIndex]);
        }
    }

    private void ValidateCurrent(
        float[] times,
        ulong[] revisions,
        uint[] matrixBits,
        int animatorBase,
        int matrixBase,
        string role)
    {
        for (int animatorIndex = 0;
             animatorIndex < _animators.Length;
             animatorIndex++)
        {
            (string identity, Animator animator) = _animators[animatorIndex];
            if (animator.Enabled || !animator.IsPlaying || !animator.Looping ||
                BitConverter.SingleToInt32Bits(animator.TimeSeconds) !=
                    BitConverter.SingleToInt32Bits(
                        times[animatorBase + animatorIndex]) ||
                animator.PoseRevision !=
                    revisions[animatorBase + animatorIndex])
            {
                throw new InvalidDataException(
                    $"Sponza animator '{identity}' changed during {role}.");
            }
            ReadOnlySpan<CoreMatrix4x4> matrices =
                animator.CurrentPose.GlobalMatrices;
            int component = matrixBase + _componentOffsets[animatorIndex];
            for (int matrixIndex = 0;
                 matrixIndex < matrices.Length;
                 matrixIndex++)
            {
                CoreMatrix4x4 matrix = matrices[matrixIndex];
                for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    uint actual = BitConverter.SingleToUInt32Bits(
                        matrix[row, column]);
                    if (actual != matrixBits[component++])
                    {
                        throw new InvalidDataException(
                            $"Sponza animator '{identity}' pose changed during " +
                            $"{role}.");
                    }
                }
            }
        }
    }

    private static void CopyPoseBits(
        Animator animator,
        uint[] destination,
        int offset)
    {
        ReadOnlySpan<CoreMatrix4x4> matrices =
            animator.CurrentPose.GlobalMatrices;
        int component = offset;
        for (int matrixIndex = 0;
             matrixIndex < matrices.Length;
             matrixIndex++)
        {
            CoreMatrix4x4 matrix = matrices[matrixIndex];
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                destination[component++] =
                    BitConverter.SingleToUInt32Bits(matrix[row, column]);
            }
        }
    }

    public IReadOnlyList<SampleBenchmarkActivationFrameState> BuildEvidence(
        int frameCount)
    {
        if (frameCount < 0 || frameCount > _recorded.Length)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        var frames = new SampleBenchmarkActivationFrameState[frameCount];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            if (!_recorded[frameIndex])
            {
                throw new InvalidOperationException(
                    $"Activation evidence frame {frameIndex} was not recorded.");
            }
            var states = new SampleBenchmarkActivationAnimatorState[
                _animators.Length];
            int animatorBase = checked(frameIndex * _animators.Length);
            int matrixBase = checked(frameIndex * _componentCountPerFrame);
            for (int animatorIndex = 0;
                 animatorIndex < _animators.Length;
                 animatorIndex++)
            {
                (string identity, Animator animator) = _animators[animatorIndex];
                AnimationClip clip = animator.Clips[0];
                int componentCount =
                    _componentOffsets[animatorIndex + 1] -
                    _componentOffsets[animatorIndex];
                var bits = new uint[componentCount];
                Array.Copy(
                    _matrixBits,
                    matrixBase + _componentOffsets[animatorIndex],
                    bits,
                    0,
                    componentCount);
                states[animatorIndex] =
                    new SampleBenchmarkActivationAnimatorState(
                        identity,
                        clip.Name,
                        clip.DurationSeconds,
                        _times[animatorBase + animatorIndex],
                        _revisions[animatorBase + animatorIndex],
                        animator.Skeleton.Joints.Count,
                        animator.Skins.Count,
                        SampleAnimatedCharacter.CreatePoseHash(bits))
                    {
                        GlobalMatrixComponentBits = Array.AsReadOnly(bits)
                    };
            }
            int routeFrameIndex = _routeFrameIndices[frameIndex];
            string configuration = SampleBenchmarkActivationFrameState
                .CreateConfigurationFingerprint(states);
            frames[frameIndex] = new SampleBenchmarkActivationFrameState(
                SampleBenchmarkActivationFrameState.CurrentSchema,
                routeFrameIndex,
                configuration,
                SampleBenchmarkActivationFrameState.CreateFrameHash(
                    routeFrameIndex,
                    configuration,
                    states),
                Array.AsReadOnly(states));
        }
        return Array.AsReadOnly(frames);
    }
}
