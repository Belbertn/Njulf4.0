using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Produces stable, content-derived identities only when scene or producer
/// revisions change. It intentionally hashes the transport geometry itself
/// when an authored content hash is unavailable; runtime handle identity alone
/// is never trusted as a persistent mesh key.
/// </summary>
internal static class SimpleDdgiWarmStartIdentityBuilder
{
    internal const string ShaderAbi =
        "Njulf.SimpleDdgi.WarmStart/2;Irradiance=RGBA16F-8x8;" +
        "Visibility=RG16F-16x16;ReceiverProbe=16;WorldOriginBitsToroidal=1";

    public static SimpleDdgiWarmStartSceneIdentity Create(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager,
        ulong exactLightingSignature,
        ulong exactEnvironmentSignature,
        ulong emissiveSourceSignature,
        string shaderBundleHash)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(meshManager);
        ArgumentNullException.ThrowIfNull(materialManager);

        using IncrementalHash sceneHash = NewHash("scene/v1");
        using IncrementalHash meshHash = NewHash("mesh/v1");
        using IncrementalHash transformHash = NewHash("transform/v1");
        using IncrementalHash materialHash = NewHash("material-transport/v1");
        using IncrementalHash environmentHash = NewHash("environment/v1");
        using IncrementalHash shaderHash = NewHash("shader-abi/v1");

        Append(sceneHash, scene.Name);
        Append(sceneHash, scene.AmbientLight.R);
        Append(sceneHash, scene.AmbientLight.G);
        Append(sceneHash, scene.AmbientLight.B);
        Append(sceneHash, scene.AmbientLight.A);
        Append(sceneHash, scene.RenderObjects.Count);
        Append(sceneHash, scene.StaticInstanceBatches.Count);
        Append(sceneHash, scene.FoliagePrototypes.Count);
        Append(sceneHash, scene.FoliagePatches.Count);
        Append(sceneHash, scene.ParticleEffects.Count);
        Append(sceneHash, scene.GlobalIlluminationProbeVolumes.Count);

        var meshHandles = new HashSet<MeshHandle>();
        bool eligible = true;
        string ineligibleReason = string.Empty;

        for (int index = 0; index < scene.RenderObjects.Count; index++)
        {
            RenderObject renderObject = scene.RenderObjects[index];
            Append(sceneHash, index);
            Append(sceneHash, renderObject.Name);
            Append(sceneHash, renderObject.Visible);
            Append(sceneHash, renderObject.Enabled);
            Append(sceneHash, renderObject.IsStatic);
            AppendAsset(sceneHash, renderObject.AssetReference);
            AppendResourceIdentity(sceneHash, renderObject.Mesh);
            AppendResourceIdentity(sceneHash, renderObject.Material);

            Append(transformHash, index);
            Append(transformHash, renderObject.WorldMatrix);
            Append(transformHash, renderObject.LocalMeshBounds);
            if (renderObject.Mesh is MeshHandle handle && handle.IsValid)
                meshHandles.Add(handle);
        }

        for (int index = 0; index < scene.StaticInstanceBatches.Count; index++)
        {
            StaticInstanceBatch batch = scene.StaticInstanceBatches[index];
            Append(sceneHash, index);
            Append(sceneHash, batch.Name);
            Append(sceneHash, batch.Visible);
            AppendAsset(sceneHash, batch.AssetReference);
            AppendResourceIdentity(sceneHash, batch.Mesh);
            AppendResourceIdentity(sceneHash, batch.Material);
            Append(transformHash, batch.WorldMatrices.Count);
            foreach (Matrix4x4 matrix in batch.WorldMatrices)
                Append(transformHash, matrix);
            if (batch.Mesh is MeshHandle handle && handle.IsValid)
                meshHandles.Add(handle);
        }

        for (int index = 0; index < scene.FoliagePrototypes.Count; index++)
        {
            FoliagePrototype prototype = scene.FoliagePrototypes[index];
            Append(sceneHash, index);
            Append(sceneHash, prototype.Name);
            AppendAsset(sceneHash, prototype.AssetReference);
            AppendResourceIdentity(sceneHash, prototype.Mesh);
            AppendResourceIdentity(sceneHash, prototype.Material);
            Append(sceneHash, (uint)prototype.GeometryMode);
            Append(sceneHash, prototype.CardHeight);
            Append(sceneHash, prototype.CardWidth);
            Append(sceneHash, prototype.FarImpostorEnabled);
            if (prototype.Mesh is MeshHandle handle && handle.IsValid)
                meshHandles.Add(handle);
        }

