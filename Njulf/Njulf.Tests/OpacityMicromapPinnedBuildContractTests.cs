using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpacityMicromapPinnedBuildContractTests
{
    private const string ReviewedCommit =
        "9abacd0f187d0efca491946a29ba7df8c5345264";

    [Test]
    public void NativeBridgeBuild_IsStaticPinnedOfflineAndLicenseComplete()
    {
        string root = FindRepositoryRoot();
        string nativeRoot = Path.Combine(
            root,
            "native",
            "opacity_micromap_bridge");
        string cmake = File.ReadAllText(Path.Combine(nativeRoot, "CMakeLists.txt"));
        string build = File.ReadAllText(Path.Combine(
            nativeRoot,
            "Build-PinnedBridge.ps1"));
        string readme = File.ReadAllText(Path.Combine(nativeRoot, "README.md"));
        string notices = File.ReadAllText(Path.Combine(
            root,
            "Njulf.AssetTool",
            "THIRD-PARTY-NOTICES.txt"));

        Assert.Multiple(() =>
        {
            Assert.That(cmake, Does.Contain("OMM_STATIC_LIBRARY ON"));
            Assert.That(cmake, Does.Contain("OMM_ENABLE_FAST_MATH OFF"));
            Assert.That(cmake, Does.Contain("OMM_ENABLE_OPENMP OFF"));
            Assert.That(cmake, Does.Contain("SHADERMAKE_FIND_COMPILERS OFF"));
            Assert.That(cmake, Does.Contain("SHADERMAKE_FIND_DXC OFF"));
            Assert.That(cmake, Does.Contain("SHADERMAKE_FIND_SLANG OFF"));
            Assert.That(cmake, Does.Contain("NJULF_OMM_VERSION STREQUAL \"1.9.2\""));
            Assert.That(cmake, Does.Not.Contain("find_package(omm"));

            Assert.That(build, Does.Contain(ReviewedCommit));
            Assert.That(build, Does.Contain("$expectedSdkVersion = '1.9.2'"));
            Assert.That(build, Does.Contain("--untracked-files=no"));
            Assert.That(build, Does.Contain("/dependents"));
            Assert.That(build, Does.Contain("/loadconfig"));
            Assert.That(build, Does.Contain("CF instrumented"));
            Assert.That(build, Does.Contain("binarySha256"));
            Assert.That(build, Does.Contain("NVIDIA-RTX-SDKs-LICENSE.txt"));
            Assert.That(build, Does.Contain("external/glm/copying.txt"));
            Assert.That(build, Does.Contain("external/lz4/LICENSE"));
            Assert.That(build, Does.Contain("external/stb/LICENSE"));
            Assert.That(build, Does.Contain("external/xxHash/LICENSE"));

            Assert.That(readme, Does.Contain(ReviewedCommit));
            Assert.That(readme, Does.Contain("statically links"));
            Assert.That(notices, Does.Contain(
                "LicenseRef-NVIDIA-RTX-SDKs-2023-01-23"));
            Assert.That(notices, Does.Contain(
                "This software contains source code provided by NVIDIA Corporation."));
        });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing Njulf.sln and native/.");
    }
}
