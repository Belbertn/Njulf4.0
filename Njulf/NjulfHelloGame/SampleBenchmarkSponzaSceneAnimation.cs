using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Scene;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

public enum SampleBenchmarkSponzaSceneAnimationMode : byte
{
    Unavailable,
    PhaseZeroHold,
    DirectionalRoute
}

public sealed record SampleBenchmarkSponzaSceneAnimationEvidence(
    string Schema,
    string Fingerprint,
    SampleBenchmarkSponzaSceneAnimationMode Mode,
    bool Passed,
    int SampleCount,
    string ConfigurationFingerprint,
    string SequenceHash,
    string SidecarPath,
    string SidecarSha256,
    IReadOnlyList<string> Failures)
{
    public const string CurrentSchema =
        "njulf-benchmark-sponza-scene-animation/v1";
    public const string UnavailableReason =
        "Sponza scene animation evidence was not evaluated.";

    public static SampleBenchmarkSponzaSceneAnimationEvidence Unavailable {
        get;
    } = new(
        CurrentSchema,
        SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
        SampleBenchmarkSponzaSceneAnimationMode.Unavailable,
        Passed: false,
        SampleCount: 0,
        ConfigurationFingerprint: "unavailable",
        SequenceHash: "unavailable",
        SidecarPath: string.Empty,
        SidecarSha256: string.Empty,
        Failures: [UnavailableReason]);

    public static bool IsCanonicalUnavailable(
        SampleBenchmarkSponzaSceneAnimationEvidence? evidence) =>
        evidence != null &&
        string.Equals(evidence.Schema, CurrentSchema, StringComparison.Ordinal) &&
        string.Equals(
            evidence.Fingerprint,
            SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
            StringComparison.Ordinal) &&
        evidence.Mode == SampleBenchmarkSponzaSceneAnimationMode.Unavailable &&
        !evidence.Passed &&
        evidence.SampleCount == 0 &&
        string.Equals(
            evidence.ConfigurationFingerprint,
            "unavailable",
            StringComparison.Ordinal) &&
        string.Equals(
            evidence.SequenceHash,
            "unavailable",
            StringComparison.Ordinal) &&
        string.IsNullOrEmpty(evidence.SidecarPath) &&
        string.IsNullOrEmpty(evidence.SidecarSha256) &&
        evidence.Failures is { Count: 1 } &&
        string.Equals(
            evidence.Failures[0],
            UnavailableReason,
            StringComparison.Ordinal);

    internal static SampleBenchmarkSponzaSceneAnimationEvidence Failed(
        SampleBenchmarkSponzaSceneAnimationMode mode,
        int sampleCount,
        string reason) => new(
        CurrentSchema,
        SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
        mode,
        Passed: false,
        SampleCount: sampleCount,
        ConfigurationFingerprint: "unavailable",
        SequenceHash: "unavailable",
        SidecarPath: string.Empty,
        SidecarSha256: string.Empty,
        Failures: [reason]);
}

internal sealed record SampleBenchmarkSponzaSceneAnimationBuild(
    SampleBenchmarkSponzaSceneAnimationEvidence Evidence,
    IReadOnlyList<SampleBenchmarkActivationFrameState> Frames);

public static class SampleBenchmarkSponzaSceneAnimationContract
{
    public static readonly Guid JointObjectId =
        Guid.Parse("cccccccc-0000-4000-8000-000000000001");
    public static readonly Guid SurfaceObjectId =
        Guid.Parse("cccccccc-0000-4000-8000-000000000002");
    public const string AssetPath = "Strut.glb";
    public const string JointSubObject = "0";
    public const string SurfaceSubObject = "1";
    public const string JointName = "AnimatedCharacter.Strut.Alpha_Joints";
    public const string SurfaceName = "AnimatedCharacter.Strut.Alpha_Surface";

    public static string Fingerprint { get; } = CreateFingerprint();

