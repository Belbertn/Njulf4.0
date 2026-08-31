using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiFeedbackPartialReductionTests
{
    private const int WorkgroupSize = 64;
    private const int ReductionWordCount = 26;
    private const int FeedbackHeaderWordCount = 64;
    private const int FeedbackWordCount = 1024;
    private const int MaximumLaneCount = 896;
    private const uint ReceiverCoverageMask = 0x00ff_ffffu;
    private const uint ReceiverRoleMask = 0x0700_0000u;
    private const uint ReceiverFallbackMask = 0x0600_0000u;
    private const uint ReceiverConsumerMask = 0xf800_0000u;

    [TestCase(1, 1u)]
    [TestCase(4095, 1u)]
    [TestCase(4096, 1u)]
    [TestCase(4097, 2u)]
    [TestCase(15368, 4u)]
    [TestCase(32768, 8u)]
    public void PartialGroupCount_IsBoundedAndMatchesTheRuntimeHelper(
        int probeCount,
        uint expectedGroupCount)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiSchedulerCommitPass
                    .CalculateFeedbackPartialGroupCount(probeCount),
                Is.EqualTo(expectedGroupCount));
            Assert.That(expectedGroupCount,
                Is.LessThanOrEqualTo(
                    SimpleDdgiSchedulerCommitPass
                        .MaximumFeedbackPartialGroupCount));
            Assert.That(
                SimpleDdgiSchedulerCommitPass
                    .FeedbackProbesPerPartialGroup,
                Is.EqualTo(4096u));
        });
    }

    [TestCase(1)]
    [TestCase(4095)]
    [TestCase(4096)]
    [TestCase(4097)]
    [TestCase(15368)]
    [TestCase(32768)]
    public void GlobalInvocationPartition_CoversEveryProbeOnceWithoutOverlap(
        int probeCount)
    {
        int groupCount = checked((int)SimpleDdgiSchedulerCommitPass
            .CalculateFeedbackPartialGroupCount(probeCount));
        int globalInvocationCount = checked(groupCount * WorkgroupSize);
        var visits = new byte[probeCount];
        var probesPerGroup = new int[groupCount];

        for (int group = 0; group < groupCount; group++)
        {
            for (int lane = 0; lane < WorkgroupSize; lane++)
            {
                int globalInvocationIndex = group * WorkgroupSize + lane;
                for (int probeIndex = globalInvocationIndex;
                     probeIndex < probeCount;
                     probeIndex += globalInvocationCount)
                {
                    if (visits[probeIndex] != 0)
                    {
                        Assert.Fail(
                            $"Probe {probeIndex} was visited more than once " +
                            $"for {probeCount} probes/{groupCount} groups.");
                    }
                    visits[probeIndex]++;
                    probesPerGroup[group]++;
                }
            }
        }

        for (int probeIndex = 0; probeIndex < visits.Length; probeIndex++)
        {
            if (visits[probeIndex] != 1)
            {
                Assert.Fail(
                    $"Probe {probeIndex} received {visits[probeIndex]} visits " +
                    $"for {probeCount} probes/{groupCount} groups.");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(probesPerGroup.Sum(), Is.EqualTo(probeCount));
            Assert.That(probesPerGroup,
                Has.All.LessThanOrEqualTo(4096));
        });
    }

    [TestCase(1)]
    [TestCase(4095)]
    [TestCase(4096)]
    [TestCase(4097)]
    [TestCase(15368)]
    [TestCase(32768)]
    public void TwoStageMirror_MatchesSingleGroupForSumMaxOrSparseAndReceiverBanks(
        int probeCount)
    {
        uint frameIndex = (uint)(probeCount & 1);
        int currentBank = checked((int)(frameIndex & 1u));
        int previousBank = 1 - currentBank;
        ReceiverRecord[][] sourceBanks = CreateReceiverBanks(probeCount);
        ReceiverRecord[][] legacyBanks = CloneBanks(sourceBanks);
        ReceiverRecord[][] twoStageBanks = CloneBanks(sourceBanks);

        uint[] legacy = RunSingleGroupMirror(
            probeCount,
            previousBank,
            legacyBanks,
            receiverAvailable: true);
        uint[] twoStage = RunTwoStageMirror(
            probeCount,
            previousBank,
            twoStageBanks,
            receiverAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(twoStage, Is.EqualTo(legacy),
                "sum, maximum, and OR reductions must remain bit-exact");
            Assert.That(twoStage[0], Is.GreaterThan(0u));
            Assert.That(twoStage[10], Is.GreaterThan(0u));
            Assert.That(twoStage[25], Is.Not.EqualTo(0u));
            Assert.That(legacyBanks[previousBank],
                Has.All.EqualTo(default(ReceiverRecord)),
                "the legacy traversal clears every virtual receiver slot");
            Assert.That(twoStageBanks[previousBank],
                Has.All.EqualTo(default(ReceiverRecord)),
                "the partial traversal must clear resident and nonresident slots exactly once");
            Assert.That(twoStageBanks[currentBank],
                Is.EqualTo(sourceBanks[currentBank]),
                "the current receiver bank must remain untouched");
            Assert.That(legacyBanks, Is.EqualTo(twoStageBanks));
        });
    }

    [Test]
    public void ReceiverUnavailable_LeavesBothBanksUntouchedAndContributesNoReceiverFields()
    {
        const int probeCount = 4097;
        ReceiverRecord[][] sourceBanks = CreateReceiverBanks(probeCount);
        ReceiverRecord[][] legacyBanks = CloneBanks(sourceBanks);
        ReceiverRecord[][] twoStageBanks = CloneBanks(sourceBanks);

        uint[] legacy = RunSingleGroupMirror(
            probeCount,
            previousBank: 1,
            legacyBanks,
            receiverAvailable: false);
        uint[] twoStage = RunTwoStageMirror(
            probeCount,
            previousBank: 1,
            twoStageBanks,
            receiverAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(twoStage, Is.EqualTo(legacy));
            Assert.That(twoStage[22], Is.Zero);
            Assert.That(twoStage[23], Is.Zero);
            Assert.That(twoStage[24], Is.Zero);
            Assert.That(twoStage[25], Is.Zero);
            Assert.That(legacyBanks, Is.EqualTo(sourceBanks));
            Assert.That(twoStageBanks, Is.EqualTo(sourceBanks));
        });
    }

    [Test]
    public void FinalReductionMirror_UsesModuloSumMaximumAndBitwiseOrFields()
    {
        var first = new uint[ReductionWordCount];
        var second = new uint[ReductionWordCount];
        for (int field = 0; field <= 9; field++)
        {
            first[field] = uint.MaxValue - 3u;
            second[field] = 7u;
        }
        for (int field = 10; field <= 14; field++)
        {
            first[field] = (uint)(field * 3);
            second[field] = (uint)(field * 5);
        }
        for (int field = 15; field <= 24; field++)
        {
            first[field] = uint.MaxValue - 3u;
            second[field] = 7u;
        }
        first[25] = 0x8800_0000u;
        second[25] = 0x5000_0000u;

        var actual = new uint[ReductionWordCount];
        MergeReduction(actual, first);
        MergeReduction(actual, second);

        Assert.Multiple(() =>
        {
            for (int field = 0; field <= 9; field++)
                Assert.That(actual[field], Is.EqualTo(3u));
            for (int field = 10; field <= 14; field++)
                Assert.That(actual[field], Is.EqualTo((uint)(field * 5)));
            for (int field = 15; field <= 24; field++)
                Assert.That(actual[field], Is.EqualTo(3u));
            Assert.That(actual[25], Is.EqualTo(0xd800_0000u));
        });
    }

    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(4095, false)]
    [TestCase(4095, true)]
    [TestCase(4096, false)]
    [TestCase(4096, true)]
    [TestCase(4097, false)]
    [TestCase(4097, true)]
    [TestCase(15368, false)]
    [TestCase(15368, true)]
    [TestCase(32768, false)]
    [TestCase(32768, true)]
    public void FeedbackScratchRestoration_IsBitExactIncludingCertifiedQuiescence(
        int probeCount,
        bool certifiedQuiesced)
    {
        int groupCount = checked((int)SimpleDdgiSchedulerCommitPass
            .CalculateFeedbackPartialGroupCount(probeCount));
        uint[] originalFeedback = Enumerable.Range(0, FeedbackWordCount)
            .Select(static word => unchecked(0x9e37_79b9u * (uint)(word + 1)))
            .ToArray();
        uint[] laneCursors = new uint[MaximumLaneCount];
        for (int lane = 0; lane < laneCursors.Length; lane++)
        {
            laneCursors[lane] = certifiedQuiesced
                ? originalFeedback[FeedbackHeaderWordCount + lane]
                : unchecked(0x7f4a_7c15u ^ (uint)(lane * 17 + 3));
        }

        uint[] expected = (uint[])originalFeedback.Clone();
        PublishFinalHeader(expected);
        if (!certifiedQuiesced)
            laneCursors.CopyTo(expected, FeedbackHeaderWordCount);

        uint[] actual = (uint[])originalFeedback.Clone();
        int partialWordCount = groupCount * ReductionWordCount;
        for (int word = 0; word < partialWordCount; word++)
            actual[word] = unchecked(0xa5a5_0000u + (uint)word);
        PublishFinalHeader(actual);
        if (!certifiedQuiesced)
        {
            laneCursors.CopyTo(actual, FeedbackHeaderWordCount);
        }
        else
        {
            int overwrittenCursorCount = Math.Max(
                0,
                partialWordCount - FeedbackHeaderWordCount);
            Array.Copy(
                laneCursors,
                0,
                actual,
                FeedbackHeaderWordCount,
                overwrittenCursorCount);
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected),
                "temporary partial records must not change any published feedback ABI word");
            Assert.That(
                Math.Max(0, partialWordCount - FeedbackHeaderWordCount),
                Is.EqualTo(groupCount <= 2
                    ? 0
                    : groupCount * ReductionWordCount -
                        FeedbackHeaderWordCount));
        });
    }

    [Test]
    public void ShaderAndPassSource_KeepTheTwoStagesOrderedAndReceiverClearingIsolated()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string renderingDirectory = FindRepoDirectory("Njulf.Rendering");
        string shader = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "ddgi_simple_schedule_feedback.comp"));
        string pass = File.ReadAllText(Path.Combine(
            renderingDirectory,
            "Pipeline",
            "SimpleDdgiSchedulerCommitPass.cs"));
        string project = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "Njulf.Shaders.csproj"));
        string atomicGate = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "VerifyProductionDiagnosticAtomics.ps1"));
        string normalizedPass = pass.Replace("\r\n", "\n", StringComparison.Ordinal);

        int scanConditional = shader.IndexOf(
            "#if SIMPLE_DDGI_FEEDBACK_PARTIAL_REDUCTION",
            shader.IndexOf("uint activeProbeCount", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int finalBranch = shader.IndexOf(
            "#else",
            scanConditional,
            StringComparison.Ordinal);
        int scanEnd = shader.IndexOf(
            "#endif",
            finalBranch,
            StringComparison.Ordinal);
        string partialScan = shader[scanConditional..finalBranch];
        string finalReduction = shader[finalBranch..scanEnd];

        int executeStart = pass.IndexOf(
            "public override void Execute(",
            StringComparison.Ordinal);
        int helperStart = pass.IndexOf(
            "internal static uint CalculateFeedbackPartialGroupCount",
            executeStart,
            StringComparison.Ordinal);
        string execute = pass[executeStart..helperStart];
        int partialBind = execute.IndexOf(
            "_pipelines[3]",
            StringComparison.Ordinal);
        int partialDispatch = execute.IndexOf(
            "CmdDispatch(cmd, partialGroupCount, 1, 1)",
            partialBind,
            StringComparison.Ordinal);
        int interStageBarrier = execute.IndexOf(
            "InsertStorageBarrier(cmd)",
            partialDispatch,
            StringComparison.Ordinal);
        int finalBind = execute.IndexOf(
            "_pipelines[4]",
            interStageBarrier,
            StringComparison.Ordinal);
        int finalDispatch = execute.IndexOf(
            "CmdDispatch(cmd, 1, 1, 1)",
            finalBind,
            StringComparison.Ordinal);
        int finalBarrier = execute.IndexOf(
            "InsertStorageBarrier(cmd)",
            finalDispatch,
            StringComparison.Ordinal);
        int readback = execute.IndexOf(
            "RecordFeedbackReadback",
            finalBarrier,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain(
                "#define SIMPLE_DDGI_FEEDBACK_PARTIAL_REDUCTION 0"));
            Assert.That(partialScan, Does.Contain(
                "uint globalInvocationIndex = gl_GlobalInvocationID.x;"));
            Assert.That(partialScan, Does.Contain(
                "gl_NumWorkGroups.x * gl_WorkGroupSize.x"));
            Assert.That(partialScan, Does.Contain(
                "probeIndex += globalInvocationCount"));
            Assert.That(partialScan, Does.Contain(
                "SchedulerArenaWrite(receiverRecordBase + 0u, 0u);"));
            Assert.That(partialScan, Does.Contain(
                "SchedulerArenaWrite(receiverRecordBase + 1u, 0u);"));
            Assert.That(partialScan.IndexOf(
                    "SchedulerArenaWrite(receiverRecordBase + 0u, 0u);",
                    StringComparison.Ordinal),
                Is.LessThan(partialScan.IndexOf(
                    "if (!sourceVolumeValid ||",
                    StringComparison.Ordinal)),
                "receiver clearing must precede sparse-residency rejection");
            Assert.That(finalReduction, Does.Contain(
                "uint partialGroupCount = clamp(pc.Stage, 1u, 8u);"));
            Assert.That(finalReduction, Does.Contain(
                "pendingFresh += SchedulerArenaRead("));
            Assert.That(finalReduction, Does.Contain(
                "maximumFreshAge = max("));
            Assert.That(finalReduction, Does.Contain(
                "receiverConsumerMask |= SchedulerArenaRead("));
            Assert.That(finalReduction, Does.Not.Contain(
                "receiverPreviousBankBase"));
            Assert.That(finalReduction, Does.Not.Contain(
                "SchedulerArenaWrite(receiverRecordBase"));
            Assert.That(shader, Does.Contain(
                "gl_WorkGroupID.x * SIMPLE_DDGI_FEEDBACK_REDUCTION_WORDS"));
            Assert.That(shader, Does.Contain(
                "partialWordCount - 64u"));
            Assert.That(shader, Does.Contain(
                "lane < overwrittenCursorCount"));

            int partialName = pass.IndexOf(
                "\"ddgi_simple_schedule_feedback_partial.comp.spv\"",
                StringComparison.Ordinal);
            int finalName = pass.IndexOf(
                "\"ddgi_simple_schedule_feedback.comp.spv\"",
                StringComparison.Ordinal);
            Assert.That(partialName, Is.GreaterThanOrEqualTo(0));
            Assert.That(finalName, Is.GreaterThan(partialName));
            Assert.That(normalizedPass, Does.Contain("PipelinesAreReady()"));
            Assert.That(normalizedPass, Does.Contain(
                "for (int i = 0; i < _pipelines.Length; i++)"));
            Assert.That(partialBind, Is.GreaterThanOrEqualTo(0));
            Assert.That(partialDispatch, Is.GreaterThan(partialBind));
            Assert.That(interStageBarrier, Is.GreaterThan(partialDispatch));
            Assert.That(finalBind, Is.GreaterThan(interStageBarrier));
            Assert.That(finalDispatch, Is.GreaterThan(finalBind));
            Assert.That(finalBarrier, Is.GreaterThan(finalDispatch));
            Assert.That(readback, Is.GreaterThan(finalBarrier));

            Assert.That(project, Does.Contain(
                "<SimpleDdgiFeedbackShaderVariant Include=\"ddgi_simple_schedule_feedback_partial.comp\">"));
            Assert.That(project, Does.Contain(
                "-DSIMPLE_DDGI_FEEDBACK_PARTIAL_REDUCTION=1"));
            Assert.That(project, Does.Contain(
                "<NjulfShaderArtifact Include=\"@(SimpleDdgiFeedbackShaderVariant)\">"));
            Assert.That(project, Does.Contain(
                "%(SimpleDdgiFeedbackShaderVariant.Defines)"));
            Assert.That(project, Does.Contain(
                "<EmbeddedResource Include=\"@(NjulfShaderArtifact -&gt; '$(IntermediateOutputPath)Shaders\\%(Identity).spv')\">"));
            Assert.That(atomicGate, Does.Contain(
                "'ddgi_simple_schedule_feedback_partial.comp.spv'"));
        });
    }

    [Test]
    public void FeedbackAbiAndBuiltArtifacts_RemainAvailableAndDistinct()
    {
        SimpleDdgiGpuSchedulerLayout layout =
            SimpleDdgiGpuSchedulerLayout.Create(
                activeProbeCount: 32768,
                requestCapacity: 2048,
                activeVolumeCount: 16);
        byte[] partial = ShaderModuleLoader.LoadBytes(
            "ddgi_simple_schedule_feedback_partial.comp.spv");
        byte[] final = ShaderModuleLoader.LoadBytes(
            "ddgi_simple_schedule_feedback.comp.spv");
        string[] resources = typeof(ShaderLibrary).Assembly
            .GetManifestResourceNames();

        Assert.Multiple(() =>
        {
            Assert.That(layout.FeedbackSummary.ByteSize, Is.EqualTo(4096));
            Assert.That(layout.FeedbackSummary.ElementCount, Is.EqualTo(1024));
            Assert.That(
                Marshal.SizeOf<GPUSimpleDdgiSchedulePushConstants>(),
                Is.EqualTo(124));
            Assert.That(BitConverter.ToUInt32(partial), Is.EqualTo(0x0723_0203u));
            Assert.That(BitConverter.ToUInt32(final), Is.EqualTo(0x0723_0203u));
            Assert.That(partial, Is.Not.EqualTo(final));
            Assert.That(resources, Does.Contain(
                "Njulf.Shaders.ddgi_simple_schedule_feedback_partial.comp"));
            Assert.That(resources, Does.Contain(
                "Njulf.Shaders.ddgi_simple_schedule_feedback.comp"));
        });
    }

    [Test]
    public void BothFeedbackSpecializations_CompileAndPassSpirvValidation()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string shaderPath = Path.Combine(
            shaderDirectory,
            "ddgi_simple_schedule_feedback.comp");
        string finalPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"ddgi-feedback-final-{Guid.NewGuid():N}.spv");
        string partialPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"ddgi-feedback-partial-{Guid.NewGuid():N}.spv");
        try
        {
            RunTool(
                "glslangValidator",
                "-V",
                "--target-env",
                "vulkan1.3",
                "-Os",
                $"-I{shaderDirectory}",
                "-o",
                finalPath,
                shaderPath);
            RunTool(
                "glslangValidator",
                "-V",
                "--target-env",
                "vulkan1.3",
                "-Os",
                "-DSIMPLE_DDGI_FEEDBACK_PARTIAL_REDUCTION=1",
                $"-I{shaderDirectory}",
                "-o",
                partialPath,
                shaderPath);
            RunTool("spirv-val", "--target-env", "vulkan1.3", finalPath);
            RunTool("spirv-val", "--target-env", "vulkan1.3", partialPath);

            Assert.Multiple(() =>
            {
                Assert.That(new FileInfo(finalPath).Length, Is.GreaterThan(20));
                Assert.That(new FileInfo(partialPath).Length, Is.GreaterThan(20));
                Assert.That(File.ReadAllBytes(partialPath),
                    Is.Not.EqualTo(File.ReadAllBytes(finalPath)));
            });
        }
        finally
        {
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
    }

    private static uint[] RunSingleGroupMirror(
        int probeCount,
        int previousBank,
        ReceiverRecord[][] receiverBanks,
        bool receiverAvailable)
    {
        var reduction = new uint[ReductionWordCount];
        for (int lane = 0; lane < WorkgroupSize; lane++)
        {
            for (int probeIndex = lane;
                 probeIndex < probeCount;
                 probeIndex += WorkgroupSize)
            {
                AccumulateProbe(
                    reduction,
                    probeIndex,
                    previousBank,
                    receiverBanks,
                    receiverAvailable);
            }
        }
        return reduction;
    }

    private static uint[] RunTwoStageMirror(
        int probeCount,
        int previousBank,
        ReceiverRecord[][] receiverBanks,
        bool receiverAvailable)
    {
        int groupCount = checked((int)SimpleDdgiSchedulerCommitPass
            .CalculateFeedbackPartialGroupCount(probeCount));
        int globalInvocationCount = groupCount * WorkgroupSize;
        var partials = new uint[groupCount][];
        for (int group = 0; group < groupCount; group++)
        {
            uint[] partial = new uint[ReductionWordCount];
            partials[group] = partial;
            for (int lane = 0; lane < WorkgroupSize; lane++)
            {
                int globalInvocationIndex = group * WorkgroupSize + lane;
                for (int probeIndex = globalInvocationIndex;
                     probeIndex < probeCount;
                     probeIndex += globalInvocationCount)
                {
                    AccumulateProbe(
                        partial,
                        probeIndex,
                        previousBank,
                        receiverBanks,
                        receiverAvailable);
                }
            }
        }

        var final = new uint[ReductionWordCount];
        foreach (uint[] partial in partials)
            MergeReduction(final, partial);
        return final;
    }

    private static void AccumulateProbe(
        uint[] reduction,
        int probeIndex,
        int previousBank,
        ReceiverRecord[][] receiverBanks,
        bool receiverAvailable)
    {
        if (receiverAvailable)
        {
            ReceiverRecord receiver = receiverBanks[previousBank][probeIndex];
            bool contributed = receiver.WeightQ8 != 0u &&
                (receiver.CoverageAndFlags & ReceiverRoleMask) != 0u;
            Add(ref reduction[22], contributed ? 1u : 0u);
            Add(
                ref reduction[23],
                contributed
                    ? (uint)BitOperations.PopCount(
                        receiver.CoverageAndFlags & ReceiverCoverageMask)
                    : 0u);
            Add(
                ref reduction[24],
                contributed &&
                    (receiver.CoverageAndFlags & ReceiverFallbackMask) != 0u
                    ? 1u
                    : 0u);
            if (contributed)
            {
                reduction[25] |=
                    receiver.CoverageAndFlags & ReceiverConsumerMask;
            }
            receiverBanks[previousBank][probeIndex] = default;
        }

        if (!IsResident(probeIndex))
            return;

        for (int field = 0; field <= 9; field++)
        {
            Add(
                ref reduction[field],
                (uint)(1 + (probeIndex * (field + 3) + field) % 11));
        }
        for (int field = 10; field <= 14; field++)
        {
            reduction[field] = Math.Max(
                reduction[field],
                (uint)((probeIndex * (field + 5) + field * 13) % 65_521));
        }
        for (int field = 15; field <= 21; field++)
        {
            Add(
                ref reduction[field],
                (uint)(1 + (probeIndex * (field + 7) + field) % 17));
        }
    }

    private static void MergeReduction(uint[] destination, uint[] source)
    {
        for (int field = 0; field <= 9; field++)
            Add(ref destination[field], source[field]);
        for (int field = 10; field <= 14; field++)
            destination[field] = Math.Max(destination[field], source[field]);
        for (int field = 15; field <= 24; field++)
            Add(ref destination[field], source[field]);
        destination[25] |= source[25];
    }

    private static void Add(ref uint destination, uint value) =>
        destination = unchecked(destination + value);

    private static bool IsResident(int probeIndex) =>
        probeIndex % 7 != 6 && probeIndex % 17 != 16;

    private static ReceiverRecord[][] CreateReceiverBanks(int probeCount)
    {
        ReceiverRecord[][] banks =
        [new ReceiverRecord[probeCount], new ReceiverRecord[probeCount]];
        for (int bank = 0; bank < banks.Length; bank++)
        {
            for (int probe = 0; probe < probeCount; probe++)
            {
                uint coverage = 1u << (probe % 24);
                coverage |= 1u << ((probe * 5 + 3) % 24);
                uint role = 1u << (24 + probe % 3);
                uint consumer = 1u << (27 + (probe + bank) % 5);
                banks[bank][probe] = new ReceiverRecord(
                    WeightQ8: (probe + bank) % 5 == 4
                        ? 0u
                        : (uint)(1 + probe * 3 + bank),
                    CoverageAndFlags: coverage | role | consumer);
            }
        }
        return banks;
    }

    private static ReceiverRecord[][] CloneBanks(ReceiverRecord[][] source) =>
        [(ReceiverRecord[])source[0].Clone(), (ReceiverRecord[])source[1].Clone()];

    private static void PublishFinalHeader(uint[] feedback)
    {
        for (int word = 0; word < FeedbackHeaderWordCount; word++)
            feedback[word] = unchecked(0xc001_0000u + (uint)word);
    }

    private static void RunTool(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ??
            throw new AssertionException($"Could not start {fileName}.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{fileName} timed out. {standardOutput} {standardError}");
        }
        Assert.That(process.ExitCode, Is.Zero,
            $"{fileName} failed. {standardOutput} {standardError}");
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new AssertionException($"Could not find repo directory '{name}'.");
    }

    private readonly record struct ReceiverRecord(
        uint WeightQ8,
        uint CoverageAndFlags);
}
