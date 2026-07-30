using System.Reflection;
using Njulf.Editor;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialEditorV2ContractTests
{
    [Test]
    public void EditorMaterialAuthoringSurface_DoesNotExposeRawGpuPayloads()
    {
        MethodInfo[] materialMethods = typeof(EditorController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(static method => method.Name.Contains("Material", StringComparison.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                materialMethods.Select(static method => method.Name),
                Does.Contain(nameof(EditorController.UpdateSelectedMaterialDefinition)));
            Assert.That(
                materialMethods.Select(static method => method.Name),
                Does.Contain(nameof(EditorController.TryGetSelectedMaterialInspection)));
            Assert.That(
                materialMethods.Any(static method =>
                    method.ReturnType == typeof(GPUMaterialData) ||
                    method.GetParameters().Any(static parameter =>
                        (parameter.ParameterType.IsByRef
                            ? parameter.ParameterType.GetElementType()
                            : parameter.ParameterType) == typeof(GPUMaterialData))),
                Is.False);
        });
    }

    [Test]
    public void InspectionContract_SeparatesAuthoredDefinitionFromDerivedTransport()
    {
        PropertyInfo[] properties = typeof(EditorMaterialInspection).GetProperties();

        Assert.Multiple(() =>
        {
            Assert.That(
                properties.Single(static property =>
                    property.Name == nameof(EditorMaterialInspection.Definition)).PropertyType,
                Is.EqualTo(typeof(MaterialDefinition)));
            Assert.That(
                properties.Single(static property =>
                    property.Name == nameof(EditorMaterialInspection.TransportProfile)).PropertyType,
                Is.EqualTo(typeof(GiMaterialTransportProfile)));
            Assert.That(
                properties.Any(static property => property.PropertyType == typeof(GPUMaterialData)),
                Is.False);
        });
    }
}
