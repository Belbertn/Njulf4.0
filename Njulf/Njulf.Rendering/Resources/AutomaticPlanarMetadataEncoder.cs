using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

internal readonly record struct AutomaticPlanarMetadataCaptureInput(
    int Slot,
    IReadOnlyList<uint> ReceiverIdentities,
    IReadOnlyList<uint> ExcludedObjectIndices,
    IReadOnlyList<int> TextureIndices);

internal sealed record AutomaticPlanarMetadataCaptureLayout(
    int Slot,
    uint[] ReceiverIdentities,
    uint ReceiverOffset,
    AutomaticPlanarExclusionPayloadEncoding ExclusionEncoding,
    uint ExclusionDescriptor,
    uint[] ExclusionPayload,
    uint ExclusionOffset,
    int ExcludedObjectCount,
    uint[] TextureIndices,
    uint TextureOffset)
{
    public int BitsetPayloadWords =>
        ExclusionEncoding == AutomaticPlanarExclusionPayloadEncoding.DenseBitset
            ? ExclusionPayload.Length
            : 0;

    public int SortedListPayloadWords =>
        ExclusionEncoding == AutomaticPlanarExclusionPayloadEncoding.SortedList
            ? ExclusionPayload.Length
            : 0;
}

internal sealed record AutomaticPlanarMetadataBankLayout(
    bool Fits,
    AutomaticPlanarMetadataCaptureLayout[] Captures,
    int PayloadWordCount,
    int WordsUsed,
    int BitsetCaptureCount,
    int SortedListCaptureCount,
    string Detail);

internal static class AutomaticPlanarMetadataEncoder
{
    internal const uint DenseBitsetFlag = 0x80000000u;
    internal const uint PayloadCountMask = 0x7fffffffu;
    internal const string EncodingModeEnvironmentVariable =
        "NJULF_AUTOMATIC_PLANAR_EXCLUSION_ENCODING";

    public static AutomaticPlanarExclusionEncodingMode ResolveMode(
        string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Equals(
                nameof(AutomaticPlanarExclusionEncodingMode.SortedList),
                StringComparison.OrdinalIgnoreCase))
        {
            return AutomaticPlanarExclusionEncodingMode.SortedList;
        }
        if (configured.Equals(
                nameof(AutomaticPlanarExclusionEncodingMode.BitsetAuto),
                StringComparison.OrdinalIgnoreCase))
        {
            return AutomaticPlanarExclusionEncodingMode.BitsetAuto;
        }

