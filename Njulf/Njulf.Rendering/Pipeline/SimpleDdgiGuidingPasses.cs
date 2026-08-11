using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Stable names for C3 graph resources.  These are intentionally separate
/// from the ordinary DDGI ray staging inputs: training records/work items are
/// transient slices of existing scheduler/trace staging, while only the two
/// banks and partial scratch are C3-owned allocations.
/// </summary>
public static class SimpleDdgiGuidingResourceNames
{
    public const string DistributionBank0 = "simple-ddgi-guiding-distribution-bank-0";
    public const string DistributionBank1 = "simple-ddgi-guiding-distribution-bank-1";
    public const string TrainingScratch = "simple-ddgi-guiding-training-scratch";
    public const string ValidationReference = "simple-ddgi-guiding-validation-reference";
    public const string RayDispatchStaging = "simple-ddgi-ray-dispatch-staging";
    public const string DirectionPayloadSidecar = "simple-ddgi-guiding-direction-pdf-sidecar";
    public const string ValidationCounters = "simple-ddgi-guiding-validation-counters";
}

/// <summary>
/// Per-program descriptor binding ABI.  Bindless table slots are assigned by
/// the renderer integration; these bindings describe the logical resources in
/// the standalone SPIR-V modules and make accidental cross-pass rebinding
/// visible in review/tests.
/// </summary>
public static class SimpleDdgiGuidingGpuBindings
{
    public const uint ExtractTrainingRecords = 0u;
    public const uint ExtractWorkItems = 1u;
    public const uint ExtractValidationCounters = 2u;

    public const uint TrainRecords = 0u;
    public const uint TrainWorkItems = 1u;
    public const uint TrainPartialScratch = 2u;
    public const uint TrainValidationCounters = 3u;

    public const uint BuildWorkItems = 0u;
    public const uint BuildPartialScratch = 1u;
    public const uint BuildWriteBank = 2u;
    public const uint BuildValidationCounters = 3u;

    public const uint SampleReadBank = 0u;
    public const uint SampleRequests = 1u;
    public const uint SamplePayloads = 2u;
    public const uint SampleValidationCounters = 3u;

    public const uint ValidateBank = 0u;
    public const uint ValidateWorkItems = 1u;
    public const uint ValidateCounters = 2u;
}

/// <summary>
/// Renderer-wide bindless slots reserved by <see cref="BindlessIndex"/>.
/// These are not descriptor-set binding numbers: they are the fixed static
/// table entries mirrored in common.glsl.  Keeping the reference here prevents
/// an eventual C3 pass implementation from silently choosing a second slot.
/// </summary>
public static class SimpleDdgiGuidingBindlessSlots
{
    public const int DistributionBank0 =
        BindlessIndex.SimpleDdgiGuidingDistributionBank0Buffer;
    public const int DistributionBank1 =
        BindlessIndex.SimpleDdgiGuidingDistributionBank1Buffer;
    public const int TrainingScratch =
        BindlessIndex.SimpleDdgiGuidingTrainingScratchBuffer;
    public const int DirectionPdfSidecar =
        BindlessIndex.SimpleDdgiGuidingDirectionPdfSidecarBuffer;
}

public static class SimpleDdgiGuidingGpuPassNames
{
    public const string Train = "SimpleDdgiGuidingTrainPass";
    public const string Build = "SimpleDdgiGuidingBuildPass";
    public const string Sample = "SimpleDdgiGuidingSamplePass";
    public const string Validate = "SimpleDdgiGuidingValidatePass";

    public const string TrainShader = "ddgi_guiding_train.comp.spv";
    public const string BuildShader = "ddgi_guiding_build.comp.spv";
    public const string SampleShader = "ddgi_guiding_sample.comp.spv";
    public const string ValidateShader = "ddgi_guiding_validate.comp.spv";
    public const string ExtractLegacyShader =
        "ddgi_guiding_extract_legacy.comp.spv";
    public const string ExtractValidateShader =
        "ddgi_guiding_extract_validate.comp.spv";
    public const string ExtractPackedShader =
        "ddgi_guiding_extract_packed.comp.spv";
}

public enum SimpleDdgiGuidingPassKind : byte
{
    Train = 0,
    Build = 1,
    Sample = 2,
    Validate = 3,
    // Folded into the graph's Train node after DDGI trace. Kept as a distinct
    // private descriptor-ring identity so in-flight Train and Extract sets can
    // never be updated through the same Vulkan descriptor object.
    Extract = 4
}

public enum SimpleDdgiGuidingResourceAccess : byte
{
    Read = 0,
    Write = 1,
    ReadWrite = 2
}

public readonly record struct SimpleDdgiGuidingPassResourceUse(
    string ResourceName,
    SimpleDdgiGuidingResourceAccess Access);

/// <summary>
/// Graph-facing declaration only.  It performs no Vulkan allocation or
/// dispatch, allowing the renderer to omit every C3 node when its effective
/// admission is false.
/// </summary>
public readonly record struct SimpleDdgiGuidingPassDeclaration(
    SimpleDdgiGuidingPassKind Kind,
    string Name,
    string ShaderName,
    IReadOnlyList<SimpleDdgiGuidingPassResourceUse> ResourceUses,
    int SourceBankIndex,
    int DestinationBankIndex);

