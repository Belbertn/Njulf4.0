using Njulf.Rendering.Data;

namespace Njulf.AssetTool;

internal static class MaterialGiTestMatrixCommand
{
    public static int Run(string[] args)
    {
        string? outputPath = null;
        string? buildCommit = null;
        string? shaderFingerprint = null;
        string? settingsFingerprint = null;
        string? deviceId = null;
        string? gpuName = null;
        string? driverVersion = null;
        bool attestReleaseBuild = false;
        var trxInputs = new List<(string Name, string Path)>();

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out":
                    outputPath = RequireValue(args, ref index, "--out");
                    break;
                case "--build-commit":
                    buildCommit = RequireValue(
                        args,
                        ref index,
                        "--build-commit");
                    break;
                case "--shader-fingerprint":
                    shaderFingerprint = RequireValue(
                        args,
                        ref index,
                        "--shader-fingerprint");
                    break;
                case "--settings-fingerprint":
                    settingsFingerprint = RequireValue(
                        args,
                        ref index,
                        "--settings-fingerprint");
                    break;
                case "--device-id":
                    deviceId = RequireValue(args, ref index, "--device-id");
                    break;
                case "--gpu-name":
                    gpuName = RequireValue(args, ref index, "--gpu-name");
                    break;
                case "--driver-version":
                    driverVersion = RequireValue(
                        args,
                        ref index,
                        "--driver-version");
                    break;
                case "--trx":
                    trxInputs.Add(
                        ParseNamedPath(
                            RequireValue(args, ref index, "--trx")));
                    break;
                case "--attest-release-build":
                    if (attestReleaseBuild)
                    {
                        throw new ArgumentException(
                            "--attest-release-build may be specified only once.");
                    }
                    attestReleaseBuild = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown material-GI test-matrix option '{args[index]}'.");
            }
        }

        Require(outputPath, "--out");
        Require(buildCommit, "--build-commit");
        Require(shaderFingerprint, "--shader-fingerprint");
        Require(settingsFingerprint, "--settings-fingerprint");
        Require(deviceId, "--device-id");
        Require(gpuName, "--gpu-name");
        Require(driverVersion, "--driver-version");
        if (!attestReleaseBuild)
        {
            throw new ArgumentException(
                "--attest-release-build is required and must only be supplied after a successful Release build command.");
        }
        if (trxInputs.Count == 0)
            throw new ArgumentException("At least one --trx <check>=<path> input is required.");

        outputPath = Path.GetFullPath(outputPath!);
        if (!string.Equals(
                Path.GetExtension(outputPath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Material-GI test-matrix output path must end in .json.");
        }

        var inputNames = new HashSet<string>(StringComparer.Ordinal);
        var inputPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var results = new List<MaterialGiTestMatrixProducerResult>(
            trxInputs.Count + 1)
        {
            MaterialGiTestMatrixBuilder.CreateAttestedBuildResult(
                "ReleaseBuild")
        };
        foreach ((string name, string pathValue) in trxInputs)
        {
            if (!inputNames.Add(name))
            {
                throw new ArgumentException(
                    $"TRX check '{name}' is duplicated.");
            }
            string path = Path.GetFullPath(pathValue);
            if (!inputPaths.Add(path))
            {
                throw new ArgumentException(
                    $"TRX path '{path}' is duplicated across checks.");
            }
            if (string.Equals(
                    outputPath,
                    path,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Test-matrix output cannot overwrite a TRX input.");
            }
            results.Add(
                MaterialGiTestMatrixBuilder.ReadTrxResult(name, path));
        }

        var device = new MaterialGiEvidenceDeviceIdentity
        {
            DeviceId = deviceId!,
            GpuName = gpuName!,
            DriverVersion = driverVersion!
        };
        MaterialGiTestMatrixProducerReport report =
            MaterialGiTestMatrixBuilder.CreateReport(
                buildCommit!,
                shaderFingerprint!,
                settingsFingerprint!,
                device,
                results);
        MaterialGiReleaseEvidenceAssembler.WriteTestMatrixReport(
            outputPath,
            report);
        foreach (MaterialGiTestMatrixProducerResult result in report.Results)
        {
            Console.WriteLine(
                $"{result.Name}: {result.Status}; " +
                $"passed={result.PassedCount}, failed={result.FailedCount}, " +
                $"skipped={result.SkippedCount}");
        }
        Console.WriteLine(
            $"Material-GI test matrix {report.Status}; report='{outputPath}'.");
        return string.Equals(
            report.Status,
            MaterialGiReleaseEvidenceContract.PassedStatus,
            StringComparison.Ordinal)
            ? 0
            : 1;
    }

    private static (string Name, string Path) ParseNamedPath(string value)
    {
        int separator = value.IndexOf('=');
        if (separator <= 0 ||
            separator == value.Length - 1 ||
            value.IndexOf('=', separator + 1) >= 0)
        {
            throw new ArgumentException(
                "--trx requires a single <check>=<path> value.");
        }
        string name = value[..separator];
        string path = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(path) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "--trx check and path values must be non-empty and canonical.");
        }
        return (name, path);
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
}
