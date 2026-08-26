using System.Reflection;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererDiagnosticsAssemblerTests
{
    [Test]
    public void ApplyAsyncSubmission_ChangesOnlySubmittedSegmentCounts()
    {
        var assembler = new RendererDiagnosticsAssembler();
        RendererDiagnostics before = RendererDiagnostics.Empty with
        {
            VisibleObjectCount = 17,
            AsyncComputeSubmittedGraphicsSegmentCount = 2,
            AsyncComputeSubmittedComputeSegmentCount = 3
        };

        RendererDiagnostics after = assembler.ApplyAsyncSubmission(
            before,
            new AsyncComputeSubmissionPatch(5, 7));

        Assert.Multiple(() =>
        {
            Assert.That(
                after.AsyncComputeSubmittedGraphicsSegmentCount,
                Is.EqualTo(5));
            Assert.That(
                after.AsyncComputeSubmittedComputeSegmentCount,
                Is.EqualTo(7));
        });
        AssertPropertiesUnchangedExcept(
            before,
            after,
            nameof(
                RendererDiagnostics
                    .AsyncComputeSubmittedGraphicsSegmentCount),
            nameof(
                RendererDiagnostics
                    .AsyncComputeSubmittedComputeSegmentCount));
    }

    [Test]
    public void ApplyValidationMessages_ChangesOnlyLateValidationFields()
    {
        var assembler = new RendererDiagnosticsAssembler();
        RendererDiagnostics before = RendererDiagnostics.Empty with
        {
            VisibleObjectCount = 23,
            ValidationMode = RendererValidationMode.Standard
        };
        var validation = new RendererValidationMessageSnapshot(
            1,
            2,
            3,
            4,
            "first warning",
            "last warning",
            "first error",
            "last error");

        RendererDiagnostics after =
            assembler.ApplyValidationMessages(before, validation);

        Assert.Multiple(() =>
        {
            Assert.That(after.ValidationVerboseMessageCount, Is.EqualTo(1));
            Assert.That(after.ValidationInfoMessageCount, Is.EqualTo(2));
            Assert.That(after.ValidationWarningMessageCount, Is.EqualTo(3));
            Assert.That(after.ValidationErrorMessageCount, Is.EqualTo(4));
            Assert.That(
                after.ValidationMode,
                Is.EqualTo(RendererValidationMode.Standard));
        });
        AssertPropertiesUnchangedExcept(
            before,
            after,
            nameof(RendererDiagnostics.ValidationVerboseMessageCount),
            nameof(RendererDiagnostics.ValidationInfoMessageCount),
            nameof(RendererDiagnostics.ValidationWarningMessageCount),
            nameof(RendererDiagnostics.ValidationErrorMessageCount),
            nameof(RendererDiagnostics.ValidationFirstWarningMessage),
            nameof(RendererDiagnostics.ValidationLastWarningMessage),
            nameof(RendererDiagnostics.ValidationFirstErrorMessage),
            nameof(RendererDiagnostics.ValidationLastErrorMessage));
    }

    private static void AssertPropertiesUnchangedExcept(
        RendererDiagnostics before,
        RendererDiagnostics after,
        params string[] changedProperties)
    {
        var changed = changedProperties.ToHashSet(
            StringComparer.Ordinal);
        foreach (PropertyInfo property in
                 typeof(RendererDiagnostics).GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (changed.Contains(property.Name))
                continue;

            Assert.That(
                property.GetValue(after),
                Is.EqualTo(property.GetValue(before)),
                property.Name);
        }
    }
}
