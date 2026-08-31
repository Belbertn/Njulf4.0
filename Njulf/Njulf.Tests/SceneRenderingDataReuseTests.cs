using System.Reflection;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneRenderingDataReuseTests
{
    [Test]
    public void Clear_RestoresFreshScalarAndCoreValueDefaults()
    {
        using var fresh = new SceneRenderingData();
        using var reused = new SceneRenderingData();
        var dirtied = new List<PropertyInfo>();

        foreach (PropertyInfo property in typeof(SceneRenderingData)
                     .GetProperties(BindingFlags.Instance |
                                    BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite ||
                property.GetIndexParameters().Length != 0 ||
                !TryCreateDifferentValue(
                    property.PropertyType,
                    property.GetValue(fresh),
                    out object? value))
            {
                continue;
            }

            property.SetValue(reused, value);
            dirtied.Add(property);
        }

        reused.MeshletDrawCommands.Add(default);
        reused.ObjectData.Add(default);
        reused.DdgiVolumeDiagnostics.Add(
            new DdgiVolumeDiagnosticsEntry(
                0,
                default,
                0,
                0,
                0,
                0,
                0,
                0,
                0UL,
                0f));
        reused.Clear();

        Assert.Multiple(() =>
        {
            foreach (PropertyInfo property in dirtied)
            {
                Assert.That(
                    property.GetValue(reused),
                    Is.EqualTo(property.GetValue(fresh)),
                    $"Clear did not restore {property.Name}.");
            }
            Assert.That(reused.MeshletDrawCommands, Is.Empty);
            Assert.That(reused.ObjectData, Is.Empty);
            Assert.That(reused.DdgiVolumeDiagnostics, Is.Empty);
        });
    }

    private static bool TryCreateDifferentValue(
        Type type,
        object? baseline,
        out object? value)
    {
        if (type == typeof(bool)) value = !(bool)baseline!;
        else if (type == typeof(byte)) value = (byte)123;
        else if (type == typeof(sbyte)) value = (sbyte)57;
        else if (type == typeof(short)) value = (short)1234;
        else if (type == typeof(ushort)) value = (ushort)1234;
        else if (type == typeof(int)) value = 1234567;
        else if (type == typeof(uint)) value = 1234567u;
        else if (type == typeof(long)) value = 1234567L;
        else if (type == typeof(ulong)) value = 1234567UL;
        else if (type == typeof(float)) value = 123.25f;
        else if (type == typeof(double)) value = 123.25;
        else if (type == typeof(string)) value = "dirty";
        else if (type == typeof(Vector2)) value = new Vector2(7f, 11f);
        else if (type == typeof(Vector3)) value = new Vector3(7f, 11f, 13f);
        else if (type == typeof(Vector4)) value = new Vector4(7f, 11f, 13f, 17f);
        else if (type == typeof(Matrix4x4))
            value = Matrix4x4.CreateTranslation(new Vector3(7f, 11f, 13f));
        else if (type == typeof(BufferHandle))
            value = new BufferHandle(123, 7u);
        else if (type.IsEnum)
        {
            value = Enum.GetValues(type).Cast<object>()
                .FirstOrDefault(candidate => !Equals(candidate, baseline));
            return value is not null;
        }
        else if (type.IsArray)
        {
            value = Array.CreateInstance(type.GetElementType()!, 1);
        }
        else
        {
            value = null;
            return false;
        }

        return !Equals(value, baseline);
    }
}