    public static SampleBenchmarkSponzaSceneAnimationMode ResolveMode(
        string? activation) =>
        SampleBenchmarkActivation.RequiresDeterministicAnimation(activation)
            ? SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute
            : SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold;

    public static int ResolvePhaseFrameIndex(
        SampleBenchmarkSponzaSceneAnimationMode mode,
        int authoredRouteFrameIndex) => mode switch
        {
            SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold => 0,
            SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute =>
                authoredRouteFrameIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    public static void ValidateAuthoredObjects(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        RenderObject joints = RequireObject(
            scene,
            JointObjectId,
            JointSubObject,
            JointName);
        RenderObject surface = RequireObject(
            scene,
            SurfaceObjectId,
            SurfaceSubObject,
            SurfaceName);
        int strutReferenceCount = scene.RenderObjects.Count(renderObject =>
            string.Equals(
                renderObject.AssetReference?.Path,
                AssetPath,
                StringComparison.Ordinal));
        if (strutReferenceCount != 2)
        {
            throw new InvalidDataException(
                "The controlled Sponza scene must contain exactly the two " +
                "authored Strut.glb subobjects.");
        }
        if (joints is not SkinnedRenderObject jointSkinned ||
            surface is not SkinnedRenderObject surfaceSkinned ||
            jointSkinned.Animator == null ||
            surfaceSkinned.Animator == null ||
            ReferenceEquals(
                jointSkinned.Animator,
                surfaceSkinned.Animator) ||
            jointSkinned.Animator.Clips.Count == 0 ||
            surfaceSkinned.Animator.Clips.Count == 0)
        {
            throw new InvalidDataException(
                "The authored Sponza Strut objects do not expose their exact " +
                "two distinct skinned animator configurations.");
        }
    }

    private static RenderObject RequireObject(
        Scene scene,
        Guid id,
        string subObject,
        string expectedName)
    {
        RenderObject? renderObject = scene.RenderObjects.SingleOrDefault(
            candidate => candidate.Id == id);
        if (renderObject == null ||
            renderObject.AssetReference == null ||
            !string.Equals(
                renderObject.AssetReference.Path,
                AssetPath,
                StringComparison.Ordinal) ||
            !string.Equals(
                renderObject.AssetReference.SubObject,
                subObject,
                StringComparison.Ordinal) ||
            !string.Equals(
                renderObject.Name,
                expectedName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The authored Sponza Strut object {id:D} is missing or changed.");
        }
        return renderObject;
    }

    private static string CreateFingerprint()
    {
        string canonical =
            "njulf-benchmark-sponza-scene-animation/v1|" +
            $"objects={JointObjectId:D}:{JointSubObject}," +
            $"{SurfaceObjectId:D}:{SurfaceSubObject}|" +
            $"names={JointName},{SurfaceName}|asset={AssetPath}|" +
            "animators=two:stable-id-order|" +
            "clip=first:looping|step=1/60|" +
            "ordinary=phase-zero-hold:revision-relative-0|" +
            "directional=route-relative:revision-contiguous|" +
            "pose=raw-global-matrix-bits|";
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    internal static string CreateSequenceHash(
        SampleBenchmarkSponzaSceneAnimationMode mode,
        IReadOnlyList<SampleBenchmarkActivationFrameState> frames,
        string configurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        var canonical = new StringBuilder(
            "njulf-benchmark-sponza-scene-animation-sequence/v1|");
        canonical.Append(Fingerprint).Append('|').Append((int)mode).Append('|')
            .Append(configurationFingerprint).Append('\n');
        for (int index = 0; index < frames.Count; index++)
        {
            canonical.Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(ResolvePhaseFrameIndex(mode, index).ToString(
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(frames[index].FrameHash).Append('\n');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}

internal sealed class SampleBenchmarkSponzaSceneAnimationObserver
{
    private readonly int _expectedSampleCount;
    private readonly SampleBenchmarkSponzaSceneAnimationMode _mode;
    private readonly SampleBenchmarkTrajectoryKind _trajectory;
    private readonly List<SampleBenchmarkActivationFrameState> _qualityFrames;
    private SampleBenchmarkActivationAnimationCapture? _timingCapture;
    private bool _qualityTopologyValidated;
    private bool _timingPhaseZeroInitialized;
    private bool _qualityPhaseZeroInitialized;
    private readonly List<string> _failures = new();

    public SampleBenchmarkSponzaSceneAnimationObserver(
        int expectedSampleCount,
        string? activation,
        SampleBenchmarkTrajectoryKind trajectory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedSampleCount, 1);
        _expectedSampleCount = expectedSampleCount;
        _mode = SampleBenchmarkSponzaSceneAnimationContract.ResolveMode(
            activation);
        _trajectory = trajectory;
        _qualityFrames = new List<SampleBenchmarkActivationFrameState>(
            expectedSampleCount);
    }

    public SampleBenchmarkSponzaSceneAnimationMode Mode => _mode;

    public void PrepareTimingFrame(
        Scene scene,
        int authoredRouteFrameIndex,
        bool measurementFrame,
        bool hold)
    {
        if (_timingCapture == null)
        {
            SampleBenchmarkSponzaSceneAnimationContract.ValidateAuthoredObjects(
                scene);
            _timingCapture = new SampleBenchmarkActivationAnimationCapture(
                scene,
                _expectedSampleCount);
        }
        if (hold)
        {
            int heldPhase = SampleBenchmarkSponzaSceneAnimationContract
                .ResolvePhaseFrameIndex(_mode, _expectedSampleCount - 1);
            _timingCapture.ValidateRecordedFrame(
                heldPhase,
                _expectedSampleCount - 1);
            return;
        }

        int phase = SampleBenchmarkSponzaSceneAnimationContract
            .ResolvePhaseFrameIndex(_mode, authoredRouteFrameIndex);
        bool stationaryDirectionalWarmup =
            _mode ==
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute &&
            !SampleBenchmarkTrajectory.IsMoving(_trajectory) &&
            !measurementFrame;
        if (_mode == SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold ||
            stationaryDirectionalWarmup)
        {
            if (!_timingPhaseZeroInitialized)
            {
                _timingCapture.InitializePhaseZeroHold();
                _timingPhaseZeroInitialized = true;
            }
            else
            {
                _timingCapture.ValidatePhaseZeroHold();
            }
        }
        else if (phase == 0 && _timingPhaseZeroInitialized)
        {
            _timingCapture.ValidatePhaseZeroHold();
        }
        else
        {
            _timingCapture.PrepareWarmupFrame(phase);
        }
    }

    public void RecordTimingFrame(int routeFrameIndex, int evidenceFrameIndex)
    {
        if (_timingCapture == null)
        {
            throw new InvalidOperationException(
                "Sponza animation timing evidence was not prepared before Draw.");
        }
        int phase = SampleBenchmarkSponzaSceneAnimationContract
            .ResolvePhaseFrameIndex(_mode, routeFrameIndex);
        _timingCapture.RecordCurrentFrame(phase, evidenceFrameIndex);
    }

    public void RecordFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!_failures.Contains(reason, StringComparer.Ordinal))
            _failures.Add(reason);
    }

    public SampleBenchmarkActivationFrameState PrepareQualityFrame(
        Scene scene,
        int authoredRouteFrameIndex,
        int? evidenceFrameIndex,
        bool hold)
    {
        if (!_qualityTopologyValidated)
        {
            SampleBenchmarkSponzaSceneAnimationContract.ValidateAuthoredObjects(
                scene);
            _qualityTopologyValidated = true;
        }
        int phase = SampleBenchmarkSponzaSceneAnimationContract
            .ResolvePhaseFrameIndex(_mode, authoredRouteFrameIndex);
        SampleBenchmarkActivationFrameState state;
        if (_mode == SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold)
        {
            if (!_qualityPhaseZeroInitialized)
            {
                state = SampleAnimatedCharacter.PrepareBenchmarkActivationFrame(
                    scene,
                    phase);
                _qualityPhaseZeroInitialized = true;
            }
            else
            {
                state = SampleAnimatedCharacter.CaptureBenchmarkActivationFrame(
                    scene,
                    phase);
            }
        }
        else
        {
            state = hold
                ? SampleAnimatedCharacter.CaptureBenchmarkActivationFrame(
                    scene,
                    phase)
                : SampleAnimatedCharacter.PrepareBenchmarkActivationFrame(
                    scene,
                    phase);
        }
        if (evidenceFrameIndex.HasValue)
        {
            if (evidenceFrameIndex.Value != _qualityFrames.Count)
            {
                throw new InvalidOperationException(
                    "Sponza scene animation evidence was reordered.");
            }
            _qualityFrames.Add(state);
        }
        else if (hold)
        {
            if (_qualityFrames.Count != _expectedSampleCount)
            {
                throw new InvalidDataException(
                    "Sponza animation hold began before the complete authored " +
                    "route was recorded.");
            }
            RequireFrameEqual(
                _qualityFrames[^1],
                state,
                "quality readback hold");
        }
        return state;
    }

    public SampleBenchmarkSponzaSceneAnimationBuild BuildTiming(
        string sidecarPath) =>
        Build(
            _timingCapture?.BuildEvidence(_expectedSampleCount) ??
                Array.Empty<SampleBenchmarkActivationFrameState>(),
            sidecarPath);

    public SampleBenchmarkSponzaSceneAnimationBuild BuildQuality(
        string sidecarPath) =>
        Build(Array.AsReadOnly(_qualityFrames.ToArray()), sidecarPath);

    private SampleBenchmarkSponzaSceneAnimationBuild Build(
        IReadOnlyList<SampleBenchmarkActivationFrameState> frames,
        string sidecarPath)
    {
        var failures = new List<string>(_failures);
        if (frames.Count != _expectedSampleCount)
        {
            failures.Add(
                $"Sponza animation contains {frames.Count}/" +
                $"{_expectedSampleCount} evidence frames.");
        }
        string configuration = frames.Count == 0
            ? "unavailable"
            : frames[0].ConfigurationFingerprint;
        ulong[] firstRevisions = frames.Count == 0
            ? Array.Empty<ulong>()
            : frames[0].Animators.Select(static animator =>
                animator.PoseRevision).ToArray();
        for (int index = 0; index < frames.Count; index++)
        {
            SampleBenchmarkActivationFrameState frame = frames[index];
            int expectedPhase =
                SampleBenchmarkSponzaSceneAnimationContract
                    .ResolvePhaseFrameIndex(_mode, index);
            try
            {
                SampleBenchmarkActivationFrameState.ValidateCanonical(
                    frame,
                    expectedPhase);
                if (!string.Equals(
                        frame.ConfigurationFingerprint,
                        configuration,
                        StringComparison.Ordinal) ||
                    frame.Animators.Count != firstRevisions.Length)
                {
                    throw new InvalidDataException(
                        "Sponza animator topology changed inside the route.");
                }
                for (int animatorIndex = 0;
                     animatorIndex < frame.Animators.Count;
                     animatorIndex++)
                {
                    SampleBenchmarkActivationAnimatorState animator =
                        frame.Animators[animatorIndex];
                    float expectedTime = NormalizeTime(
                        expectedPhase * HelloGame.BenchmarkSimulationDeltaSeconds,
                        animator.ClipDurationSeconds);
                    if (BitConverter.SingleToInt32Bits(animator.TimeSeconds) !=
                            BitConverter.SingleToInt32Bits(expectedTime) ||
                        animator.PoseRevision != (_mode ==
                            SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold
                                ? firstRevisions[animatorIndex]
                                : checked(
                                    firstRevisions[animatorIndex] +
                                    (ulong)index)))
                    {
                        throw new InvalidDataException(
                            "Sponza animator phase or relative revision changed.");
                    }
                }
                if (_mode ==
                        SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold &&
                    !string.Equals(
                        frame.FrameHash,
                        frames[0].FrameHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The phase-zero Sponza pose changed inside the route.");
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or OverflowException)
            {
                failures.Add(
                    $"Sponza animation frame {index} failed: " +
                    exception.Message);
            }
        }
        string sequence =
            SampleBenchmarkSponzaSceneAnimationContract.CreateSequenceHash(
                _mode,
                frames,
                configuration);
        string canonicalSidecarPath = string.Empty;
        string sidecarSha256 = string.Empty;
        if (failures.Count == 0)
        {
            try
            {
                canonicalSidecarPath = Path.GetFullPath(sidecarPath);
                SampleEvidenceFileContent published =
                    SampleBenchmarkSponzaSceneAnimationSidecar.Write(
                    canonicalSidecarPath,
                    _mode,
                    frames,
                    configuration,
                    sequence);
                sidecarSha256 = published.Sha256;
                _ = SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                    canonicalSidecarPath,
                    sidecarSha256,
                    _mode,
                    frames.Count,
                    configuration,
                    sequence);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or
                    InvalidDataException or UnauthorizedAccessException or
                    OverflowException)
            {
                failures.Add(
                    "Sponza animation sidecar publication failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                sidecarSha256 = string.Empty;
            }
        }
        var evidence = new SampleBenchmarkSponzaSceneAnimationEvidence(
            SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema,
            SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
            _mode,
            failures.Count == 0,
            frames.Count,
            configuration,
            sequence,
            canonicalSidecarPath,
            sidecarSha256,
            Array.AsReadOnly(failures.ToArray()));
        return new SampleBenchmarkSponzaSceneAnimationBuild(evidence, frames);
    }

    private static float NormalizeTime(float time, float duration)
    {
        float wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }

    private static void RequireFrameEqual(
        SampleBenchmarkActivationFrameState expected,
        SampleBenchmarkActivationFrameState actual,
        string role)
    {
        if (expected.RouteFrameIndex != actual.RouteFrameIndex ||
            !string.Equals(
                expected.ConfigurationFingerprint,
                actual.ConfigurationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                expected.FrameHash,
                actual.FrameHash,
                StringComparison.Ordinal) ||
            expected.Animators.Count != actual.Animators.Count)
        {
            throw new InvalidDataException(
                $"Sponza animation changed during {role}.");
        }
        for (int index = 0; index < expected.Animators.Count; index++)
        {
            SampleBenchmarkActivationAnimatorState left =
                expected.Animators[index];
            SampleBenchmarkActivationAnimatorState right =
                actual.Animators[index];
            if (!string.Equals(left.Identity, right.Identity, StringComparison.Ordinal) ||
                !string.Equals(left.ClipName, right.ClipName, StringComparison.Ordinal) ||
                BitConverter.SingleToInt32Bits(left.ClipDurationSeconds) !=
                    BitConverter.SingleToInt32Bits(right.ClipDurationSeconds) ||
                BitConverter.SingleToInt32Bits(left.TimeSeconds) !=
                    BitConverter.SingleToInt32Bits(right.TimeSeconds) ||
                left.PoseRevision != right.PoseRevision ||
                left.JointCount != right.JointCount ||
                left.SkinCount != right.SkinCount ||
                !string.Equals(left.PoseHash, right.PoseHash, StringComparison.Ordinal) ||
                !left.GlobalMatrixComponentBits.SequenceEqual(
                    right.GlobalMatrixComponentBits))
            {
                throw new InvalidDataException(
                    $"Sponza animator '{left.Identity}' changed during {role}.");
            }
        }
    }
}
