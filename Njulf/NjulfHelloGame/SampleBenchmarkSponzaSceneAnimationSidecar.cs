using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NjulfHelloGame;

internal sealed record SampleBenchmarkSponzaSceneAnimationSidecarContent(
    string Path,
    string Sha256,
    SampleBenchmarkSponzaSceneAnimationMode Mode,
    string ConfigurationFingerprint,
    string SequenceHash,
    IReadOnlyList<SampleBenchmarkActivationFrameState> Frames);

/// <summary>
/// Compact, bounded, little-endian evidence for the authored Sponza Strut
/// animation. Phase-zero workloads store one pose plus an exact repeat count;
/// directional workloads store the complete route-relative pose sequence.
/// Absolute process-lifetime pose revisions are deliberately normalized away.
/// </summary>
internal static class SampleBenchmarkSponzaSceneAnimationSidecar
{
    private const int Version = 1;
    private const int MaximumStringBytes = 4096;
    private const int MaximumFrameCount = 960;
    private const int RequiredAnimatorCount = 2;
    private const long MaximumSidecarBytes = 16L * 1024L * 1024L;
    private static readonly byte[] Magic = "NJSPANI1"u8.ToArray();

    public static SampleEvidenceFileContent Write(
        string path,
        SampleBenchmarkSponzaSceneAnimationMode mode,
        IReadOnlyList<SampleBenchmarkActivationFrameState> frames,
        string configurationFingerprint,
        string sequenceHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(frames);
        RequireMode(mode);
        RequireSha256Identity(
            configurationFingerprint,
            "Sponza animation configuration fingerprint");
        RequireSha256Identity(sequenceHash, "Sponza animation sequence hash");
        if (frames.Count is < 1 or > MaximumFrameCount)
        {
            throw new InvalidDataException(
                $"Sponza animation sidecar frame count {frames.Count} is out " +
                "of range.");
        }

        int storedFrameCount = mode ==
            SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold
                ? 1
                : frames.Count;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteString(
                writer,
                SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema);
            WriteString(
                writer,
                SampleBenchmarkSponzaSceneAnimationContract.Fingerprint);
            writer.Write((byte)mode);
            writer.Write(frames.Count);
            writer.Write(storedFrameCount);
            WriteString(writer, configurationFingerprint);
            WriteString(writer, sequenceHash);

            SampleBenchmarkActivationFrameState first = frames[0];
            if (first.Animators.Count != RequiredAnimatorCount)
            {
                throw new InvalidDataException(
                    "The Sponza animation sidecar requires exactly the two " +
                    "authored Strut animators.");
            }
            writer.Write(first.Animators.Count);
            foreach (SampleBenchmarkActivationAnimatorState animator in
                     first.Animators)
            {
                WriteString(writer, animator.Identity);
                WriteString(writer, animator.ClipName);
                writer.Write(BitConverter.SingleToInt32Bits(
                    animator.ClipDurationSeconds));
                writer.Write(animator.JointCount);
                writer.Write(animator.SkinCount);
                writer.Write(animator.GlobalMatrixComponentBits.Count);
            }

            ulong[] firstRevisions = first.Animators
                .Select(static animator => animator.PoseRevision)
                .ToArray();
            for (int frameIndex = 0;
                 frameIndex < storedFrameCount;
                 frameIndex++)
            {
                SampleBenchmarkActivationFrameState frame = frames[frameIndex];
                writer.Write(frame.RouteFrameIndex);
                if (frame.Animators.Count != first.Animators.Count)
                {
                    throw new InvalidDataException(
                        "Sponza animation topology changed while writing its " +
                        "sidecar.");
                }
                for (int animatorIndex = 0;
                     animatorIndex < frame.Animators.Count;
                     animatorIndex++)
                {
                    SampleBenchmarkActivationAnimatorState animator =
                        frame.Animators[animatorIndex];
                    SampleBenchmarkActivationAnimatorState topology =
                        first.Animators[animatorIndex];
                    RequireTopologyEqual(topology, animator);
                    writer.Write(BitConverter.SingleToInt32Bits(
                        animator.TimeSeconds));
                    writer.Write(checked(
                        animator.PoseRevision - firstRevisions[animatorIndex]));
                    foreach (uint bits in animator.GlobalMatrixComponentBits)
                        writer.Write(bits);
                }
            }
            writer.Flush();
        }

