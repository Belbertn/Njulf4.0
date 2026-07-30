using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CookedRuntimePolicyTests
{
    [TestCase(CookedRuntimePolicy.AllowSourceFallbackVariable)]
    [TestCase(CookedRuntimePolicy.StrictVariable)]
    [TestCase(CookedRuntimePolicy.RequireSignatureVariable)]
    public void ConfiguredInvalidBoolean_FailsClosed(string variable)
    {
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "tru");

            InvalidOperationException failure =
                Assert.Throws<InvalidOperationException>(
                    () => CookedRuntimePolicy.IsEnvironmentEnabled(
                        variable,
                        defaultValue: false))!;

            Assert.Multiple(() =>
            {
                Assert.That(failure.Message, Does.Contain(variable));
                Assert.That(failure.Message, Does.Contain("tru"));
                Assert.That(failure.Message, Does.Contain("true/false"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Test]
    public void AcceptedBooleanSpellingsRemainExplicit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                CookedRuntimePolicy.ParseBooleanSetting(
                    "test",
                    " true ",
                    defaultValue: false),
                Is.True);
            Assert.That(
                CookedRuntimePolicy.ParseBooleanSetting(
                    "test",
                    "OFF",
                    defaultValue: true),
                Is.False);
            Assert.That(
                CookedRuntimePolicy.ParseBooleanSetting(
                    "test",
                    null,
                    defaultValue: true),
                Is.True);
            Assert.That(
                () => CookedRuntimePolicy.ParseBooleanSetting(
                    "test",
                    " ",
                    defaultValue: false),
                Throws.TypeOf<InvalidOperationException>());
        });
    }
}
