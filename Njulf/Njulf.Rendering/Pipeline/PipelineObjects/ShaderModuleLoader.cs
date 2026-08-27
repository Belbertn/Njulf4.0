using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Shaders;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    internal static unsafe class ShaderModuleLoader
    {
        private const string EmbeddedResourcePrefix = "Njulf.Shaders.";
        internal const int MaximumShaderModuleBytes = 16 * 1024 * 1024;

        public static ShaderModule Load(VulkanContext context, string shaderFileName)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(shaderFileName))
                throw new ArgumentException("Shader file name is required.", nameof(shaderFileName));
            if (Path.IsPathFullyQualified(shaderFileName) ||
                !string.Equals(
                    shaderFileName,
                    Path.GetFileName(shaderFileName),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Shader names must be unqualified file names.",
                    nameof(shaderFileName));
            }

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

        internal static byte[] LoadBytes(string shaderFileName)
        {
            Assembly assembly = typeof(ShaderLibrary).Assembly;
            string resourceName = EmbeddedResourcePrefix + shaderFileName;
            string? overrideDirectory = Environment.GetEnvironmentVariable(
                PerformanceCaptureHostIdentityResolver
                    .ShaderOverrideDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                string overridePath = Path.Combine(
                    Path.GetFullPath(overrideDirectory),
                    shaderFileName);
                if (File.Exists(overridePath))
                {
                    using var input = new FileStream(
                        overridePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.SequentialScan);
                    return ReadBoundedSnapshot(
                        input,
                        $"shader override '{overridePath}'");
                }
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName) ??
                                   assembly.GetManifestResourceStream(EmbeddedResourcePrefix + Path.GetFileNameWithoutExtension(shaderFileName));
            if (stream != null)
                return ReadBoundedSnapshot(
                    stream,
                    $"embedded shader '{resourceName}'");

            // The build-pinned embedded bundle is authoritative unless an explicit
            // override directory is configured. A deployment may still provide a
            // same-directory fallback when a resource is genuinely absent.
            string[] fileCandidates = GetFileCandidates(shaderFileName).ToArray();
            foreach (string candidate in fileCandidates)
            {
                try
                {
                    using var input = new FileStream(
                        candidate,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.SequentialScan);
                    return ReadBoundedSnapshot(
                        input,
                        $"shader file '{candidate}'");
                }
                catch (FileNotFoundException)
                {
                    // A deployment may omit individual fallback files. Keep
                    // searching the fixed, same-directory candidate set.
                }
                catch (DirectoryNotFoundException)
                {
                    // The candidate directory is optional.
                }
            }

            string searchedFiles = string.Join(
                Environment.NewLine,
                fileCandidates.Select(path => "  " + path));
            string resources = string.Join(Environment.NewLine, assembly.GetManifestResourceNames().Select(name => "  " + name));

            throw new FileNotFoundException(
                $"Required shader '{shaderFileName}' was not found. Searched files:{Environment.NewLine}{searchedFiles}{Environment.NewLine}" +
                $"Searched embedded resource '{resourceName}'. Available shader resources:{Environment.NewLine}{resources}");
        }

        internal static byte[] ReadBoundedSnapshot(
            Stream stream,
            string description)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new InvalidDataException(
                    $"{description} must be a readable, seekable snapshot.");
            }

            long start = stream.Position;
            long admittedLength = checked(stream.Length - start);
            if (admittedLength <= 0 ||
                admittedLength > MaximumShaderModuleBytes)
            {
                throw new InvalidDataException(
                    $"{description} contains {admittedLength} bytes; expected " +
                    $"a size in (0, {MaximumShaderModuleBytes}].");
            }

            byte[] snapshot =
                GC.AllocateUninitializedArray<byte>(
                    checked((int)admittedLength));
            try
            {
                stream.ReadExactly(snapshot);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"{description} became shorter while it was read.",
                    exception);
            }

            if (stream.ReadByte() != -1 ||
                stream.Length - start != admittedLength)
            {
                throw new InvalidDataException(
                    $"{description} changed length while it was read.");
            }

            return snapshot;
        }

        private static IEnumerable<string> GetFileCandidates(string shaderFileName)
        {
            string baseDirectory = AppContext.BaseDirectory;

            yield return Path.Combine(baseDirectory, "Shaders", shaderFileName);
            yield return Path.Combine(baseDirectory, shaderFileName);
        }
    }
}
