using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionCaptureContractTests
{
    [Test]
    public void CubeFaceContract_IsOrthogonalAndRightHandedForEveryFace()
    {
        Assert.That(ReflectionCubeViewContract.All.Length, Is.EqualTo(6));
        for (int face = 0; face < 6; face++)
        {
            ReflectionCubeFaceContract contract = ReflectionCubeViewContract.Get(face);
            Vector3 cross = Vector3.Cross(contract.Up, contract.Right).Normalized();
            Assert.Multiple(() =>
            {
                Assert.That(Vector3.Dot(contract.Forward, contract.Up), Is.EqualTo(0.0f));
                Assert.That(Vector3.Dot(contract.Forward, contract.Right), Is.EqualTo(0.0f));
                Assert.That(Vector3.Dot(contract.Up, contract.Right), Is.EqualTo(0.0f));
                Assert.That(cross.X, Is.EqualTo(contract.Forward.X).Within(1e-5f));
                Assert.That(cross.Y, Is.EqualTo(contract.Forward.Y).Within(1e-5f));
                Assert.That(cross.Z, Is.EqualTo(contract.Forward.Z).Within(1e-5f));
            });
        }
    }

    [Test]
    public void CaptureViewFactory_MapsFaceToStableArrayLayerAndClampsPlanes()
    {
        var snapshot = new ReflectionProbeCaptureSnapshot(
            new Vector3(2, 3, 4),
            Quaternion.Identity,
            ReflectionProbeShape.Box,
            new Vector3(0.001f, 2, 3),
            1.0f);
        var version = new ReflectionCaptureVersion(1, 2, 3, 4, 5, 6, 7);

        ReflectionCaptureViewContext view = ReflectionCaptureViewFactory.Create(
            snapshot,
            face: 2,
            cubemapArrayLayer: 5,
            resolution: 128,
            resourceGeneration: 9,
            sceneRevision: 10,
            version,
            includesDdgi: true);

        Assert.Multiple(() =>
        {
            Assert.That(view.CubemapArrayLayer, Is.EqualTo(32));
            Assert.That(view.Face, Is.EqualTo(2));
            Assert.That(view.Resolution, Is.EqualTo(128U));
            Assert.That(view.NearPlane, Is.EqualTo(0.01f));
            Assert.That(view.FarPlane, Is.GreaterThan(view.NearPlane));
            Assert.That(view.Version, Is.EqualTo(version));
            Assert.That(view.IncludesDdgi, Is.True);
        });
    }

    [Test]
    public void PrefilterAndMemoryContractsRequireCompletePrivateChain()
    {
        ReflectionPrefilterMipWork mip = ReflectionPrefilterContract.GetMipWork(2, 64, 5);
        byte[] initialized = new byte[5];
        initialized[1] = 1;
        initialized[2] = 1;
        initialized[3] = 1;
        initialized[4] = 1;
        ReflectionProbeMemoryPlan memory = ReflectionProbeMemoryPlan.Build(2, 4, 3, 4, 2);

        Assert.Multiple(() =>
        {
            Assert.That(mip.Resolution, Is.EqualTo(16U));
            Assert.That(mip.Roughness, Is.EqualTo(0.5f));
            Assert.That(mip.FaceCount, Is.EqualTo(6));
            Assert.That(ReflectionPrefilterContract.IsComplete(initialized, 5), Is.True);
            Assert.That(memory.PublishedBytes, Is.EqualTo(2016UL));
            Assert.That(memory.PrivateScratchBytes, Is.EqualTo(1008UL));
            Assert.That(memory.CaptureDepthBytes, Is.EqualTo(128UL));
            Assert.That(memory.TotalBytes, Is.EqualTo(3152UL));
        });
    }

    [Test]
    public void CaptureGraphDeclaration_IsGraphicsOnlyAndHasThreeTransactionalPasses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReflectionProbeCaptureGraphDeclaration.Validate(), Is.True);
            Assert.That(ReflectionProbeCaptureGraphDeclaration.IsGraphicsOnly, Is.True);
            Assert.That(ReflectionProbeCaptureGraphDeclaration.PassDeclarations,
                Has.Count.EqualTo(3));
            Assert.That(ReflectionProbeCaptureGraphDeclaration.PassDeclarations.Select(pass => pass.Name),
                Is.EqualTo(new[]
                {
                    "ReflectionProbeCapturePass",
                    "ReflectionProbePrefilterPass",
                    "ReflectionProbePublishPass"
                }));
        });
    }
}
