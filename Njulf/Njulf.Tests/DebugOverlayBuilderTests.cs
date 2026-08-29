using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DebugOverlayBuilderTests
{
    [Test]
    public void Build_NoneSnapshotsExternalCommandsAndClearRetainsPersistentCommands()
    {
        var builder = new DebugOverlayBuilder(new EmptyResourceLookup());
        builder.ConfigureDrawList(enabled: true, maxLineSegments: 16);
        builder.DrawList.Line(
            Vector3.Zero,
            Vector3.One,
            Vector4.One,
            lifetime: DebugDrawLifetime.OneFrame);
        builder.DrawList.Line(
            Vector3.One,
            new Vector3(2.0f),
            Vector4.One,
            lifetime: DebugDrawLifetime.Persistent);

        using var scene = new Scene();
        using var sceneData = new SceneRenderingData
        {
            DebugToolingEnabled = true,
            DebugOverlayMode = DebugOverlayMode.None
        };

        DebugDrawFrameSnapshot snapshot = builder.Build(
            scene,
            sceneData,
            manager: null,
            new DebugOverlayBuildOptions(false, false, true, -1));

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.DebugOverlayStatus.Availability,
                Is.EqualTo(DebugOverlayAvailability.Disabled));
            Assert.That(snapshot.LineCount, Is.EqualTo(2));
            Assert.That(snapshot.PersistentLineCount, Is.EqualTo(1));
        });

        builder.ClearFrame();
        DebugDrawFrameSnapshot afterClear = builder.DrawList.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(afterClear.LineCount, Is.EqualTo(1));
            Assert.That(afterClear.PersistentLineCount, Is.EqualTo(1));
            Assert.That(afterClear.DroppedLineCount, Is.Zero);
        });
    }

    [Test]
    public void Build_UnknownModePreservesExactUnavailableReason()
    {
        var builder = new DebugOverlayBuilder(new EmptyResourceLookup());
        builder.ConfigureDrawList(enabled: true, maxLineSegments: 16);
        const uint UnknownValue = 4_000_000_000u;
        using var scene = new Scene();
        using var sceneData = new SceneRenderingData
        {
            DebugToolingEnabled = true,
            DebugOverlayMode = (DebugOverlayMode)UnknownValue
        };

        _ = builder.Build(
            scene,
            sceneData,
            manager: null,
            new DebugOverlayBuildOptions(false, false, true, -1));

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.DebugOverlayStatus.Availability,
                Is.EqualTo(DebugOverlayAvailability.Unavailable));
            Assert.That(sceneData.DebugOverlayStatus.Reason,
                Is.EqualTo($"unknown overlay value {UnknownValue}"));
        });
    }

    [TestCase(true, true, DebugDrawDepthMode.XRay)]
    [TestCase(false, true, DebugDrawDepthMode.DepthTested)]
    [TestCase(false, false, DebugDrawDepthMode.AlwaysVisible)]
    public void Build_ResolvesDepthModeWithXRayPrecedence(
        bool showXRay,
        bool showDepthTested,
        DebugDrawDepthMode expected)
    {
        var builder = new DebugOverlayBuilder(new EmptyResourceLookup());
        builder.ConfigureDrawList(enabled: true, maxLineSegments: 16);
        using var scene = new Scene();
        using var sceneData = new SceneRenderingData
        {
            DebugToolingEnabled = true,
            DebugOverlayMode = DebugOverlayMode.LightTiles
        };

        _ = builder.Build(
            scene,
            sceneData,
            manager: null,
            new DebugOverlayBuildOptions(
                false,
                showXRay,
                showDepthTested,
                -1));

        Assert.That(sceneData.DebugOverlayDepthMode, Is.EqualTo(expected));
    }

    [Test]
    public void Build_EveryActiveCatalogModeHasARegisteredHandler()
    {
        var builder = new DebugOverlayBuilder(new EmptyResourceLookup());
        builder.ConfigureDrawList(enabled: true, maxLineSegments: 16);
        using var scene = new Scene();

        foreach (DebugOverlayDescriptor descriptor in DebugOverlayCatalog.ActiveCycle)
        {
            using var sceneData = new SceneRenderingData
            {
                DebugToolingEnabled = true,
                DebugOverlayMode = descriptor.Mode
            };

            _ = builder.Build(
                scene,
                sceneData,
                manager: null,
                new DebugOverlayBuildOptions(false, false, true, -1));

            Assert.That(
                sceneData.DebugOverlayStatus.Reason,
                Is.Not.EqualTo("catalog renderer has no registered handler"),
                descriptor.Mode.ToString());
            builder.ClearFrame();
        }
    }

    private sealed class EmptyResourceLookup : IDebugOverlayResourceLookup
    {
        public bool TryGetMaterialMetadata(
            MaterialHandle handle,
            out MaterialRenderMetadata metadata)
        {
            metadata = null!;
            return false;
        }

        public bool TryGetMeshInfo(MeshHandle handle, out MeshInfo meshInfo)
        {
            meshInfo = default;
            return false;
        }

        public bool TryGetMeshlet(
            MeshHandle mesh,
            uint index,
            out Meshlet meshlet)
        {
            meshlet = default;
            return false;
        }
    }
}
