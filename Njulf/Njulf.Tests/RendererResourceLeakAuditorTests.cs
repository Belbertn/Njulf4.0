using System.Linq;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererResourceLeakAuditorTests
{
    [Test]
    public void WithinTolerancePasses()
    {
        var auditor = new RendererResourceLeakAuditor();
        var before = new RendererResourceLeakSnapshot(1, 1, 1, 1, 100, 1000, 0);
        var after = new RendererResourceLeakSnapshot(1, 1, 1, 1, 100, 1050, 0);

        Assert.That(auditor.Compare(before, after).All(finding => finding.Passed), Is.True);
    }

    [Test]
    public void OverToleranceFailsWithCategory()
    {
        var auditor = new RendererResourceLeakAuditor();
        var before = new RendererResourceLeakSnapshot(1, 1, 1, 1, 100, 1000, 0);
        var after = new RendererResourceLeakSnapshot(2, 1, 1, 1, 100, 1300, 0);

        var findings = auditor.Compare(before, after);

        Assert.That(findings.Any(finding => finding.Category == "meshes" && !finding.Passed), Is.True);
        Assert.That(findings.Any(finding => finding.Category == "managed bytes" && !finding.Passed), Is.True);
    }
}
