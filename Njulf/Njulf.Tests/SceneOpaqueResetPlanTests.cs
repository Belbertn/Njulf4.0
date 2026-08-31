using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneOpaqueResetPlanTests
{
    [Test]
    public void IndirectOnlyFrame_DoesNotClearUnobservablePayloads()
    {
        SceneOpaqueResetPlan plan = SceneOpaqueResetPlan.Create(
            indirectDispatchEnabled: true,
            validationReadsPayload: false,
            directionalCascadeCount: 4,
            staticShadowCascadeMask: 0b1111u,
            dynamicShadowCompactionActive: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ClearPayloads, Is.False);
            Assert.That(plan.ClearsStaticShadowCascade(0), Is.False);
            Assert.That(plan.ClearsDynamicShadowCascade(3), Is.False);
        });
    }

    [Test]
    public void ValidationFrame_ClearsOnlyPublishedShadowCascades()
    {
        SceneOpaqueResetPlan plan = SceneOpaqueResetPlan.Create(
            indirectDispatchEnabled: true,
            validationReadsPayload: true,
            directionalCascadeCount: 3,
            staticShadowCascadeMask: 0b0101u,
            dynamicShadowCompactionActive: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ClearPayloads, Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(0), Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(1), Is.False);
            Assert.That(plan.ClearsStaticShadowCascade(2), Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(3), Is.False);
            Assert.That(plan.ClearsDynamicShadowCascade(2), Is.True);
            Assert.That(plan.ClearsDynamicShadowCascade(3), Is.False);
        });
    }

    [Test]
    public void DirectFallback_ClearsPayloadsWithoutInactiveDynamicShadows()
    {
        SceneOpaqueResetPlan plan = SceneOpaqueResetPlan.Create(
            indirectDispatchEnabled: false,
            validationReadsPayload: false,
            directionalCascadeCount: 2,
            staticShadowCascadeMask: 0b1111u,
            dynamicShadowCompactionActive: false);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ClearPayloads, Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(0), Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(1), Is.True);
            Assert.That(plan.ClearsStaticShadowCascade(2), Is.False);
            Assert.That(plan.ClearsDynamicShadowCascade(0), Is.False);
        });
    }
}
