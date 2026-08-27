using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Njulf.Rendering.Diagnostics;

internal interface IPerformanceCaptureGitStatusProbe
{
    string Resolve(string repositoryRoot);
}

internal sealed class PerformanceCaptureGitStatusProbe :
    IPerformanceCaptureGitStatusProbe
{
    public string Resolve(string repositoryRoot)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(repositoryRoot);
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--porcelain=v1");
            startInfo.ArgumentList.Add("--untracked-files=normal");
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return "unavailable:git-status-start-failed";

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return "unavailable:git-status-timeout";
            }

            string output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? string.IsNullOrWhiteSpace(output) ? "clean" : "dirty"
                : "unavailable:git-status-failed";
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException)
        {
            return "unavailable:git-status-failed";
        }
    }
}

internal sealed class PerformanceCaptureHostIdentityResolver
{
    private const string ShaderResourcePrefix = "Njulf.Shaders.";
    private const string ShaderBundleHashMetadataKey =
        "NjulfShaderBundleHash";
    internal const string ShaderOverrideDirectoryEnvironmentVariable =
        "NJULF_SHADER_OVERRIDE_DIRECTORY";
    private readonly Assembly _applicationAssembly;
    private readonly Assembly _shaderAssembly;
    private readonly IPerformanceCaptureGitStatusProbe _gitStatusProbe;

    internal PerformanceCaptureHostIdentityResolver(
        Assembly applicationAssembly,
        Assembly shaderAssembly,
        IPerformanceCaptureGitStatusProbe? gitStatusProbe = null)
    {
        _applicationAssembly = applicationAssembly ??
            throw new ArgumentNullException(nameof(applicationAssembly));
        _shaderAssembly = shaderAssembly ??
            throw new ArgumentNullException(nameof(shaderAssembly));
        _gitStatusProbe = gitStatusProbe ?? new PerformanceCaptureGitStatusProbe();
    }

    internal PerformanceCaptureStartupIdentity ResolveStartupIdentity() => new(
        ResolveApplicationVersion(_applicationAssembly),
        ResolveCommit(_applicationAssembly),
        ResolveShaderBundleHash(_shaderAssembly),
        ResolveBuildConfiguration(),
        ResolveTargetFramework(_applicationAssembly));

    internal PerformanceCapturePostPipelineIdentity ResolvePostPipelineIdentity() =>
        new(
            ResolveExecutableHash(),
            ResolveDirtyWorktreeState(gitStatusProbe: _gitStatusProbe));

