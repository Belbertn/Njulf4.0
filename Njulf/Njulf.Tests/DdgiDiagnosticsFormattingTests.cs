using System.IO;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiDiagnosticsFormattingTests
    {
        [Test]
        public void SampleDiagnosticsReporter_UsesExplicitDdgiEstimateNames()
        {
            string reporter = File.ReadAllText(Path.Combine("..", "..", "..", "..", "NjulfHelloGame", "SampleDiagnosticsReporter.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(reporter, Does.Contain("ddgiEstimate spatial/support/data/visibility/leak/effective/rawLum/finalLum/ownership/reloc/inactive"));
                Assert.That(reporter, Does.Contain("ddgiDispatchCapacity"));
                Assert.That(reporter, Does.Contain("ddgiActualRequests"));
                Assert.That(reporter, Does.Contain("readback={FormatReadbackStatus(diagnostics)}"));
                Assert.That(reporter, Does.Not.Contain("ddgiEstimate coverage/visible/effective"));
            });
        }

        [Test]
        public void DdgiDiagnosticsDocumentation_IncludesTroubleshootingMatrix()
        {
            string docs = File.ReadAllText(Path.Combine("..", "..", "..", "..", "docs", "rendering", "ddgi-diagnostics.md"));

            Assert.Multiple(() =>
            {
                Assert.That(docs, Does.Contain("spatial` high, `support` low"));
                Assert.That(docs, Does.Contain("rawLum` high, `finalLum` low"));
                Assert.That(docs, Does.Contain("ownership` must also be `0.000`"));
            });
        }
    }
}
