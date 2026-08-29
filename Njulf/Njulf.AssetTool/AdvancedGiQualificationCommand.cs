using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.AssetTool;

internal static class AdvancedGiQualificationCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "advanced-gi requires pin-corpus, verify-corpus, " +
                "create-startup, verify-startup, verify-qualification, " +
                "or verify-c1-model.");
        }
        return args[0] switch
        {
            "pin-corpus" => RunPinCorpus(args[1..]),
            "verify-corpus" => RunVerifyCorpus(args[1..]),
            "create-startup" => RunCreateStartup(args[1..]),
            "verify-startup" => RunVerifyStartup(args[1..]),
            "verify-qualification" => RunVerifyQualification(args[1..]),
            "verify-c1-model" => RunVerifyC1Model(args[1..]),
            _ => throw new ArgumentException(
                $"Unknown Advanced-GI operation '{args[0]}'.")
        };
    }

    private static int RunPinCorpus(string[] args)
    {
        string? root = null;
        string? request = null;
        string? output = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    root = RequireValue(args, ref index, "--root");
                    break;
                case "--request":
                    request = RequireValue(args, ref index, "--request");
                    break;
                case "--out":
                    output = RequireValue(args, ref index, "--out");
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown corpus pinning option '{args[index]}'.");
            }
        }
        Require(root, "--root");
        Require(request, "--request");
        Require(output, "--out");
        AdvancedGiVerifiedQualificationCorpus corpus =
            AdvancedGiQualificationCorpusCodec.Pin(
                root!, request!, output!);
        PrintCorpus(corpus, "Pinned");
        return 0;
    }

    private static int RunVerifyCorpus(string[] args)
    {
        string manifest = ReadSinglePathOption(
            args, "--manifest", "corpus verification");
        if (!AdvancedGiQualificationCorpusCodec.TryLoadAndVerify(
                manifest,
                out AdvancedGiVerifiedQualificationCorpus? corpus,
                out string detail) || corpus is null)
        {
            Console.Error.WriteLine(
                $"Advanced-GI corpus rejected: {detail}");
            return 1;
        }
        PrintCorpus(corpus, "Verified");
        return 0;
    }

    private static int RunCreateStartup(string[] args)
    {
        string? profile = null;
        string? settings = null;
        string? corpusSha256 = null;
        string? contentProfile = null;
        string? sceneSha256 = null;
        string? prerequisite = null;
        string? qualification = null;
        string? runtimeEvidence = null;
        string? candidate = null;
        string? buildCommit = null;
        string? shaderBundleSha256 = null;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--profile":
                    profile = RequireValue(args, ref index, option);
                    break;
                case "--settings":
                    settings = RequireValue(args, ref index, option);
                    break;
                case "--corpus-sha256":
                    corpusSha256 = RequireValue(args, ref index, option);
                    break;
                case "--content-profile":
                    contentProfile = RequireValue(args, ref index, option);
                    break;
                case "--scene-sha256":
                    sceneSha256 = RequireValue(args, ref index, option);
                    break;
                case "--prerequisite":
                    prerequisite = RequireValue(args, ref index, option);
                    break;
                case "--qualification":
                    qualification = RequireValue(args, ref index, option);
                    break;
                case "--runtime-evidence":
                    runtimeEvidence = RequireValue(args, ref index, option);
                    break;
                case "--candidate":
                    candidate = RequireValue(args, ref index, option);
                    break;
                case "--build-commit":
                    buildCommit = RequireValue(args, ref index, option);
                    break;
                case "--shader-bundle-sha256":
                    shaderBundleSha256 = RequireValue(args, ref index, option);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown startup creation option '{option}'.");
            }
        }
        Require(profile, "--profile");
        Require(settings, "--settings");
        Require(corpusSha256, "--corpus-sha256");
        Require(contentProfile, "--content-profile");
        Require(sceneSha256, "--scene-sha256");
        AdvancedGiRuntimeBuildIdentity? identity = CreateRuntimeIdentity(
            buildCommit, shaderBundleSha256);
        RenderSettings renderSettings = RenderSettings.Load(settings!);
        var inputs = new AdvancedGiStartupProfileInputs(
            profile!,
            new AdvancedGiRuntimeContentBinding(
                corpusSha256!, contentProfile!, sceneSha256!),
            prerequisite,
            qualification,
            runtimeEvidence,
            candidate);
        AdvancedGiStartupProfilePreflightResult result =
            AdvancedGiStartupProfilePreflight.SaveValidated(
                renderSettings, inputs, identity);
        Console.WriteLine(
            $"Advanced-GI startup profile created: " +
            $"profile='{Path.GetFullPath(profile!)}', " +
            $"settingsFingerprint=" +
            $"{AdvancedGiSettingsFingerprint.Compute(renderSettings.GlobalIllumination)}, " +
            $"checks={result.Checks.Count}.");
        return 0;
    }

    private static int RunVerifyStartup(string[] args)
    {
        string? profilePath = null;
        string? buildCommit = null;
        string? shaderBundleSha256 = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile":
                    profilePath = RequireValue(
                        args, ref index, "--profile");
                    break;
                case "--build-commit":
                    buildCommit = RequireValue(
                        args, ref index, "--build-commit");
                    break;
                case "--shader-bundle-sha256":
                    shaderBundleSha256 = RequireValue(
                        args, ref index, "--shader-bundle-sha256");
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown startup verification option '{args[index]}'.");
            }
        }
        Require(profilePath, "--profile");
        if (!AdvancedGiStartupProfileCodec.TryLoad(
                profilePath!,
                out AdvancedGiStartupProfile? profile,
                out string loadDetail) || profile is null)
        {
            Console.Error.WriteLine(
                $"Advanced-GI startup profile rejected: {loadDetail}");
            return 1;
        }

        var inputs = new AdvancedGiStartupProfileInputs(
            profile.ProfilePath,
            profile.ContentBinding,
            profile.PrerequisiteManifestPath,
            profile.QualificationManifestPath,
            profile.RuntimeEvidenceBundlePath,
            profile.CandidateProfilePath);
        AdvancedGiStartupProfilePreflightResult result =
            AdvancedGiStartupProfilePreflight.Evaluate(
                profile.Settings,
                inputs,
                CreateRuntimeIdentity(buildCommit, shaderBundleSha256));
        foreach (AdvancedGiStartupProfileCheck check in result.Checks)
        {
            Console.WriteLine(
                $"{(check.Passed ? "PASS" : "FAIL")} " +
                $"{check.Id}: {check.Detail}");
        }
        if (!result.Ready)
        {
            Console.Error.WriteLine(
                "Advanced-GI startup preflight rejected: " +
                result.FailureSummary);
            return 1;
        }
        Console.WriteLine(
            "Advanced-GI startup preflight passed. Device capability and " +
            "driver-rule admission remain cold-start checks.");
        return 0;
    }

    private static int RunVerifyQualification(string[] args)
    {
        string manifest = ReadSinglePathOption(
            args, "--manifest", "qualification verification");
        if (!AdvancedGiQualificationManifestCodec.TryLoad(
                manifest,
                out AdvancedGiQualificationManifest qualification,
                out string detail))
        {
            Console.Error.WriteLine(
                $"Advanced-GI qualification manifest rejected: {detail}");
            return 1;
        }
        Console.WriteLine(
            $"Advanced-GI qualification manifest verified: " +
            $"features={qualification.Count}, manifest='{Path.GetFullPath(manifest)}'.");
        return 0;
    }

    private static int RunVerifyC1Model(string[] args)
    {
        string modelPath = ReadSinglePathOption(
            args, "--model", "C1 cooked-model verification");
        CookedModelAsset model = CookedPackage.LoadModel(modelPath);
        CookedOpacityMicromapPayloadLoadStatus status =
            model.OpacityMicromapLoadStatus;
        OpacityMicromapCookedPayload? payload = model.OpacityMicromapPayload;
        if (!status.SectionPresent || !status.Accepted || payload is null)
        {
            Console.Error.WriteLine(
                "C1 cooked model rejected: " +
                $"sectionPresent={status.SectionPresent}, " +
                $"accepted={status.Accepted}, failure={status.Failure}, " +
                $"detail='{status.Detail}', " +
                $"model='{Path.GetFullPath(modelPath)}'.");
            return 1;
        }

        bool complete =
            payload.PayloadKind == OpacityMicromapPayloadKind.VulkanExtFourState &&
            payload.Format == OpacityMicromapFormat.FourState &&
            payload.CookAbi != 0u &&
            !payload.SourceContentHash.IsZero &&
            !payload.SdkProvenanceHash.IsZero &&
            payload.MaximumSubdivisionLevel != 0u &&
            payload.PrimitiveCount != 0u &&
            payload.DescriptorCount != 0u &&
            payload.MaterialContracts.Count != 0 &&
            payload.UsageHistogram.Count != 0 &&
            !payload.OmmData.IsEmpty &&
            !payload.IndexData.IsEmpty &&
            !payload.DescriptorData.IsEmpty;
        if (!complete)
        {
            Console.Error.WriteLine(
                "C1 cooked model rejected: the accepted optional section is " +
                "not a resource-complete Vulkan EXT four-state payload.");
            return 1;
        }

        Console.WriteLine(
            "C1 cooked model verified: " +
            $"primitives={payload.PrimitiveCount}, " +
            $"descriptors={payload.DescriptorCount}, " +
            $"materials={payload.MaterialContracts.Count}, " +
            $"ommBytes={payload.OmmData.Length}, " +
            $"model='{Path.GetFullPath(modelPath)}'.");
        return 0;
    }

    private static AdvancedGiRuntimeBuildIdentity? CreateRuntimeIdentity(
        string? buildCommit,
        string? shaderBundleSha256)
    {
        bool hasCommit = !string.IsNullOrWhiteSpace(buildCommit);
        bool hasShaders = !string.IsNullOrWhiteSpace(shaderBundleSha256);
        if (hasCommit != hasShaders)
        {
            throw new ArgumentException(
                "--build-commit and --shader-bundle-sha256 must be supplied together.");
        }
        if (!hasCommit)
            return null;
        var identity = new AdvancedGiRuntimeBuildIdentity(
            buildCommit!.Trim(), shaderBundleSha256!.Trim());
        if (!identity.IsWellFormed)
        {
            throw new ArgumentException(
                "The supplied runtime build identity is malformed.");
        }
        return identity;
    }

    private static string ReadSinglePathOption(
        string[] args,
        string option,
        string role)
    {
        if (args.Length != 2 || args[0] != option)
            throw new ArgumentException($"{role} requires {option} <path>.");
        return args[1];
    }

    private static void PrintCorpus(
        AdvancedGiVerifiedQualificationCorpus corpus,
        string action)
    {
        Console.WriteLine(
            $"{action} Advanced-GI corpus '{corpus.CorpusId}': " +
            $"cases={corpus.CaseCount}, artifacts={corpus.ArtifactCount}, " +
            $"features={string.Join(',', corpus.CoveredFeatures.Order())}, " +
            $"corpusSha256={corpus.CorpusSha256}, " +
            $"manifest='{corpus.ManifestPath}'.");
    }

    private static string RequireValue(
        string[] args,
        ref int index,
        string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static void Require(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{option} is required.");
    }
}
