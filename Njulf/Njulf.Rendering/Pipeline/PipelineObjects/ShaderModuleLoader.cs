using System;
using System.IO;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Shaders;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

internal static unsafe class ShaderModuleLoader
{
    internal static LoadedShaderModuleIdentity EffectIdentity(Njulf.Graphics.ShaderEffectAsset asset)
    {
        string nameHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(asset.Name))).ToLowerInvariant();
        string codeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(asset.ShaderCode)).ToLowerInvariant();
        return new($"effect-{nameHash}.spv", codeHash, asset.ShaderCode.Length, "effect", asset.Name);
    }
    internal static ShaderModule LoadEffect(VulkanContext context, Njulf.Graphics.ShaderEffectAsset asset)
    {
        fixed (byte* code = asset.ShaderCode)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)asset.ShaderCode.Length, PCode = (uint*)code };
            Result result = context.Api.CreateShaderModule(context.Device, &info, null, out var module);
            if (result != Result.Success) throw new VulkanException($"Failed to create effect shader '{asset.Name}'", result);
            try
            {
                // Hash logical names, not bytes: shared-module comparisons must still detect
                // bytecode changes for the same asset across feature-isolation captures.
                context.ShaderModuleIdentities.Record(EffectIdentity(asset));
                return module;
            }
            catch { context.Api.DestroyShaderModule(context.Device, module, null); throw; }
        }
    }
    internal const int MaximumShaderModuleBytes = ShaderArtifactResolver.MaximumShaderModuleBytes;

    public static ShaderModule Load(VulkanContext context, string shaderFileName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResolvedShaderArtifact artifact = Resolve(shaderFileName);
        byte[] spirv = artifact.Bytes;
        if (spirv.Length == 0 || spirv.Length % sizeof(uint) != 0)
            throw new VulkanException($"Shader '{shaderFileName}' is not valid SPIR-V bytecode.");
        LoadedShaderModuleIdentity identity = LoadedShaderModuleIdentity.Capture(artifact);

        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code
            };
            Result result = context.Api.CreateShaderModule(context.Device, &createInfo, null, out ShaderModule module);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create shader module for '{shaderFileName}'", result);
            try
            {
                context.ShaderModuleIdentities.Record(identity);
                return module;
            }
            catch
            {
                context.Api.DestroyShaderModule(context.Device, module, null);
                throw;
            }
        }
    }

    private static ResolvedShaderArtifact Resolve(string shaderFileName) =>
        ShaderArtifactResolver.Resolve(typeof(ShaderLibrary).Assembly, shaderFileName,
            Environment.GetEnvironmentVariable(
                PerformanceCaptureHostIdentityResolver.ShaderOverrideDirectoryEnvironmentVariable),
            AppContext.BaseDirectory);

    internal static byte[] LoadBytes(string shaderFileName) => Resolve(shaderFileName).Bytes;

    internal static byte[] ReadBoundedSnapshot(Stream stream, string description) =>
        ShaderArtifactResolver.ReadBoundedSnapshot(stream, description);
}