        for (int index = 0; index < scene.FoliagePatches.Count; index++)
        {
            FoliagePatch patch = scene.FoliagePatches[index];
            Append(sceneHash, index);
            Append(sceneHash, patch.Name);
            Append(sceneHash, patch.Visible);
            Append(sceneHash, patch.Density);
            Append(sceneHash, patch.Seed);
            Append(sceneHash, patch.DensityTexturePath ?? string.Empty);
            Append(sceneHash, (uint)patch.PlacementMode);
            Append(sceneHash, patch.Placement.Revision);
            Append(transformHash, patch.Bounds);
            Append(transformHash, patch.InstancePosition);
            Append(transformHash, patch.InstanceScale);
        }

        for (int index = 0;
             index < scene.GlobalIlluminationProbeVolumes.Count;
             index++)
        {
            GlobalIlluminationProbeVolume volume =
                scene.GlobalIlluminationProbeVolumes[index];
            Append(sceneHash, index);
            Append(sceneHash, volume.Name);
            Append(sceneHash, volume.Enabled);
            Append(sceneHash, volume.Interior);
            Append(sceneHash, (uint)volume.QualityClass);
            Append(sceneHash, volume.Priority);
            Append(sceneHash, volume.BlendDistance);
            Append(sceneHash, volume.StreamingCellId);
            Append(sceneHash, volume.ProbeCountX);
            Append(sceneHash, volume.ProbeCountY);
            Append(sceneHash, volume.ProbeCountZ);
            Append(transformHash, volume.Origin);
            Append(transformHash, volume.Size);
        }

        for (int index = 0; index < scene.ParticleEffects.Count; index++)
        {
            ParticleEffectInstance effect = scene.ParticleEffects[index];
            Append(sceneHash, index);
            Append(sceneHash, effect.Name);
            AppendAsset(sceneHash, effect.AssetReference);
            Append(sceneHash, effect.Visible);
            Append(sceneHash, effect.Playing);
            Append(sceneHash, effect.Paused);
            Append(sceneHash, effect.Stopped);
            Append(sceneHash, effect.RandomSeed);
            Append(transformHash, effect.WorldMatrix);
        }

        foreach (MeshHandle handle in meshHandles
                     .OrderBy(static handle => handle.Index)
                     .ThenBy(static handle => handle.Generation))
        {
            Append(meshHash, handle.Index);
            Append(meshHash, handle.Generation);
            try
            {
                MeshTransportGeometry geometry =
                    meshManager.GetTransportGeometry(handle);
                Append(meshHash, geometry.IsSkinned);
                Append(meshHash, geometry.LocalSurfaceArea);
                AppendBytes(
                    meshHash,
                    MemoryMarshal.AsBytes(
                        geometry.VertexPositions.Span));
                AppendBytes(
                    meshHash,
                    MemoryMarshal.AsBytes(
                        geometry.VertexUvColors.Span));
                AppendBytes(
                    meshHash,
                    MemoryMarshal.AsBytes(geometry.Indices.Span));
            }
            catch (InvalidOperationException ex)
            {
                eligible = false;
                ineligibleReason =
                    $"Mesh {handle.Index}:{handle.Generation} has no exact transport geometry ({ex.Message}).";
                Append(meshHash, ineligibleReason);
                break;
            }
        }

