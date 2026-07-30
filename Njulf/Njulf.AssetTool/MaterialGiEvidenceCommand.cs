using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;

namespace Njulf.AssetTool;

internal static class MaterialGiEvidenceCommand
{
    private const int AssemblyRequestSchemaVersion = 1;
    private const int MaximumAssemblyRequestBytes = 2 * 1024 * 1024;
    private const int MaximumJsonDepth = 32;

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = MaximumJsonDepth,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "material-gi-evidence requires assemble, pin-manifest, or verify-manifest.");
        }
        return args[0] switch
        {
            "assemble" => RunAssemble(args[1..]),
            "pin-manifest" => RunPinManifest(args[1..]),
            "verify-manifest" => RunVerifyManifest(args[1..]),
            _ => throw new ArgumentException(
                $"Unknown material-GI evidence operation '{args[0]}'.")
        };
    }

    private static int RunAssemble(string[] args)
    {
        string? rootPath = null;
        string? requestPath = null;
        string? bundleRelativePath = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    rootPath = RequireValue(args, ref index, "--root");
                    break;
                case "--request":
                    requestPath =
                        RequireValue(args, ref index, "--request");
                    break;
                case "--bundle":
                    bundleRelativePath =
                        RequireValue(args, ref index, "--bundle");
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown evidence assembly option '{args[index]}'.");
            }
        }
        Require(rootPath, "--root");
        Require(requestPath, "--request");
        Require(bundleRelativePath, "--bundle");

        string root = Path.GetFullPath(rootPath!);
        Directory.CreateDirectory(root);
        string bundlePath = ResolveContainedRelativePath(
            root,
            bundleRelativePath!,
            "--bundle");
        byte[] requestBytes = ReadStableBounded(
            requestPath!,
            MaximumAssemblyRequestBytes,
            "Evidence assembly request");
        RejectDuplicateJsonProperties(
            requestBytes,
            "Evidence assembly request");
        MaterialGiEvidenceAssemblyRequest request;
        try
        {
            request =
                JsonSerializer.Deserialize<MaterialGiEvidenceAssemblyRequest>(
                    requestBytes,
                    StrictJsonOptions)
                ?? throw new InvalidDataException(
                    "Evidence assembly request is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Evidence assembly request is invalid or contains unknown metadata.",
                exception);
        }
        ValidateAssemblyRequest(request);

        string generationDirectory = Path.Combine(
            root,
            ".material-gi-evidence",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generationDirectory);
        bool published = false;
        try
        {
            var artifacts =
                new List<MaterialGiReleaseEvidenceArtifact>(
                    request.Roles.Length);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (MaterialGiEvidenceAssemblyRole role in
                     request.Roles.OrderBy(
                         static role => role.Report.Role,
                         StringComparer.Ordinal))
            {
                if (!roles.Add(role.Report.Role))
                {
                    throw new InvalidDataException(
                        $"Evidence role '{role.Report.Role}' is duplicated.");
                }

                MaterialGiProducerEvidenceArtifact[] producers =
                    role.Producers.Select(producer =>
                        PinProducer(
                            root,
                            request.Bundle,
                            producer)).ToArray();
                MaterialGiReleaseEvidenceReport report =
                    role.Report with
                    {
                        BuildCommit = request.Bundle.BuildCommit,
                        ShaderFingerprint =
                            request.Bundle.ShaderFingerprint,
                        SettingsContractFingerprint =
                            request.Bundle.SettingsContractFingerprint,
                        Producers = producers
                    };
                string reportPath = Path.Combine(
                    generationDirectory,
                    role.Report.Role + ".json");
                artifacts.Add(
                    MaterialGiReleaseEvidenceAssembler.WriteRoleReport(
                        root,
                        reportPath,
                        report));
            }

            var bundle = request.Bundle with
            {
                Artifacts = artifacts
                    .OrderBy(
                        static artifact => artifact.Role,
                        StringComparer.Ordinal)
                    .ToArray()
            };
            MaterialGiReleaseEvidenceAssembler.WriteBundle(
                root,
                bundlePath,
                bundle);
            published = true;
            Console.WriteLine(
                $"Material-GI release evidence bundle Passed; " +
                $"roles={bundle.Artifacts.Length}, devices={bundle.Devices.Length}, " +
                $"aggregateSha256=" +
                $"{MaterialGiReleaseEvidenceContract.ComputeAggregateSha256(bundle)}, " +
                $"bundle='{bundlePath}'.");
            return 0;
        }
        finally
        {
            if (!published && Directory.Exists(generationDirectory))
                Directory.Delete(generationDirectory, recursive: true);
        }
    }

    private static int RunPinManifest(string[] args)
    {
        string? rootPath = null;
        string? manifestRelativePath = null;
        string? bundleRelativePath = null;
        string? alphaReportRelativePath = null;
        string? alphaEvidenceRelativePath = null;
        string? approvalId = null;
        string? approvedAtText = null;
        MaterialGiV2Feature enabledFeatures = MaterialGiV2Feature.All;
        var devices = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    rootPath = RequireValue(args, ref index, "--root");
                    break;
                case "--manifest":
                    manifestRelativePath =
                        RequireValue(args, ref index, "--manifest");
                    break;
                case "--bundle":
                    bundleRelativePath =
                        RequireValue(args, ref index, "--bundle");
                    break;
                case "--alpha-report":
                    alphaReportRelativePath =
                        RequireValue(args, ref index, "--alpha-report");
                    break;
                case "--alpha-evidence":
                    alphaEvidenceRelativePath =
                        RequireValue(args, ref index, "--alpha-evidence");
                    break;
                case "--approval-id":
                    approvalId =
                        RequireValue(args, ref index, "--approval-id");
                    break;
                case "--approved-at-utc":
                    approvedAtText =
                        RequireValue(args, ref index, "--approved-at-utc");
                    break;
                case "--qualified-device":
                    devices.Add(
                        RequireValue(
                            args,
                            ref index,
                            "--qualified-device"));
                    break;
                case "--enabled-features":
                    string featureText = RequireValue(
                        args,
                        ref index,
                        "--enabled-features");
                    if (!Enum.TryParse(
                            featureText,
                            ignoreCase: true,
                            out enabledFeatures))
                    {
                        throw new ArgumentException(
                            $"Unknown material-GI V2 feature mask '{featureText}'.");
                    }
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown manifest pinning option '{args[index]}'.");
            }
        }

        Require(rootPath, "--root");
        Require(manifestRelativePath, "--manifest");
        Require(bundleRelativePath, "--bundle");
        Require(alphaReportRelativePath, "--alpha-report");
        Require(alphaEvidenceRelativePath, "--alpha-evidence");
        Require(approvalId, "--approval-id");
        Require(approvedAtText, "--approved-at-utc");
        if (devices.Count < 2)
        {
            throw new ArgumentException(
                "At least two --qualified-device values are required.");
        }
        if (!string.Equals(
                approvalId,
                approvalId!.Trim(),
                StringComparison.Ordinal) ||
            approvalId.Length > 512)
        {
            throw new ArgumentException(
                "Approval ID must be canonical and at most 512 characters.");
        }
        if (!DateTimeOffset.TryParseExact(
                approvedAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset approvedAtUtc) ||
            approvedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "--approved-at-utc requires an ISO-8601 round-trip timestamp with a zero UTC offset.");
        }

        string root = Path.GetFullPath(rootPath!);
        string manifestPath = ResolveContainedRelativePath(
            root,
            manifestRelativePath!,
            "--manifest");
        string bundlePath = ResolveContainedRelativePath(
            root,
            bundleRelativePath!,
            "--bundle");
        string alphaReportPath = ResolveContainedRelativePath(
            root,
            alphaReportRelativePath!,
            "--alpha-report");
        string alphaEvidencePath = ResolveContainedRelativePath(
            root,
            alphaEvidenceRelativePath!,
            "--alpha-evidence");
        MaterialGiRolloutQualificationManifest manifest =
            MaterialGiReleaseEvidenceAssembler.WriteQualificationManifest(
                root,
                manifestPath,
                bundlePath,
                alphaReportPath,
                alphaEvidencePath,
                devices,
                approvalId!,
                approvedAtUtc,
                enabledFeatures);
        Console.WriteLine(
            $"Authenticated material-GI qualification manifest pinned; " +
            $"approval='{manifest.ApprovalId}', " +
            $"devices={manifest.QualifiedDeviceIds.Length}, " +
            $"evidenceSha256={manifest.EvidenceSha256}, " +
            $"manifest='{manifestPath}'.");
        return 0;
    }

    private static int RunVerifyManifest(string[] args)
    {
        string? manifestPath = null;
        DateOnly? evaluationDate = null;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--manifest":
                    manifestPath =
                        RequireValue(args, ref index, "--manifest");
                    break;
                case "--evaluation-date":
                    string value = RequireValue(
                        args,
                        ref index,
                        "--evaluation-date");
                    if (!DateOnly.TryParseExact(
                            value,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateOnly parsed))
                    {
                        throw new ArgumentException(
                            "--evaluation-date requires yyyy-MM-dd.");
                    }
                    evaluationDate = parsed;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown manifest verification option '{args[index]}'.");
            }
        }
        Require(manifestPath, "--manifest");
        MaterialGiRolloutQualificationManifest manifest =
            MaterialGiRolloutQualificationManifest.Load(
                Path.GetFullPath(manifestPath!));
        IReadOnlyList<string> failures = manifest.Validate(
            evaluationDate ??
            DateOnly.FromDateTime(DateTime.UtcNow));
        if (failures.Count != 0)
        {
            throw new InvalidDataException(
                "Qualification manifest failed verification: " +
                string.Join(" ", failures));
        }
        Console.WriteLine(
            $"Material-GI qualification manifest Passed; " +
            $"approval='{manifest.ApprovalId}', " +
            $"roles={manifest.AuthenticatedReleaseEvidenceRoleCount}, " +
            $"tierDevices={manifest.AuthenticatedTierDeviceCount}, " +
            $"lowerMemoryRayQueryDevices=" +
            $"{manifest.AuthenticatedLowerMemoryRayQueryDeviceCount}, " +
            $"recovery={manifest.AuthenticatedRecoveryCapabilitySummary}.");
        return 0;
    }

    private static MaterialGiProducerEvidenceArtifact PinProducer(
        string root,
        MaterialGiReleaseEvidenceBundle bundle,
        MaterialGiEvidenceAssemblyProducer producer)
    {
        MaterialGiEvidenceDeviceIdentity device =
            bundle.Devices.SingleOrDefault(device =>
                string.Equals(
                    device.DeviceId,
                    producer.DeviceId,
                    StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"Producer device '{producer.DeviceId}' is absent from the bundle device table.");
        string producerPath = ResolveContainedRelativePath(
            root,
            producer.ManifestRelativePath,
            $"producer '{producer.Kind}'");
        return MaterialGiReleaseEvidenceAssembler.PinProducer(
            root,
            producerPath,
            producer.Kind,
            producer.Schema,
            device,
            bundle.BuildCommit,
            bundle.ShaderFingerprint,
            producer.SettingsFingerprint,
            producer.QualityTier);
    }

    private static void ValidateAssemblyRequest(
        MaterialGiEvidenceAssemblyRequest request)
    {
        if (request.SchemaVersion != AssemblyRequestSchemaVersion)
        {
            throw new InvalidDataException(
                $"Evidence assembly request schema {request.SchemaVersion} is unsupported.");
        }
        if (request.Bundle is null ||
            request.Bundle.SchemaVersion !=
                MaterialGiReleaseEvidenceContract.BundleSchemaVersion ||
            request.Bundle.Artifacts is null ||
            request.Bundle.Artifacts.Length != 0)
        {
            throw new InvalidDataException(
                "Assembly request Bundle must use the current schema and leave Artifacts empty for computed pins.");
        }
        if (request.Roles is null ||
            request.Roles.Length !=
                MaterialGiReleaseEvidenceContract.RequiredRoles.Count)
        {
            throw new InvalidDataException(
                "Assembly request must contain every required evidence role exactly once.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (MaterialGiEvidenceAssemblyRole role in request.Roles)
        {
            if (role is null ||
                role.Report is null ||
                !MaterialGiReleaseEvidenceContract.RequiredRoles.Contains(
                    role.Report.Role,
                    StringComparer.Ordinal) ||
                !roles.Add(role.Report.Role) ||
                role.Report.Producers is null ||
                role.Report.Producers.Length != 0 ||
                role.Producers is null ||
                role.Producers.Length == 0)
            {
                throw new InvalidDataException(
                    "Assembly request contains a null, unknown, duplicate, pre-pinned, or producer-free role.");
            }
        }
        if (!roles.SetEquals(
                MaterialGiReleaseEvidenceContract.RequiredRoles))
        {
            throw new InvalidDataException(
                "Assembly request does not contain every required role exactly once.");
        }
    }

    private static byte[] ReadStableBounded(
        string path,
        int maximumBytes,
        string role)
    {
        string fullPath = Path.GetFullPath(path);
        using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength <= 0 || admittedLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"{role} has an invalid bounded length.");
        }
        var bytes = new byte[checked((int)admittedLength)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new IOException(
                $"{role} changed length while it was being read.");
        }
        return bytes;
    }

    private static void RejectDuplicateJsonProperties(
        ReadOnlySpan<byte> bytes,
        string role)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(
                        new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.PropertyName:
                    if (!containers.TryPeek(
                            out HashSet<string>? properties) ||
                        properties is null ||
                        !properties.Add(
                            reader.GetString() ??
                            throw new InvalidDataException(
                                $"{role} contains a null property name.")))
                    {
                        throw new InvalidDataException(
                            $"{role} contains a duplicate or misplaced property.");
                    }
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"{role} contains unbalanced JSON.");
                    }
                    containers.Pop();
                    break;
            }
        }
        if (containers.Count != 0)
            throw new InvalidDataException($"{role} contains unbalanced JSON.");
    }

    private static string ResolveContainedRelativePath(
        string root,
        string relativePath,
        string role)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !string.Equals(
                relativePath,
                relativePath.Trim(),
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Split(
                ['/', '\\'],
                StringSplitOptions.None)
                .Any(static segment =>
                    segment.Length == 0 ||
                    segment is "." or ".."))
        {
            throw new ArgumentException(
                $"{role} must be a canonical root-relative path.");
        }
        string path = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));
        string boundary = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                boundary,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{role} resolves outside the evidence root.");
        }
        return path;
    }

    private static string RequireValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Count ||
            string.IsNullOrWhiteSpace(args[index + 1]) ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        index++;
        return args[index];
    }

    private static void Require(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{option} is required.");
    }

    private sealed record MaterialGiEvidenceAssemblyRequest
    {
        public int SchemaVersion { get; init; }
        public MaterialGiReleaseEvidenceBundle Bundle { get; init; } =
            new();
        public MaterialGiEvidenceAssemblyRole[] Roles { get; init; } =
            Array.Empty<MaterialGiEvidenceAssemblyRole>();
    }

    private sealed record MaterialGiEvidenceAssemblyRole
    {
        public MaterialGiReleaseEvidenceReport Report { get; init; } =
            new();
        public MaterialGiEvidenceAssemblyProducer[] Producers { get; init; } =
            Array.Empty<MaterialGiEvidenceAssemblyProducer>();
    }

    private sealed record MaterialGiEvidenceAssemblyProducer
    {
        public string ManifestRelativePath { get; init; } =
            string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Schema { get; init; } = string.Empty;
        public string DeviceId { get; init; } = string.Empty;
        public string SettingsFingerprint { get; init; } =
            string.Empty;
        public string QualityTier { get; init; } = string.Empty;
    }
}