        if (stream.Length <= 0 || stream.Length > MaximumSidecarBytes)
        {
            throw new InvalidDataException(
                $"Sponza animation sidecar contains {stream.Length} bytes; " +
                $"the bounded limit is {MaximumSidecarBytes} bytes.");
        }
        return WriteAtomicNew(
            Path.GetFullPath(path),
            stream.ToArray(),
            "Sponza scene-animation sidecar");
    }

    public static SampleBenchmarkSponzaSceneAnimationSidecarContent Read(
        string path,
        string expectedSha256,
        SampleBenchmarkSponzaSceneAnimationMode expectedMode,
        int expectedFrameCount,
        string expectedConfigurationFingerprint,
        string expectedSequenceHash)
    {
        RequireMode(expectedMode);
        if (expectedFrameCount is < 1 or > MaximumFrameCount)
            throw new ArgumentOutOfRangeException(nameof(expectedFrameCount));
        RequireSha256(expectedSha256, "Sponza animation sidecar hash");
        RequireSha256Identity(
            expectedConfigurationFingerprint,
            "Sponza animation configuration fingerprint");
        RequireSha256Identity(
            expectedSequenceHash,
            "Sponza animation sequence hash");

        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            MaximumSidecarBytes,
            "Sponza scene-animation sidecar");
        RequireExact(
            evidence.Sha256,
            expectedSha256,
            "Sponza animation sidecar hash");
        using var stream = new MemoryStream(evidence.Bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte[] magic = ReadBytesExactly(reader, Magic.Length, "magic");
        if (!magic.AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != Version)
            throw new InvalidDataException("Sponza animation sidecar header changed.");
        RequireExact(
            ReadString(reader, "schema"),
            SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema,
            "Sponza animation sidecar schema");
        RequireExact(
            ReadString(reader, "contract fingerprint"),
            SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
            "Sponza animation contract fingerprint");

        var mode = (SampleBenchmarkSponzaSceneAnimationMode)reader.ReadByte();
        RequireMode(mode);
        if (mode != expectedMode)
            throw new InvalidDataException("Sponza animation sidecar mode changed.");
        int frameCount = reader.ReadInt32();
        int storedFrameCount = reader.ReadInt32();
        int expectedStoredFrameCount = mode ==
            SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold
                ? 1
                : frameCount;
        if (frameCount != expectedFrameCount ||
            storedFrameCount != expectedStoredFrameCount)
        {
            throw new InvalidDataException(
                "Sponza animation sidecar frame cardinality changed.");
        }

        string configuration = ReadString(reader, "configuration fingerprint");
        string sequence = ReadString(reader, "sequence hash");
        RequireExact(
            configuration,
            expectedConfigurationFingerprint,
            "Sponza animation configuration fingerprint");
        RequireExact(
            sequence,
            expectedSequenceHash,
            "Sponza animation sequence hash");

        int animatorCount = reader.ReadInt32();
        if (animatorCount != RequiredAnimatorCount)
        {
            throw new InvalidDataException(
                "Sponza animation sidecar does not contain exactly two animators.");
        }
        var topology = new AnimatorTopology[animatorCount];
        for (int animatorIndex = 0;
             animatorIndex < animatorCount;
             animatorIndex++)
        {
            string identity = ReadString(reader, "animator identity");
            string clipName = ReadString(reader, "clip name");
            float duration = BitConverter.Int32BitsToSingle(reader.ReadInt32());
            int jointCount = reader.ReadInt32();
            int skinCount = reader.ReadInt32();
            int componentCount = reader.ReadInt32();
            int expectedComponents;
            try
            {
                expectedComponents = checked(jointCount * 16);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Sponza animation sidecar matrix extent overflowed.",
                    exception);
            }
            if (string.IsNullOrWhiteSpace(identity) ||
                string.IsNullOrWhiteSpace(clipName) ||
                !float.IsFinite(duration) || duration <= 0f ||
                jointCount <= 0 || skinCount <= 0 ||
                componentCount != expectedComponents)
            {
                throw new InvalidDataException(
                    "Sponza animation sidecar animator topology is invalid.");
            }
            topology[animatorIndex] = new AnimatorTopology(
                identity,
                clipName,
                duration,
                jointCount,
                skinCount,
                componentCount);
        }
        if (!string.Equals(
                topology[0].Identity,
                SampleBenchmarkSponzaSceneAnimationContract.JointName,
                StringComparison.Ordinal) ||
            !string.Equals(
                topology[1].Identity,
                SampleBenchmarkSponzaSceneAnimationContract.SurfaceName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Sponza animation sidecar animator identities or ordering " +
                "changed.");
        }
        if (!string.Equals(
                SampleBenchmarkActivationFrameState
                    .CreateConfigurationFingerprint(
                        topology.Select(static item => item.CreateState(
                            0f,
                            0,
                            new uint[item.ComponentCount])).ToArray()),
                configuration,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Sponza animation sidecar topology does not match its " +
                "configuration fingerprint.");
        }

        var storedFrames = new SampleBenchmarkActivationFrameState[
            storedFrameCount];
        for (int frameIndex = 0;
             frameIndex < storedFrameCount;
             frameIndex++)
        {
            int routeFrameIndex = reader.ReadInt32();
            int expectedRouteFrameIndex = mode ==
                SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold
                    ? 0
                    : frameIndex;
            if (routeFrameIndex != expectedRouteFrameIndex)
            {
                throw new InvalidDataException(
                    "Sponza animation sidecar route frames are reordered.");
            }
            var animators = new SampleBenchmarkActivationAnimatorState[
                animatorCount];
            for (int animatorIndex = 0;
                 animatorIndex < animatorCount;
                 animatorIndex++)
            {
                AnimatorTopology animator = topology[animatorIndex];
                float time = BitConverter.Int32BitsToSingle(reader.ReadInt32());
                ulong relativeRevision = reader.ReadUInt64();
                uint[] bits = new uint[animator.ComponentCount];
                for (int component = 0; component < bits.Length; component++)
                    bits[component] = reader.ReadUInt32();
                animators[animatorIndex] = animator.CreateState(
                    time,
                    relativeRevision,
                    bits);
            }
            storedFrames[frameIndex] = CreateFrame(
                routeFrameIndex,
                configuration,
                animators);
            SampleBenchmarkActivationFrameState.ValidateCanonical(
                storedFrames[frameIndex],
                expectedRouteFrameIndex);
            for (int animatorIndex = 0;
                 animatorIndex < animators.Length;
                 animatorIndex++)
            {
                SampleBenchmarkActivationAnimatorState animator =
                    animators[animatorIndex];
                float expectedTime = NormalizeTime(
                    expectedRouteFrameIndex *
                    HelloGame.BenchmarkSimulationDeltaSeconds,
                    animator.ClipDurationSeconds);
                ulong expectedRelativeRevision = mode ==
                    SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold
                        ? 0UL
                        : (ulong)frameIndex;
                if (BitConverter.SingleToInt32Bits(animator.TimeSeconds) !=
                        BitConverter.SingleToInt32Bits(expectedTime) ||
                    animator.PoseRevision != expectedRelativeRevision)
                {
                    throw new InvalidDataException(
                        "Sponza animation sidecar phase or normalized pose " +
                        "revision changed.");
                }
            }
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "Sponza animation sidecar contains trailing bytes.");
        }

        SampleBenchmarkActivationFrameState[] frames;
        if (mode == SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold)
        {
            frames = Enumerable.Repeat(storedFrames[0], frameCount).ToArray();
        }
        else
        {
            frames = storedFrames;
        }
        string recomputed =
            SampleBenchmarkSponzaSceneAnimationContract.CreateSequenceHash(
                mode,
                frames,
                configuration);
        RequireExact(
            recomputed,
            expectedSequenceHash,
            "Sponza animation recomputed sequence hash");
        return new SampleBenchmarkSponzaSceneAnimationSidecarContent(
            evidence.Path,
            evidence.Sha256,
            mode,
            configuration,
            recomputed,
            Array.AsReadOnly(frames));
    }

    private static SampleBenchmarkActivationFrameState CreateFrame(
        int routeFrameIndex,
        string configuration,
        SampleBenchmarkActivationAnimatorState[] animators) => new(
        SampleBenchmarkActivationFrameState.CurrentSchema,
        routeFrameIndex,
        configuration,
        SampleBenchmarkActivationFrameState.CreateFrameHash(
            routeFrameIndex,
            configuration,
            animators),
        Array.AsReadOnly(animators));

    private static float NormalizeTime(float time, float duration)
    {
        float wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }

    private static SampleEvidenceFileContent WriteAtomicNew(
        string path,
        byte[] payload,
        string role)
    {
        if (File.Exists(path))
        {
            throw new IOException(
                $"{role} '{path}' already exists; use a fresh run directory.");
        }
        string directory = Path.GetDirectoryName(path) ??
            throw new IOException($"Could not resolve a directory for '{path}'.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
            SampleEvidenceFileContent published = SampleEvidenceFileIo.Read(
                path,
                MaximumSidecarBytes,
                role);
            if (!published.Bytes.AsSpan().SequenceEqual(payload))
            {
                throw new IOException(
                    $"{role} '{path}' differs from the committed payload.");
            }
            return published;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void RequireTopologyEqual(
        SampleBenchmarkActivationAnimatorState expected,
        SampleBenchmarkActivationAnimatorState actual)
    {
        if (!string.Equals(expected.Identity, actual.Identity, StringComparison.Ordinal) ||
            !string.Equals(expected.ClipName, actual.ClipName, StringComparison.Ordinal) ||
            BitConverter.SingleToInt32Bits(expected.ClipDurationSeconds) !=
                BitConverter.SingleToInt32Bits(actual.ClipDurationSeconds) ||
            expected.JointCount != actual.JointCount ||
            expected.SkinCount != actual.SkinCount ||
            expected.GlobalMatrixComponentBits.Count !=
                actual.GlobalMatrixComponentBits.Count)
        {
            throw new InvalidDataException(
                "Sponza animation sidecar animator topology changed.");
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new InvalidDataException("Sponza animation sidecar string is too long.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, string role)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new InvalidDataException(
                $"Sponza animation sidecar {role} length is invalid.");
        }
        byte[] bytes = ReadBytesExactly(reader, length, role);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Sponza animation sidecar {role} is not canonical UTF-8.",
                exception);
        }
    }

    private static byte[] ReadBytesExactly(
        BinaryReader reader,
        int count,
        string role)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new InvalidDataException(
                $"Sponza animation sidecar ended inside {role}.");
        }
        return bytes;
    }

    private static void RequireMode(
        SampleBenchmarkSponzaSceneAnimationMode mode)
    {
        if (mode is not (
                SampleBenchmarkSponzaSceneAnimationMode.PhaseZeroHold or
                SampleBenchmarkSponzaSceneAnimationMode.DirectionalRoute))
        {
            throw new InvalidDataException(
                "Sponza animation sidecar mode is unavailable or unknown.");
        }
    }

    private static void RequireSha256Identity(string value, string role)
    {
        if (!value.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException($"{role} is not a SHA-256 identity.");
        RequireSha256(value[7..], role);
    }

    private static void RequireSha256(string value, string role)
    {
        if (value.Length != 64 ||
            value.Any(static character =>
                !((character is >= '0' and <= '9') ||
                  (character is >= 'a' and <= 'f'))))
        {
            throw new InvalidDataException($"{role} is not canonical SHA-256.");
        }
    }

    private static void RequireExact(string actual, string expected, string role)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{role} changed.");
    }

    private readonly record struct AnimatorTopology(
        string Identity,
        string ClipName,
        float ClipDurationSeconds,
        int JointCount,
        int SkinCount,
        int ComponentCount)
    {
        public SampleBenchmarkActivationAnimatorState CreateState(
            float time,
            ulong relativeRevision,
            uint[] bits) => new(
            Identity,
            ClipName,
            ClipDurationSeconds,
            time,
            relativeRevision,
            JointCount,
            SkinCount,
            SampleAnimatedCharacter.CreatePoseHash(bits))
        {
            GlobalMatrixComponentBits = Array.AsReadOnly(bits)
        };
    }
}
