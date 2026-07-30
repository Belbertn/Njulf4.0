using Njulf.Rendering.Diagnostics;

namespace Njulf.AssetTool;

internal static class AlphaVisibilityGateCommand
{
    public static int Run(string[] args)
    {
        string? reportPath = null;
        string? evidencePath = null;
        bool verifyOnly = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--report":
                    reportPath = RequireValue(args, ref index, "--report");
                    break;
                case "--evidence":
                    evidencePath = RequireValue(args, ref index, "--evidence");
                    break;
                case "--verify":
                    verifyOnly = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown alpha-visibility gate option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(reportPath) ||
            string.IsNullOrWhiteSpace(evidencePath))
        {
            throw new ArgumentException(
                "alpha-visibility-gate requires --report <json> --evidence <bin>.");
        }

        reportPath = Path.GetFullPath(reportPath);
        evidencePath = Path.GetFullPath(evidencePath);
        if (!string.Equals(
                Path.GetExtension(reportPath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Alpha-visibility report path must end in .json.");
        }
        if (!string.Equals(
                Path.GetExtension(evidencePath),
                ".bin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Alpha-visibility evidence path must end in .bin.");
        }
        if (string.Equals(reportPath, evidencePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Alpha-visibility report and evidence paths must be distinct.");

        if (verifyOnly)
        {
            AlphaVisibilityConformanceReport authenticated =
                AlphaVisibilityConformanceReports.AuthenticatePassed(
                    reportPath,
                    evidencePath);
            Console.WriteLine(
                $"Authenticated alpha-visibility gate Passed on " +
                $"'{authenticated.DeviceName}' with " +
                $"{authenticated.Distances.Count} deterministic distance(s).");
            return 0;
        }

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            AlphaVisibilityHardwareOutput hardware =
                AlphaVisibilityVulkanHarness.Run();
            AlphaVisibilityRawEvidence raw =
                AlphaVisibilityRawEvidence.FromGpuWords(hardware.ResultWords);
            byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
            AlphaVisibilityConformanceReport report =
                AlphaVisibilityConformanceReports.Create(
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    hardware,
                    Path.GetFileName(evidencePath),
                    evidence);
            AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
                evidencePath,
                evidence);
            AlphaVisibilityConformanceReports.WriteAtomically(
                reportPath,
                report);

            foreach (AlphaVisibilityDistanceResult distance in report.Distances)
            {
                Console.WriteLine(
                    $"distance={distance.Distance:R} " +
                    $"raster={distance.RasterCoveredCount}/{distance.RasterCandidateCount} " +
                    $"({distance.RasterCoverage:P3}) " +
                    $"ray={distance.RayCoveredCount}/{distance.RayCandidateCount} " +
                    $"({distance.RayCoverage:P3}) " +
                    $"difference={distance.AbsoluteCoverageDifference:P3} " +
                    $"status={(distance.Passed ? "Passed" : "Failed")}");
            }
            foreach (AlphaVisibilityValidationMessage message in
                     report.ValidationMessages)
            {
                Console.Error.WriteLine(
                    $"validation severity={message.Severity} " +
                    $"types=0x{message.MessageTypes:x8} " +
                    $"id={message.MessageIdNumber} " +
                    $"idName='{message.MessageIdName}' " +
                    $"textTruncated={message.TextTruncated}: {message.Message}");
            }
            if (report.ValidationMessagesTruncated)
            {
                Console.Error.WriteLine(
                    "validation diagnostics truncated after the bounded retention limit.");
            }
            Console.WriteLine(
                $"Alpha-visibility gate {report.Status}; " +
                $"report='{reportPath}', evidence='{evidencePath}'.");
            return string.Equals(report.Status, "Passed", StringComparison.Ordinal)
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            AlphaVisibilityConformanceReport failed =
                AlphaVisibilityConformanceReports.CreateFailed(
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    $"{exception.GetType().Name}: {exception.Message}");
            AlphaVisibilityConformanceReports.WriteAtomically(
                reportPath,
                failed);
            Console.Error.WriteLine(
                $"Alpha-visibility gate Failed: {exception.GetType().Name}: " +
                exception.Message);
            Console.Error.WriteLine($"Failed report='{reportPath}'.");
            return 1;
        }
    }

    private static string RequireValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Count ||
            string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        index++;
        return args[index];
    }
}