        throw new InvalidOperationException(
            $"Environment variable {EncodingModeEnvironmentVariable} must be " +
            $"'{nameof(AutomaticPlanarExclusionEncodingMode.BitsetAuto)}' or " +
            $"'{nameof(AutomaticPlanarExclusionEncodingMode.SortedList)}', " +
            $"not '{configured}'.");
    }

    public static AutomaticPlanarMetadataBankLayout Build(
        IReadOnlyList<AutomaticPlanarMetadataCaptureInput> captures,
        AutomaticPlanarExclusionEncodingMode mode,
        int bankWordCount,
        int variableDataWordOffset)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (bankWordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bankWordCount));
        if (variableDataWordOffset < 0 ||
            variableDataWordOffset > bankWordCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(variableDataWordOffset));
        }

        var normalized = new NormalizedCapture[captures.Count];
        ulong requiredWords = checked((ulong)variableDataWordOffset);
        for (int index = 0; index < captures.Count; index++)
        {
            AutomaticPlanarMetadataCaptureInput input = captures[index];
            if (input.Slot != index)
            {
                throw new ArgumentException(
                    "Automatic planar metadata slots must be contiguous and " +
                    "ordered from zero.",
                    nameof(captures));
            }

            uint[] receivers = input.ReceiverIdentities
                .Distinct()
                .Order()
                .ToArray();
            uint[] exclusions = input.ExcludedObjectIndices
                .Distinct()
                .Order()
                .ToArray();
            uint[] textures = input.TextureIndices
                .Select(static textureIndex =>
                    checked((uint)textureIndex))
                .ToArray();
            ulong bitsetWords = exclusions.Length == 0
                ? 0UL
                : checked(((ulong)exclusions[^1] >> 5) + 1UL);
            bool useBitset = mode ==
                AutomaticPlanarExclusionEncodingMode.BitsetAuto;
            normalized[index] = new NormalizedCapture(
                input.Slot,
                receivers,
                exclusions,
                textures,
                bitsetWords,
                useBitset);
            requiredWords = checked(
                requiredWords +
                (ulong)receivers.Length +
                (ulong)textures.Length +
                (useBitset ? bitsetWords : (ulong)exclusions.Length));
        }

        if (requiredWords > (ulong)bankWordCount &&
            mode == AutomaticPlanarExclusionEncodingMode.BitsetAuto)
        {
            foreach (int index in normalized
                         .Select((capture, captureIndex) => new
                         {
                             Index = captureIndex,
                             Savings = capture.BitsetWordCount >
                                       (ulong)capture.Exclusions.Length
                                 ? capture.BitsetWordCount -
                                   (ulong)capture.Exclusions.Length
                                 : 0UL
                         })
                         .Where(static candidate => candidate.Savings > 0UL)
                         .OrderByDescending(static candidate =>
                             candidate.Savings)
                         .ThenBy(static candidate => candidate.Index)
                         .Select(static candidate => candidate.Index))
            {
                NormalizedCapture capture = normalized[index];
                requiredWords -= capture.BitsetWordCount -
                    (ulong)capture.Exclusions.Length;
                normalized[index] = capture with { UseBitset = false };
                if (requiredWords <= (ulong)bankWordCount)
                    break;
            }
        }

        if (requiredWords > (ulong)bankWordCount)
        {
            return new AutomaticPlanarMetadataBankLayout(
                false,
                [],
                0,
                variableDataWordOffset,
                0,
                0,
                $"Exact automatic-planar metadata requires {requiredWords} " +
                $"words, exceeding the {bankWordCount}-word frame bank.");
        }

        int cursor = variableDataWordOffset;
        var layouts = new AutomaticPlanarMetadataCaptureLayout[normalized.Length];
        int bitsetCaptureCount = 0;
        int sortedListCaptureCount = 0;
        for (int index = 0; index < normalized.Length; index++)
        {
            NormalizedCapture capture = normalized[index];
            uint receiverOffset = checked((uint)cursor);
            cursor = checked(cursor + capture.Receivers.Length);
            uint exclusionOffset = checked((uint)cursor);
            uint[] exclusionPayload;
            AutomaticPlanarExclusionPayloadEncoding encoding;
            uint descriptor;
            if (capture.UseBitset)
            {
                int wordCount = checked((int)capture.BitsetWordCount);
                exclusionPayload = new uint[wordCount];
                foreach (uint objectIndex in capture.Exclusions)
                {
                    exclusionPayload[objectIndex >> 5] |=
                        1u << checked((int)(objectIndex & 31u));
                }
                encoding = AutomaticPlanarExclusionPayloadEncoding.DenseBitset;
                descriptor = DenseBitsetFlag |
                    checked((uint)wordCount);
                bitsetCaptureCount++;
            }
            else
            {
                exclusionPayload = capture.Exclusions;
                encoding = AutomaticPlanarExclusionPayloadEncoding.SortedList;
                descriptor = checked((uint)exclusionPayload.Length);
                sortedListCaptureCount++;
            }
            cursor = checked(cursor + exclusionPayload.Length);
            uint textureOffset = checked((uint)cursor);
            cursor = checked(cursor + capture.Textures.Length);
            layouts[index] = new AutomaticPlanarMetadataCaptureLayout(
                capture.Slot,
                capture.Receivers,
                receiverOffset,
                encoding,
                descriptor,
                exclusionPayload,
                exclusionOffset,
                capture.Exclusions.Length,
                capture.Textures,
                textureOffset);
        }

        return new AutomaticPlanarMetadataBankLayout(
            true,
            layouts,
            checked(cursor - variableDataWordOffset),
            cursor,
            bitsetCaptureCount,
            sortedListCaptureCount,
            string.Empty);
    }

    public static bool Contains(
        AutomaticPlanarMetadataCaptureLayout capture,
        uint objectIndex)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.ExclusionEncoding ==
            AutomaticPlanarExclusionPayloadEncoding.DenseBitset)
        {
            uint wordIndex = objectIndex >> 5;
            return wordIndex < (uint)capture.ExclusionPayload.Length &&
                   (capture.ExclusionPayload[wordIndex] &
                    (1u << checked((int)(objectIndex & 31u)))) != 0u;
        }

        return Array.BinarySearch(capture.ExclusionPayload, objectIndex) >= 0;
    }

    private sealed record NormalizedCapture(
        int Slot,
        uint[] Receivers,
        uint[] Exclusions,
        uint[] Textures,
        ulong BitsetWordCount,
        bool UseBitset);
}