        GiMaterialTransportProfile[] materialProfiles =
            materialManager.GetMaterialTransportProfileSnapshot();
        Append(materialHash, materialProfiles.Length);
        for (int index = 0; index < materialProfiles.Length; index++)
        {
            GiMaterialTransportProfile profile = materialProfiles[index];
            Append(materialHash, index);
            Append(materialHash, profile.AlgorithmVersion);
            Append(materialHash, profile.SourceContentHash);
            Append(materialHash, profile.PrimitiveContentHash);
            Append(materialHash, (uint)profile.Flags);
            Append(materialHash, (uint)profile.Quality);
            Append(materialHash, profile.MeanDiffuseReflectance);
            Append(materialHash, profile.MeanTransmittedDiffuseReflectance);
            Append(materialHash, profile.MeanEmissiveRadiance);
            Append(materialHash, profile.EmissiveImportance);
            Append(materialHash, (uint)profile.EmissiveUnit);
            Append(materialHash, profile.EffectiveEmissiveScale);
            Append(materialHash, profile.EmissiveArtisticMultiplier);
            Append(materialHash, profile.AverageEmissiveLuminanceNits);
            Append(materialHash, profile.PeakEmissiveLuminanceNits);
            Append(materialHash, profile.PeakEmissiveLuminanceValid);
            Append(materialHash, profile.MeanMaterialOcclusion);
            Append(materialHash, profile.AlphaCoverage);
            Append(materialHash, profile.MeanMetallic);
            Append(materialHash, profile.MeanRoughness);
            Append(materialHash, profile.NormalVariance);
        }

        Append(environmentHash, exactLightingSignature);
        Append(environmentHash, exactEnvironmentSignature);
        Append(environmentHash, emissiveSourceSignature);
        Append(environmentHash, scene.ParticleEffects.Count);

        Append(shaderHash, shaderBundleHash ?? string.Empty);
        Append(shaderHash, ShaderAbi);

        return new SimpleDdgiWarmStartSceneIdentity(
            sceneHash.GetHashAndReset(),
            meshHash.GetHashAndReset(),
            transformHash.GetHashAndReset(),
            materialHash.GetHashAndReset(),
            environmentHash.GetHashAndReset(),
            shaderHash.GetHashAndReset(),
            eligible,
            ineligibleReason);
    }

    private static IncrementalHash NewHash(string domain)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Append(hash, domain);
        return hash;
    }

    private static void AppendAsset(
        IncrementalHash hash,
        SceneAssetReference? asset)
    {
        Append(hash, asset != null);
        if (asset == null)
            return;
        Append(hash, asset.Path);
        Append(hash, asset.SubObject);
        Append(hash, asset.ContentHash ?? string.Empty);
    }

    private static void AppendResourceIdentity(
        IncrementalHash hash,
        object? resource)
    {
        switch (resource)
        {
            case MeshHandle mesh:
                Append(hash, "mesh");
                Append(hash, mesh.Index);
                Append(hash, mesh.Generation);
                break;
            case MaterialHandle material:
                Append(hash, "material");
                Append(hash, material.Index);
                Append(hash, material.Generation);
                break;
            case null:
                Append(hash, "null");
                break;
            default:
                Append(hash, resource.GetType().AssemblyQualifiedName ??
                    resource.GetType().FullName ?? "unknown");
                Append(hash, resource.ToString() ?? string.Empty);
                break;
        }
    }

    private static void Append(IncrementalHash hash, BoundingBox? bounds)
    {
        Append(hash, bounds.HasValue);
        if (bounds.HasValue)
            Append(hash, bounds.Value);
    }

    private static void Append(IncrementalHash hash, BoundingBox bounds)
    {
        Append(hash, bounds.Min);
        Append(hash, bounds.Max);
    }

    private static void Append(IncrementalHash hash, Matrix4x4 value)
    {
        Append(hash, value.M11); Append(hash, value.M12);
        Append(hash, value.M13); Append(hash, value.M14);
        Append(hash, value.M21); Append(hash, value.M22);
        Append(hash, value.M23); Append(hash, value.M24);
        Append(hash, value.M31); Append(hash, value.M32);
        Append(hash, value.M33); Append(hash, value.M34);
        Append(hash, value.M41); Append(hash, value.M42);
        Append(hash, value.M43); Append(hash, value.M44);
    }

    private static void Append(IncrementalHash hash, Vector3 value)
    {
        Append(hash, value.X);
        Append(hash, value.Y);
        Append(hash, value.Z);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        AppendBytes(hash, bytes);
    }

    private static void AppendBytes(
        IncrementalHash hash,
        ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void Append(IncrementalHash hash, bool value) =>
        Append(hash, value ? 1u : 0u);

    private static void Append(IncrementalHash hash, int value) =>
        Append(hash, unchecked((uint)value));

    private static void Append(IncrementalHash hash, float value) =>
        Append(hash, BitConverter.SingleToUInt32Bits(value));

    private static void Append(IncrementalHash hash, double value) =>
        Append(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    private static void Append(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
