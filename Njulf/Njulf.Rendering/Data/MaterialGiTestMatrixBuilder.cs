using System;
using System.Globalization;
using System.IO;
using System.Xml;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Data;

/// <summary>
/// Converts bounded VSTest TRX output into the strict producer result consumed
/// by material-GI release evidence. A passed result requires at least one
/// executed test, a completed test run, and zero failed or skipped results.
/// </summary>
public static class MaterialGiTestMatrixBuilder
{
    private const int MaximumTrxBytes =
        MaterialGiReleaseEvidenceContract.MaximumArtifactBytes;
    private const long MaximumXmlCharacters = MaximumTrxBytes;

    public static MaterialGiTestMatrixProducerResult ReadTrxResult(
        string name,
        string trxPath)
    {
        string normalizedName = RequireResultName(name);
        byte[] bytes = BoundedFileReader.ReadStable(
            trxPath,
            MaximumTrxBytes,
            $"Test-matrix TRX '{normalizedName}'");

        int passed = 0;
        int failed = 0;
        int skipped = 0;
        int resultCount = 0;
        int? declaredTotal = null;
        int? declaredPassed = null;
        string? summaryOutcome = null;
        bool sawTestRun = false;

        var settings = new XmlReaderSettings
        {
            Async = false,
            CloseInput = true,
            ConformanceLevel = ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumXmlCharacters,
            XmlResolver = null
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "TestRun":
                    if (reader.Depth != 0 || sawTestRun)
                    {
                        throw new InvalidDataException(
                            $"TRX result '{normalizedName}' has an invalid TestRun root.");
                    }
                    sawTestRun = true;
                    break;

                case "ResultSummary":
                    if (summaryOutcome is not null)
                    {
                        throw new InvalidDataException(
                            $"TRX result '{normalizedName}' contains duplicate ResultSummary elements.");
                    }
                    summaryOutcome = RequireAttribute(
                        reader,
                        "outcome",
                        normalizedName);
                    break;

                case "Counters":
                    if (declaredTotal is not null)
                    {
                        throw new InvalidDataException(
                            $"TRX result '{normalizedName}' contains duplicate Counters elements.");
                    }
                    declaredTotal = ParseNonNegativeInt32(
                        RequireAttribute(reader, "total", normalizedName),
                        "total",
                        normalizedName);
                    declaredPassed = ParseNonNegativeInt32(
                        RequireAttribute(reader, "passed", normalizedName),
                        "passed",
                        normalizedName);
                    break;

                case "UnitTestResult":
                    string outcome = RequireAttribute(
                        reader,
                        "outcome",
                        normalizedName);
                    resultCount = checked(resultCount + 1);
                    if (string.Equals(outcome, "Passed", StringComparison.Ordinal))
                    {
                        passed = checked(passed + 1);
                    }
                    else if (string.Equals(
                                 outcome,
                                 "NotExecuted",
                                 StringComparison.Ordinal) ||
                             string.Equals(
                                 outcome,
                                 "Inconclusive",
                                 StringComparison.Ordinal))
                    {
                        skipped = checked(skipped + 1);
                    }
                    else
                    {
                        failed = checked(failed + 1);
                    }
                    break;
            }
        }

        if (!sawTestRun ||
            summaryOutcome is null ||
            declaredTotal is null ||
            declaredPassed is null)
        {
            throw new InvalidDataException(
                $"TRX result '{normalizedName}' is missing its TestRun, ResultSummary, or Counters contract.");
        }
        if (declaredTotal != resultCount || declaredPassed != passed)
        {
            throw new InvalidDataException(
                $"TRX result '{normalizedName}' counters do not match its concrete UnitTestResult entries.");
        }

