using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingSampleTelemetryTests
{
    [Test]
    public void TryCreate_ValidatesCountsAndDerivesPdfExtremaAndQuantiles()
    {
        uint[] words = CreateValidWords();

        bool valid = SimpleDdgiGuidingSampleTelemetry.TryCreate(
            words,
            requestCount: 100U,
            validation: default,
            out SimpleDdgiGuidingSampleTelemetry telemetry,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True, reason);
            Assert.That(telemetry.ValidSampleCount, Is.EqualTo(100U));
            Assert.That(telemetry.BootstrapInvalidationCount, Is.Zero);
            Assert.That(telemetry.MaintenanceSampleCount, Is.EqualTo(8U));
            Assert.That(telemetry.MixtureUniformSampleCount, Is.EqualTo(22U));
            Assert.That(telemetry.MixtureGuidedSampleCount, Is.EqualTo(70U));
            Assert.That(telemetry.UniformFallbackSampleCount, Is.EqualTo(5U));
            Assert.That(telemetry.MinimumPdf, Is.EqualTo(1.0f / 6.0f).Within(1e-6f));
            Assert.That(telemetry.MaximumPdf, Is.EqualTo(0.8f));
            Assert.That(telemetry.MinimumInversePdf, Is.EqualTo(1.25f).Within(1e-6f));
            Assert.That(telemetry.P50InversePdfUpperBound, Is.EqualTo(2.0f));
            Assert.That(telemetry.P95InversePdfUpperBound, Is.EqualTo(4.0f));
            Assert.That(telemetry.P99InversePdfUpperBound, Is.EqualTo(8.0f));
            Assert.That(telemetry.MaximumInversePdf, Is.EqualTo(6.0f));
            Assert.That(telemetry.InversePdfHistogram.Total, Is.EqualTo(100UL));
        });
    }

    [Test]
    public void TryCreate_AccountsIntentionalGpuBootstrapInvalidations()
    {
        var words = new uint[checked((int)
            SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount)];
        words[(int)SimpleDdgiGuidingGpuAbi.CounterBootstrapInvalidations] =
            100U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterGpuSampleRequestCount] =
            100U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterGpuPreparationStatus] =
            SimpleDdgiGuidingGpuAbi.Version;

        bool valid = SimpleDdgiGuidingSampleTelemetry.TryCreate(
            words,
            requestCount: 100U,
            validation: default,
            out SimpleDdgiGuidingSampleTelemetry telemetry,
            out string reason,
            gpuGenerated: true);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True, reason);
            Assert.That(telemetry.RequestCount, Is.EqualTo(100U));
            Assert.That(telemetry.ValidSampleCount, Is.Zero);
            Assert.That(telemetry.BootstrapInvalidationCount,
                Is.EqualTo(100U));
            Assert.That(telemetry.InversePdfHistogram.Total, Is.Zero);
            Assert.That(telemetry.IsConsistent(default), Is.True);
        });
    }

    [Test]
    public void TryCreate_RejectsPartialOrStaleCounterPayloads()
    {
        uint[] words = CreateValidWords();
        words[31] = 1U;

        bool valid = SimpleDdgiGuidingSampleTelemetry.TryCreate(
            words,
            requestCount: 100U,
            validation: default,
            out _,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.False);
            Assert.That(reason, Is.EqualTo(
                "guiding-sample-telemetry-count-mismatch"));
        });
    }

    [Test]
    public void TryCreate_AcceptsMeasuredInvalidSampleButDoesNotHideIt()
    {
        uint[] words = CreateValidWords();
        words[(int)SimpleDdgiGuidingGpuAbi.CounterValidSamples] = 99U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMixtureGuidedSamples] = 69U;
        words[9 + 12] = 44U;
        var validation = new SimpleDdgiGuidingValidationCounters(
            InvalidRecords: 1U,
            InvalidHeaders: 0U,
            InvalidPdfs: 0U,
            PublicationRejections: 0U);

        bool valid = SimpleDdgiGuidingSampleTelemetry.TryCreate(
            words,
            requestCount: 100U,
            validation,
            out SimpleDdgiGuidingSampleTelemetry telemetry,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True, reason);
            Assert.That(validation.AreZero, Is.False);
            Assert.That(telemetry.ValidSampleCount, Is.EqualTo(99U));
            Assert.That(telemetry.InversePdfHistogram.Total, Is.EqualTo(99UL));
        });
    }

    private static uint[] CreateValidWords()
    {
        var words = new uint[checked((int)
            SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount)];
        words[(int)SimpleDdgiGuidingGpuAbi.CounterValidSamples] = 100U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMaintenanceSamples] = 8U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMixtureUniformSamples] = 22U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMixtureGuidedSamples] = 70U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterUniformFallbackSamples] = 5U;
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMaximumInversePdfBits] =
            BitConverter.SingleToUInt32Bits(6.0f);
        words[(int)SimpleDdgiGuidingGpuAbi.CounterMaximumPdfBits] =
            BitConverter.SingleToUInt32Bits(0.8f);
        words[7 + 12] = 10U;
        words[8 + 12] = 40U;
        words[9 + 12] = 45U;
        words[10 + 12] = 5U;
        return words;
    }
}
