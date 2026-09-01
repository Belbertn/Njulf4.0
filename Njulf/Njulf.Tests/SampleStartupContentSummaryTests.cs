using Njulf.Assets.Cooked;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleStartupContentSummaryTests
{
    [Test]
    public void SummaryReportsOnlyInitialSceneFallbackDelta()
    {
        CookedContentDiagnostics before = CreateDiagnostics(
            cookedAssets: 3,
            sourceFallbacks: 2);
        CookedContentDiagnostics after = CreateDiagnostics(
            cookedAssets: 4,
            sourceFallbacks: 4);

        (string message, bool warning) =
            HelloGame.FormatInitialContentSummary(before, after);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.True);
            Assert.That(message,
                Is.EqualTo(
                    "WARNING [Njulf.Content]: initial scene used 2 source " +
                    "import fallback(s); startup timing is degraded."));
        });
    }

    [Test]
    public void SummaryReportsCookedDeltaWhenNoFallbackOccurs()
    {
        CookedContentDiagnostics before = CreateDiagnostics(
            cookedAssets: 1,
            sourceFallbacks: 1);
        CookedContentDiagnostics after = CreateDiagnostics(
            cookedAssets: 3,
            sourceFallbacks: 1);

        (string message, bool warning) =
            HelloGame.FormatInitialContentSummary(before, after);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.False);
            Assert.That(message,
                Is.EqualTo(
                    "Cooked content: initial scene used no source import " +
                    "fallback (cooked models=2)."));
        });
    }

    private static CookedContentDiagnostics CreateDiagnostics(
        int cookedAssets,
        int sourceFallbacks) =>
        new(
            cookedAssets,
            CookedBytesRead: 0,
            CookedLoadMilliseconds: 0,
            CookedUploadMilliseconds: 0,
            sourceFallbacks,
            VersionOrHashMismatchCount: 0,
            Array.Empty<CookedContentDiagnosticEntry>());
}
