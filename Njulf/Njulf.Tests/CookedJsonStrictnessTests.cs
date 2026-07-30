using System.Text;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class CookedJsonStrictnessTests
{
    [TestCase(
        """{"relativePath":"mesh.njmesh","contentHash":1,"unknown":true}""",
        "unknown")]
    [TestCase(
        """{"RelativePath":"mesh.njmesh","contentHash":1}""",
        "RelativePath")]
    [TestCase(
        """{"relativePath":"a","relativePath":"b","contentHash":1}""",
        "Duplicate JSON property")]
    public void CurrentCookedSchema_RejectsAmbiguousProperties(
        string json,
        string expectedMessage)
    {
        CookedAssetFormatException failure =
            Assert.Throws<CookedAssetFormatException>(
                () => CookedJson.Deserialize<CookedAssetReference>(
                    Encoding.UTF8.GetBytes(json),
                    "strict.njmodel",
                    "fixture"))!;

        Assert.That(failure.Message, Does.Contain(expectedMessage));
    }

    [TestCase("""{"value":"NaN"}""")]
    [TestCase("""{"value":"Infinity"}""")]
    [TestCase("""{"value":"-Infinity"}""")]
    public void CurrentCookedSchema_RejectsNamedNonFiniteNumbers(string json)
    {
        Assert.That(
            () => CookedJson.Deserialize<FloatFixture>(
                Encoding.UTF8.GetBytes(json),
                "strict.njtex",
                "fixture"),
            Throws.TypeOf<CookedAssetFormatException>()
                .With.Message.Contains("invalid metadata"));
    }

    [Test]
    public void CurrentCookedSchema_AcceptsExactFinitePayload()
    {
        FloatFixture fixture = CookedJson.Deserialize<FloatFixture>(
            """{"value":1.25}"""u8,
            "strict.njtex",
            "fixture");

        Assert.That(fixture.Value, Is.EqualTo(1.25f));
    }

    [Test]
    public void CookedMaterial_InfiniteAttenuationUsesNullWithoutRelaxingNamedNumberPolicy()
    {
        var table = new CookedMaterialTable([ModelMaterial.Default]);
        byte[] encoded = CookedJson.Serialize(table);
        string json = Encoding.UTF8.GetString(encoded);

        Assert.That(json, Does.Contain("\"attenuationDistance\":null"));
        CookedMaterialTable roundTrip =
            CookedJson.Deserialize<CookedMaterialTable>(
                encoded,
                "strict.njmaterial",
                "materials");
        Assert.That(
            roundTrip.Materials.Single().AttenuationDistance,
            Is.EqualTo(float.PositiveInfinity));

        string namedInfinity = json.Replace(
            "\"attenuationDistance\":null",
            "\"attenuationDistance\":\"Infinity\"",
            StringComparison.Ordinal);
        Assert.That(
            () => CookedJson.Deserialize<CookedMaterialTable>(
                Encoding.UTF8.GetBytes(namedInfinity),
                "strict.njmaterial",
                "materials"),
            Throws.TypeOf<CookedAssetFormatException>()
                .With.Message.Contains("invalid metadata"));
    }

    private sealed record FloatFixture(float Value);
}
