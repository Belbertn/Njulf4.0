using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CookedRuntimePolicyTests
{
    [Test]
    public void SourceFallback_DefaultMatchesBuildTierAndExplicitOverrideWins()
    {
        string? previous = Environment.GetEnvironmentVariable(
            CookedRuntimePolicy.AllowSourceFallbackVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                CookedRuntimePolicy.AllowSourceFallbackVariable,
                null);
#if NJULF_DEVELOPMENT
            Assert.That(CookedRuntimePolicy.AllowSourceFallback, Is.True);
#else
            Assert.That(CookedRuntimePolicy.AllowSourceFallback, Is.False);
#endif

            Environment.SetEnvironmentVariable(
                CookedRuntimePolicy.AllowSourceFallbackVariable,
                "false");
            Assert.That(CookedRuntimePolicy.AllowSourceFallback, Is.False);

            Environment.SetEnvironmentVariable(
                CookedRuntimePolicy.AllowSourceFallbackVariable,
                "true");
            Assert.That(CookedRuntimePolicy.AllowSourceFallback, Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CookedRuntimePolicy.AllowSourceFallbackVariable,
                previous);
        }
    }

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