    internal static string ResolveApplicationVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return PerformanceCaptureHashing.NormalizeMetadataValue(
            informationalVersion ?? assembly.GetName().Version?.ToString(),
            "unavailable:application-version-not-embedded");
    }

    internal static string ResolveCommit(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? sourceRevision = null;
        foreach (AssemblyMetadataAttribute attribute in
                 assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(
                    attribute.Key,
                    "SourceRevisionId",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    attribute.Key,
                    "GitCommitId",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    attribute.Key,
                    "Commit",
                    StringComparison.OrdinalIgnoreCase))
            {
                sourceRevision = attribute.Value;
                break;
            }
        }

        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return ResolveCommit(sourceRevision, informationalVersion);
    }

    internal static string ResolveCommit(
        string? sourceRevision,
        string? informationalVersion)
    {
        string? revision =
            PerformanceCaptureHashing.NormalizeSourceRevision(sourceRevision);
        if (revision != null)
            return revision;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataIndex = informationalVersion.IndexOf('+');
            if (metadataIndex >= 0 &&
                metadataIndex < informationalVersion.Length - 1)
            {
                revision = PerformanceCaptureHashing.NormalizeSourceRevision(
                    informationalVersion[(metadataIndex + 1)..]);
            }
        }

        return revision ?? "unavailable:source-revision-not-embedded";
    }

    internal static string ResolveExecutableHash(string? executablePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
                return HashFile(executablePath);

            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return "unavailable:process-path-not-reported";

            string processFullPath = Path.GetFullPath(processPath);
            string applicationDirectory =
                Path.GetDirectoryName(processFullPath) ?? AppContext.BaseDirectory;
            var binaryPaths = new List<string> { processFullPath };
            binaryPaths.AddRange(Directory.GetFiles(
                applicationDirectory,
                "Njulf*.dll",
                SearchOption.TopDirectoryOnly));
            binaryPaths.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    Path.GetFileName(left),
                    Path.GetFileName(right)));

            var manifest = new StringBuilder();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidatePath in binaryPaths)
            {
                string fullPath = Path.GetFullPath(candidatePath);
                if (!seenPaths.Add(fullPath))
                    continue;
                string fileHash = HashFile(fullPath);
                if (!fileHash.StartsWith("sha256:", StringComparison.Ordinal))
                    return fileHash;
                manifest.Append(Path.GetFileName(fullPath));
                manifest.Append(':');
                manifest.Append(fileHash);
                manifest.Append('\n');
            }

            if (manifest.Length == 0)
                return "unavailable:executable-bundle-empty";
            byte[] bundleHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(manifest.ToString()));
            return "sha256:" +
                Convert.ToHexString(bundleHash).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            CryptographicException)
        {
            return "unavailable:executable-hash-failed";
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        byte[] hash = SHA256.HashData(stream);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ResolveDirtyWorktreeState(
        string? explicitState = null,
        string? searchStartDirectory = null,
        IPerformanceCaptureGitStatusProbe? gitStatusProbe = null)
    {
        string? supplied = string.IsNullOrWhiteSpace(explicitState)
            ? Environment.GetEnvironmentVariable("NJULF_DIRTY_WORKTREE_STATE")
            : explicitState;
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            string normalized = supplied.Trim().ToLowerInvariant();
            if (normalized is "clean" or "dirty")
                return normalized;
            return "unavailable:invalid-dirty-worktree-state";
        }

        string start = string.IsNullOrWhiteSpace(searchStartDirectory)
            ? Environment.CurrentDirectory
            : searchStartDirectory;
        string? repositoryRoot = FindGitRepositoryRoot(start) ??
            FindGitRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot == null)
            return "unavailable:git-worktree-not-found";

        return (gitStatusProbe ?? new PerformanceCaptureGitStatusProbe())
            .Resolve(repositoryRoot);
    }

    internal static string? FindGitRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException)
        {
            return null;
        }

        for (int depth = 0; directory != null && depth < 64; depth++)
        {
            string gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    internal static string ResolveShaderBundleHash(Assembly shaderAssembly)
    {
        ArgumentNullException.ThrowIfNull(shaderAssembly);
        string? overrideDirectory = Environment.GetEnvironmentVariable(
            ShaderOverrideDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overrideDirectory))
            return ResolveEmbeddedShaderBundleHash(shaderAssembly);

        try
        {
            overrideDirectory = Path.GetFullPath(overrideDirectory);
            if (!Directory.Exists(overrideDirectory))
                return "unavailable:shader-override-directory-not-found";

            string[] resourceNames = shaderAssembly.GetManifestResourceNames();
            Array.Sort(resourceNames, StringComparer.Ordinal);

            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendHashText(hash, "njulf-effective-shader-bundle-v1");

            int shaderResourceCount = 0;
            foreach (string resourceName in resourceNames)
            {
                if (!resourceName.StartsWith(
                        ShaderResourcePrefix,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string shaderFileName = resourceName[ShaderResourcePrefix.Length..];
                using Stream? stream = OpenEffectiveShaderStream(
                    shaderAssembly,
                    resourceName,
                    shaderFileName,
                    overrideDirectory);
                if (stream == null)
                    return "unavailable:shader-resource-missing";

                AppendHashText(hash, shaderFileName);
                AppendHashStream(hash, stream);
                shaderResourceCount++;
            }

            if (shaderResourceCount == 0)
                return "unavailable:shader-resources-not-embedded";

            return "sha256:" +
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "unavailable:shader-bundle-hash-failed";
        }
        catch (UnauthorizedAccessException)
        {
            return "unavailable:shader-bundle-hash-failed";
        }
        catch (NotSupportedException)
        {
            return "unavailable:shader-bundle-hash-failed";
        }
        catch (CryptographicException)
        {
            return "unavailable:shader-bundle-hash-failed";
        }
    }

    private static string ResolveEmbeddedShaderBundleHash(
        Assembly shaderAssembly)
    {
        foreach (AssemblyMetadataAttribute attribute in
                 shaderAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!string.Equals(
                    attribute.Key,
                    ShaderBundleHashMetadataKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string value = attribute.Value?.Trim().ToLowerInvariant() ??
                           string.Empty;
            if (value.Length == 71 &&
                value.StartsWith("sha256:", StringComparison.Ordinal) &&
                IsLowerHex(value.AsSpan(7)))
            {
                return value;
            }
            return "unavailable:shader-bundle-hash-metadata-invalid";
        }

        return "unavailable:shader-bundle-hash-not-embedded";
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    internal static string ResolveTargetFramework(Assembly applicationAssembly)
    {
        string? framework = applicationAssembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;
        return PerformanceCaptureHashing.NormalizeMetadataValue(
            framework,
            "unavailable:target-framework-not-embedded");
    }

    private static Stream? OpenEffectiveShaderStream(
        Assembly shaderAssembly,
        string resourceName,
        string shaderFileName,
        string overrideDirectory)
    {
        string candidate = Path.Combine(overrideDirectory, shaderFileName);
        if (File.Exists(candidate))
            return new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        return shaderAssembly.GetManifestResourceStream(resourceName);
    }

    private static void AppendHashText(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendHashStream(IncrementalHash hash, Stream stream)
    {
        using IncrementalHash shaderHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                shaderHash.AppendData(buffer.AsSpan(0, bytesRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        hash.AppendData(shaderHash.GetHashAndReset());
    }

    private static string ResolveBuildConfiguration()
    {
#if NJULF_SHIPPING_PERFORMANCE
        return "ShippingPerformance";
#elif NJULF_PROFILE_SYMBOLS
        return "ProfileSymbols";
#elif NJULF_DETAILED_INVESTIGATION
        return "DetailedInvestigation";
#elif NJULF_DEVELOPMENT
        return "Development";
#elif DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