/// <summary>
/// Builds the minimum C3 graph slice from lifecycle state.  Train/build are
/// recorded only while a matching build token is in flight.  Sampling appears
/// only after a validated bank has been published; before that the canonical
/// uniform DDGI trace path remains the sole direction producer.
/// </summary>
public static class SimpleDdgiGuidingPasses
{
    private static readonly IReadOnlyList<SimpleDdgiGuidingPassDeclaration> Disabled =
        Array.Empty<SimpleDdgiGuidingPassDeclaration>();

    public static IReadOnlyList<SimpleDdgiGuidingPassDeclaration> Create(
        in SimpleDdgiGuidingRuntimeSnapshot snapshot,
        bool includeValidationPass)
    {
        if (!snapshot.HasResources)
            return Disabled;

        var passes = new List<SimpleDdgiGuidingPassDeclaration>(
            includeValidationPass ? 4 : 3);
        if (snapshot.State == SimpleDdgiGuidingResourceState.Building)
        {
            string writeBank = ResourceForBank(snapshot.WriteBankIndex);
            passes.Add(new SimpleDdgiGuidingPassDeclaration(
                SimpleDdgiGuidingPassKind.Train,
                SimpleDdgiGuidingGpuPassNames.Train,
                SimpleDdgiGuidingGpuPassNames.TrainShader,
                [
                    new(SimpleDdgiGuidingResourceNames.RayDispatchStaging,
                        SimpleDdgiGuidingResourceAccess.Read),
                    new(SimpleDdgiGuidingResourceNames.TrainingScratch,
                        SimpleDdgiGuidingResourceAccess.Write),
                    new(SimpleDdgiGuidingResourceNames.ValidationCounters,
                        SimpleDdgiGuidingResourceAccess.ReadWrite)
                ],
                SourceBankIndex: snapshot.ReadBankIndex,
                DestinationBankIndex: snapshot.WriteBankIndex));
            passes.Add(new SimpleDdgiGuidingPassDeclaration(
                SimpleDdgiGuidingPassKind.Build,
                SimpleDdgiGuidingGpuPassNames.Build,
                SimpleDdgiGuidingGpuPassNames.BuildShader,
                [
                    new(SimpleDdgiGuidingResourceNames.TrainingScratch,
                        SimpleDdgiGuidingResourceAccess.Read),
                    new(writeBank, SimpleDdgiGuidingResourceAccess.Write),
                    new(SimpleDdgiGuidingResourceNames.ValidationCounters,
                        SimpleDdgiGuidingResourceAccess.ReadWrite)
                ],
                SourceBankIndex: snapshot.ReadBankIndex,
                DestinationBankIndex: snapshot.WriteBankIndex));
            if (includeValidationPass)
            {
                passes.Add(new SimpleDdgiGuidingPassDeclaration(
                    SimpleDdgiGuidingPassKind.Validate,
                    SimpleDdgiGuidingGpuPassNames.Validate,
                    SimpleDdgiGuidingGpuPassNames.ValidateShader,
                    [
                        new(writeBank, SimpleDdgiGuidingResourceAccess.ReadWrite),
                        new(SimpleDdgiGuidingResourceNames.ValidationCounters,
                            SimpleDdgiGuidingResourceAccess.ReadWrite)
                    ],
                    SourceBankIndex: snapshot.WriteBankIndex,
                    DestinationBankIndex: snapshot.WriteBankIndex));
            }
        }

        if (snapshot.HasReadableDistribution)
        {
            passes.Add(new SimpleDdgiGuidingPassDeclaration(
                SimpleDdgiGuidingPassKind.Sample,
                SimpleDdgiGuidingGpuPassNames.Sample,
                SimpleDdgiGuidingGpuPassNames.SampleShader,
                [
                    new(ResourceForBank(snapshot.ReadBankIndex),
                        SimpleDdgiGuidingResourceAccess.Read),
                    new(SimpleDdgiGuidingResourceNames.RayDispatchStaging,
                        SimpleDdgiGuidingResourceAccess.Read),
                    new(SimpleDdgiGuidingResourceNames.DirectionPayloadSidecar,
                        SimpleDdgiGuidingResourceAccess.Write),
                    new(SimpleDdgiGuidingResourceNames.ValidationCounters,
                        SimpleDdgiGuidingResourceAccess.ReadWrite)
                ],
                SourceBankIndex: snapshot.ReadBankIndex,
                DestinationBankIndex: -1));
        }

        return passes.Count == 0 ? Disabled : passes;
    }

    private static string ResourceForBank(int bankIndex) => bankIndex switch
    {
        0 => SimpleDdgiGuidingResourceNames.DistributionBank0,
        1 => SimpleDdgiGuidingResourceNames.DistributionBank1,
        _ => throw new ArgumentOutOfRangeException(nameof(bankIndex),
            "A guiding graph pass requires a binary bank index.")
    };
}
