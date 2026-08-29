using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class AlphaCorrectMotionVectorTests
{
    [Test]
    public void MotionPushConstantsCoverTheCompleteSharedMeshPipelineRange()
    {
        Assert.That(
            MotionVectorPass.MeshPipelinePushConstantStages,
            Is.EqualTo(
                ShaderStageFlags.TaskBitExt |
                ShaderStageFlags.MeshBitExt |
                ShaderStageFlags.FragmentBit));
    }

    [Test]
    public void MotionPass_UsesDepthCoveragePartitionsInSolidThenMaskedOrder()
    {
        string source = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "MotionVectorPass.cs");
        int solidCount = source.IndexOf(
            "sceneData.SolidMeshletCount",
            StringComparison.Ordinal);
        int maskedCount = source.IndexOf(
            "sceneData.MaskedMeshletCount",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(solidCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(maskedCount, Is.GreaterThan(solidCount));
            Assert.That(source, Does.Contain("BindlessIndex.SolidDepthMeshletDrawBufferBase"));
            Assert.That(source, Does.Contain("BindlessIndex.MaskedDepthMeshletDrawBufferBase"));
            Assert.That(source, Does.Contain("Silk.NET.Vulkan.Pipeline pipeline"));
            Assert.That(source, Does.Not.Contain("sceneData.SimpleOpaqueMeshletCount"));
            Assert.That(source, Does.Not.Contain("sceneData.SimpleNormalOpaqueMeshletCount"));
            Assert.That(source, Does.Not.Contain("sceneData.FullOpaqueMeshletCount"));
        });
    }

    [Test]
    public void MeshPipeline_OwnsTwoCullFreeSceneMotionPipelinesSymmetrically()
    {
        string source = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private VkPipeline _maskedMotionVectorPipeline;"));
            Assert.That(source, Does.Contain("MaskedMotionVectorPipeline => _maskedMotionVectorPipeline"));
            Assert.That(source, Does.Contain("\"motion_vector_alpha.mesh.spv\""));
            Assert.That(source, Does.Contain("\"motion_vector_alpha.frag.spv\""));
            Assert.That(source, Does.Contain("motion_vector_alpha_compacted.mesh.spv"));
            Assert.That(source, Does.Contain("CompactedMaskedMotionVectorPipeline"));
            Assert.That(Count(source, "cullMode: CullModeFlags.None"), Is.GreaterThanOrEqualTo(2));
            Assert.That(Count(source, "DestroyPipeline(_context.Device, _maskedMotionVectorPipeline"), Is.EqualTo(1));
            Assert.That(source, Does.Contain("_maskedMotionVectorPipeline = default;"));
        });
    }

    [Test]
    public void CompactedMotionVectorShaders_AreMeshOnlyAndUseExactDepthLists()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "MotionVectorPass.cs");
        string solid = ReadRepoText("Njulf.Shaders", "motion_vector.mesh");
        string masked = ReadRepoText(
            "Njulf.Shaders", "motion_vector_alpha.mesh");
        string project = ReadRepoText(
            "Njulf.Shaders", "Njulf.Shaders.csproj");

        Assert.Multiple(() =>
        {
            Assert.That(pass, Does.Contain(
                "SceneSolidDepthCompactedMeshletDrawBufferBase"));
            Assert.That(pass, Does.Contain(
                "GetSolidDepthIndirectDispatchOffset"));
            Assert.That(pass, Does.Contain("CmdDrawMeshTasksIndirect"));
            Assert.That(solid, Does.Contain(
                "#ifdef MOTION_VECTOR_COMPACTED_MESH"));
            Assert.That(solid, Does.Contain("pc.Push.FirstDraw"));
            Assert.That(masked, Does.Contain(
                "#ifndef MOTION_VECTOR_COMPACTED_MESH"));
            Assert.That(project, Does.Contain(
                "motion_vector_compacted.mesh"));
            Assert.That(project, Does.Contain(
                "motion_vector_alpha_compacted.mesh"));
        });
    }

    [Test]
    public void SceneMotionFragments_DiscardInvalidSidednessAndCoverageBeforeWriting()
    {
        string solid = ReadRepoText("Njulf.Shaders", "motion_vector.frag");
        string masked = ReadRepoText("Njulf.Shaders", "motion_vector_alpha.frag");

        Assert.Multiple(() =>
        {
            AssertBefore(solid, "if (!doubleSided && !gl_FrontFacing)", "outVelocity =");
            AssertBefore(masked, "if (!doubleSided && !gl_FrontFacing)", "outVelocity =");
            Assert.That(masked, Does.Contain("#include \"material_coverage.glsl\""));
            Assert.That(masked, Does.Contain("EvaluateMaterialAlphaCoverage("));
            Assert.That(masked, Does.Contain("MaterialCoverageSurvivesForward(coverage)"));
            AssertBefore(masked, "MaterialCoverageSurvivesForward(coverage)", "outVelocity =");
            AssertBefore(masked, "MaterialCoverageSurvivesForward(coverage)", "WriteStorageWord(");
        });
    }

    [Test]
    public void MaskedMotionMesh_ExportsFullCoverageInputsAndMirroredWinding()
    {
        string source = ReadRepoText("Njulf.Shaders", "motion_vector_alpha.mesh");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("FetchRenderableVertex("));
            Assert.That(source, Does.Contain("meshTexCoord[vertexSlot] = vertex.TexCoord;"));
            Assert.That(source, Does.Contain("meshTexCoord2[vertexSlot] = vertex.TexCoord2;"));
            Assert.That(source, Does.Contain("meshVertexColor[vertexSlot] = vertex.Color;"));
            Assert.That(source, Does.Contain("meshMaterialIndex[vertexSlot] = command.MaterialIndex;"));
            Assert.That(source, Does.Contain("ResolveMirroredInstanceTriangle("));
        });
    }

    [Test]
    public void AuthoredFoliageMotion_UsesTheSharedStableCoverageContract()
    {
        string mesh = ReadRepoText("Njulf.Shaders", "foliage_motion.mesh");
        string fragment = ReadRepoText("Njulf.Shaders", "foliage_motion.frag");

        Assert.Multiple(() =>
        {
            Assert.That(mesh, Does.Contain("meshClusterIndex[vertexSlot] = command.ClusterIndex;"));
            Assert.That(mesh, Does.Contain("meshLodBand[vertexSlot] = command.LodLevel;"));
            Assert.That(mesh, Does.Contain("meshGeometryMode[vertexSlot] = 1u;"));
            Assert.That(fragment, Does.Contain("#include \"foliage_coverage.glsl\""));
            Assert.That(fragment, Does.Contain("FoliageCoverageSurvives("));
            Assert.That(fragment, Does.Contain("gl_FragCoord.xy"));
            AssertBefore(fragment, "FoliageCoverageSurvives(", "outVelocity =");
            AssertBefore(fragment, "FoliageCoverageSurvives(", "WriteStorageWord(");
        });
    }

    [Test]
    public void DepthPrePass_RemainsFreeOfMotionHistoryFusion()
    {
        string source = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "DepthPrePass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Not.Contain("MotionVectors"));
            Assert.That(source, Does.Not.Contain("PreviousViewProjection"));
            Assert.That(source, Does.Not.Contain("PreviousTime"));
        });
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int index = 0;
             (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }
        return count;
    }

    private static void AssertBefore(string source, string first, string second)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0), first);
        Assert.That(secondIndex, Is.GreaterThan(firstIndex), second);
    }

    private static string ReadRepoText(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }
}
