using System;
using System.IO;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Shaders;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

internal static unsafe class ShaderModuleLoader
{
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