        bool completed =
            string.Equals(summaryOutcome, "Completed", StringComparison.Ordinal) ||
            string.Equals(summaryOutcome, "Passed", StringComparison.Ordinal);
        bool succeeded =
            completed &&
            passed > 0 &&
            failed == 0 &&
            skipped == 0;
        return new MaterialGiTestMatrixProducerResult
        {
            Name = normalizedName,
            Status = succeeded
                ? MaterialGiReleaseEvidenceContract.PassedStatus
                : "Failed",
            PassedCount = passed,
            FailedCount = failed + (completed ? 0 : 1),
            SkippedCount = skipped
        };
    }

    public static MaterialGiTestMatrixProducerReport CreateReport(
        string buildCommit,
        string shaderFingerprint,
        string settingsFingerprint,
        MaterialGiEvidenceDeviceIdentity device,
        IEnumerable<MaterialGiTestMatrixProducerResult> results)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(results);
        string normalizedCommit = RequireGitCommit(buildCommit);
        string normalizedShader =
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                shaderFingerprint);
        string normalizedSettings =
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                settingsFingerprint);
        MaterialGiEvidenceDeviceIdentity normalizedDevice =
            NormalizeDevice(device);
        MaterialGiTestMatrixProducerResult[] normalizedResults =
            results.Select(NormalizeResult).ToArray();

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (normalizedResults.Length !=
                MaterialGiReleaseEvidenceContract
                    .RequiredOracleReleaseChecks.Count ||
            normalizedResults.Any(result => !names.Add(result.Name)) ||
            !names.SetEquals(
                MaterialGiReleaseEvidenceContract
                    .RequiredOracleReleaseChecks))
        {
            throw new InvalidDataException(
                "Test-matrix results must contain every required CPU/GPU oracle and Release build/test check exactly once.");
        }

        bool passed = normalizedResults.All(static result =>
            string.Equals(
                result.Status,
                MaterialGiReleaseEvidenceContract.PassedStatus,
                StringComparison.Ordinal) &&
            result.PassedCount > 0 &&
            result.FailedCount == 0 &&
            result.SkippedCount == 0);
        return new MaterialGiTestMatrixProducerReport
        {
            Status = passed
                ? MaterialGiReleaseEvidenceContract.PassedStatus
                : "Failed",
            BuildConfiguration = "Release",
            BuildCommit = normalizedCommit,
            ShaderFingerprint = normalizedShader,
            SettingsFingerprint = normalizedSettings,
            Device = normalizedDevice,
            Results = normalizedResults
                .OrderBy(static result => result.Name, StringComparer.Ordinal)
                .ToArray()
        };
    }

    public static MaterialGiTestMatrixProducerResult CreateAttestedBuildResult(
        string name)
    {
        string normalizedName = RequireResultName(name);
        if (!string.Equals(
                normalizedName,
                "ReleaseBuild",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only ReleaseBuild may be represented by an explicit successful-command attestation; test and oracle checks require TRX input.",
                nameof(name));
        }
        return new MaterialGiTestMatrixProducerResult
        {
            Name = normalizedName,
            Status = MaterialGiReleaseEvidenceContract.PassedStatus,
            PassedCount = 1,
            FailedCount = 0,
            SkippedCount = 0
        };
    }

    private static MaterialGiTestMatrixProducerResult NormalizeResult(
        MaterialGiTestMatrixProducerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string name = RequireResultName(result.Name);
        if (result.PassedCount < 0 ||
            result.FailedCount < 0 ||
            result.SkippedCount < 0)
        {
            throw new InvalidDataException(
                $"Test-matrix result '{name}' contains a negative count.");
        }
        string status =
            string.Equals(
                result.Status,
                MaterialGiReleaseEvidenceContract.PassedStatus,
                StringComparison.Ordinal)
                ? MaterialGiReleaseEvidenceContract.PassedStatus
                : "Failed";
        return result with
        {
            Name = name,
            Status = status
        };
    }

    private static string RequireResultName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 128 ||
            !MaterialGiReleaseEvidenceContract
                .RequiredOracleReleaseChecks.Contains(
                    value,
                    StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown or non-canonical test-matrix result name '{value}'.",
                nameof(value));
        }
        return value;
    }

    private static MaterialGiEvidenceDeviceIdentity NormalizeDevice(
        MaterialGiEvidenceDeviceIdentity device) =>
        new()
        {
            DeviceId = RequireIdentity(device.DeviceId, "device ID"),
            GpuName = RequireIdentity(device.GpuName, "GPU name"),
            DriverVersion = RequireIdentity(
                device.DriverVersion,
                "driver version")
        };

    private static string RequireGitCommit(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 40 ||
            normalized.Any(
                static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Build commit must be an exact 40-character Git commit.",
                nameof(value));
        }
        return normalized;
    }

    private static string RequireIdentity(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\0') ||
            value.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(
                "unavailable:",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Test-matrix {role} is absent, non-canonical, or unavailable.");
        }
        return value;
    }

    private static string RequireAttribute(
        XmlReader reader,
        string name,
        string resultName)
    {
        string? value = reader.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"TRX result '{resultName}' has a missing or invalid '{name}' attribute.");
        }
        return value;
    }

    private static int ParseNonNegativeInt32(
        string value,
        string name,
        string resultName)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < 0)
        {
            throw new InvalidDataException(
                $"TRX result '{resultName}' has an invalid '{name}' counter.");
        }
        return parsed;
    }
}
