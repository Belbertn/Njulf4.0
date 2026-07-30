using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Stable CPU/GPU contract for the headless material-transport conformance
/// dispatch. The shader intentionally has no renderer, swapchain, bindless, or
/// ray-query dependencies so CI can run it on any Vulkan 1.3 compute device.
/// </summary>
public static class GiMaterialGpuConformanceContract
{
    public const string ShaderResourceName = "gi_material_conformance.comp";
    public const float MaximumAbsoluteError = 1.0e-4f;
    public const int MaterialExtensionAbiWords = 137;
    public const int MaterialExtensionAlignedWords = 140;
}

/// <summary>
/// One deterministic set of sampled material inputs. Every field occupies a
/// complete vec4 so the std430 array stride is exactly 128 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUGiMaterialConformanceCase
{
    public Vector4 BaseColorSample;
    // xyz = metallic/roughness sample, w = occlusion red sample.
    public Vector4 MetallicRoughnessSampleAndOcclusion;
    // xyz = emissive sample, w = NdotL for the diffuse-BRDF helper.
    public Vector4 EmissiveSampleAndNdotL;
    public Vector4 VertexColor;
    // xyz = geometric normal, w = front-facing boolean.
    public Vector4 GeometricNormalAndFrontFacing;
    // xyz = shading normal, w = expected NdotV metadata.
    public Vector4 ShadingNormalAndNdotV;
    // xyz = normalized view direction, w = extension-data-present boolean.
    public Vector4 ViewDirectionAndHasExtension;
    // xyz = incident irradiance, w reserved.
    public Vector4 Irradiance;
}

/// <summary>
/// Shader result for one conformance case. Boolean values are encoded as exact
/// 0/1 floats and provenance remains integer data.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUGiMaterialConformanceResult
{
    public Vector4 GeometricNormalAndOpacity;
    public Vector4 ShadingNormalAndOcclusion;
    public Vector4 DiffuseReflectanceAndMetallic;
    public Vector4 EmissiveRadianceAndRoughness;
    public Vector4 DiffuseFromIrradianceAndOpacityPass;
    public Vector4 DiffuseBrdfAndSidednessPass;
    public Vector4 CompactDiffuseBrdf;
    public uint TransportFlags;
    public uint HasExtensionData;
    public uint MaterialRevision;
    public uint TransportProfileRevision;
    public uint TextureContentRevision;
    public uint PackedMeanGiDirectionalDiffuseBaseRg;
    public uint PackedMeanGiDirectionalDiffuseBaseBAndF0R;
    public uint PackedMeanGiDielectricF0Gb;
}

/// <summary>
/// std430 rounds a struct-array stride to the struct's 16-byte alignment.
/// GPUMaterialExtensionData is a measured 548-byte production ABI, so the
/// conformance array adds three explicit words without changing that ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUGiMaterialExtensionConformanceElement
{
    public GPUMaterialExtensionData Value;
    public uint AlignmentPadding0;
    public uint AlignmentPadding1;
    public uint AlignmentPadding2;
}
