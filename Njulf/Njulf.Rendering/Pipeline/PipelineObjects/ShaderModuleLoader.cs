using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Njulf.Rendering.Core;
using Njulf.Shaders;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    internal static unsafe class ShaderModuleLoader
    {
        private const string EmbeddedResourcePrefix = "Njulf.Shaders.";

        public static ShaderModule Load(VulkanContext context, string shaderFileName)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(shaderFileName))
                throw new ArgumentException("Shader file name is required.", nameof(shaderFileName));

            byte[] spirv = LoadBytes(shaderFileName);
            if (spirv.Length == 0 || spirv.Length % sizeof(uint) != 0)
                throw new VulkanException($"Shader '{shaderFileName}' is not valid SPIR-V bytecode.");

            fixed (byte* code = spirv)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code
                };

                Result result = context.Api.CreateShaderModule(
                    context.Device,
                    &createInfo,
                    null,
                    out ShaderModule module);

                if (result != Result.Success)
                    throw new VulkanException($"Failed to create shader module for '{shaderFileName}'", result);

                return module;
            }
        }

        private static byte[] LoadBytes(string shaderFileName)
        {
            foreach (string candidate in GetFileCandidates(shaderFileName))
            {
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }

            Assembly assembly = typeof(ShaderLibrary).Assembly;
            string resourceName = EmbeddedResourcePrefix + shaderFileName;
            using Stream? stream = assembly.GetManifestResourceStream(resourceName) ??
                                   assembly.GetManifestResourceStream(EmbeddedResourcePrefix + Path.GetFileNameWithoutExtension(shaderFileName));
            if (stream != null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }

            string searchedFiles = string.Join(Environment.NewLine, GetFileCandidates(shaderFileName).Select(path => "  " + path));
            string resources = string.Join(Environment.NewLine, assembly.GetManifestResourceNames().Select(name => "  " + name));

            throw new FileNotFoundException(
                $"Required shader '{shaderFileName}' was not found. Searched files:{Environment.NewLine}{searchedFiles}{Environment.NewLine}" +
                $"Searched embedded resource '{resourceName}'. Available shader resources:{Environment.NewLine}{resources}");
        }

        private static IEnumerable<string> GetFileCandidates(string shaderFileName)
        {
            string baseDirectory = AppContext.BaseDirectory;

            yield return Path.Combine(baseDirectory, "Shaders", shaderFileName);
            yield return Path.Combine(baseDirectory, shaderFileName);

            DirectoryInfo? directory = new DirectoryInfo(baseDirectory);
            while (directory != null)
            {
                yield return Path.Combine(directory.FullName, "Njulf.Shaders", "bin", "Debug", "net10.0", "Shaders", shaderFileName);
                yield return Path.Combine(directory.FullName, "Njulf.Shaders", "bin", "Release", "net10.0", "Shaders", shaderFileName);
                directory = directory.Parent;
            }
        }
    }
}
