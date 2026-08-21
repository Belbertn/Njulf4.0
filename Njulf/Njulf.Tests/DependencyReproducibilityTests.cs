using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DependencyReproducibilityTests
{
    private const string KtxPackageId = "Ktx2.NET";
    private const string KtxPackageVersion = "1.0.5";
    private const string WebPMetaPackageId = "Imazen.WebP.NativeRuntime.All";
    private const string WebPRuntimePackageVersion = "1.6.1";

    [Test]
    public void SolutionProjects_HaveCompleteLocksAndReleaseCiUseLockedRestore()
    {
        string root = FindRepositoryRoot();
        XDocument buildProps = XDocument.Load(
            Path.Combine(root, "Directory.Build.props"),
            LoadOptions.PreserveWhitespace);
        XElement[] properties = buildProps
            .Descendants()
            .Where(static element => element.Name.LocalName is
                "RestorePackagesWithLockFile" or "RestoreLockedMode")
            .ToArray();
        XElement lockCreation = properties.Single(element =>
            element.Name.LocalName == "RestorePackagesWithLockFile");
        XElement lockedMode = properties.Single(element =>
            element.Name.LocalName == "RestoreLockedMode");
        string lockedCondition =
            lockedMode.Attribute("Condition")?.Value ?? string.Empty;

        Assert.Multiple(() =>
        {
            Assert.That(lockCreation.Value.Trim(), Is.EqualTo("true"));
            Assert.That(lockedMode.Value.Trim(), Is.EqualTo("true"));
            Assert.That(lockedCondition, Does.Contain("Configuration"));
            Assert.That(lockedCondition, Does.Contain("Release"));
            Assert.That(lockedCondition, Does.Contain("ContinuousIntegrationBuild"));
        });

        string solutionPath = Path.Combine(root, "Njulf.sln");
        string[] projectPaths = File.ReadLines(solutionPath)
            .Select(static line =>
                Regex.Match(
                    line,
                    "\"(?<path>[^\"]+\\.csproj)\"",
                    RegexOptions.CultureInvariant))
            .Where(static match => match.Success)
            .Select(match => Path.GetFullPath(
                Path.Combine(
                    root,
                    match.Groups["path"].Value.Replace(
                        '\\',
                        Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.That(projectPaths, Has.Length.EqualTo(11));

        var failures = new List<string>();
        foreach (string projectPath in projectPaths)
            ValidateProjectLock(projectPath, failures);

        Assert.That(
            failures,
            Is.Empty,
            string.Join(Environment.NewLine, failures));
    }

    [Test]
    public void HelloGame_ReleaseAndShippingPerformanceUseOnlyCookedAssets()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(
            Path.Combine(root, "NjulfHelloGame", "NjulfHelloGame.csproj"));
        const string cookedConfigurationCondition =
            "'$(Configuration)' == 'Release' Or '$(Configuration)' == 'ShippingPerformance'";
        const string sourceAssetsCondition = "'$(CookedAssetsOnly)' != 'true'";

        XElement cookedAssetsOnly = project.Descendants()
            .Single(element => element.Name.LocalName == "CookedAssetsOnly");
        XElement editorDefine = project.Descendants()
            .Single(element =>
                element.Name.LocalName == "DefineConstants" &&
                element.Value.Contains("NJULF_EDITOR", StringComparison.Ordinal));
        XElement editorReference = project.Descendants()
            .Single(element =>
                element.Name.LocalName == "ProjectReference" &&
                string.Equals(
                    element.Attribute("Include")?.Value,
                    "..\\Njulf.Editor\\Njulf.Editor.csproj",
                    StringComparison.Ordinal));
        string[] rawSourceItems =
        [
            "NewSponza_Main_glTF_003.gltf",
            "NewSponza_Main_glTF_003.bin",
            "NewSponza_Curtains_glTF.gltf",
            "NewSponza_Curtains_glTF.bin",
            "Strut.glb",
            "Assets\\**\\*.*",
            "textures\\**\\*.*"
        ];
        XElement[] rawSources = project.Descendants()
            .Where(element =>
                element.Name.LocalName == "None" &&
                rawSourceItems.Contains(
                    element.Attribute("Update")?.Value,
                    StringComparer.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(cookedAssetsOnly.Value.Trim(), Is.EqualTo("true"));
            Assert.That(
                cookedAssetsOnly.Attribute("Condition")?.Value,
                Is.EqualTo(cookedConfigurationCondition));
            Assert.That(
                editorDefine.Attribute("Condition")?.Value,
                Is.EqualTo(sourceAssetsCondition));
            Assert.That(
                editorReference.Attribute("Condition")?.Value,
                Is.EqualTo(sourceAssetsCondition));
            Assert.That(rawSources, Has.Length.EqualTo(rawSourceItems.Length));
            Assert.That(
                rawSources.Select(element => element.Attribute("Condition")?.Value),
                Is.All.EqualTo(sourceAssetsCondition));
        });
    }

    [Test]
    public void AssetToolKtxRedistribution_MatchesRestoredPackageNoticeAndBuildContract()
    {
        string root = FindRepositoryRoot();
        string assetToolDirectory = Path.Combine(root, "Njulf.AssetTool");
        string provenancePath =
            Path.Combine(assetToolDirectory, "Ktx2.NET.provenance.json");
        string noticePath =
            Path.Combine(assetToolDirectory, "THIRD-PARTY-NOTICES.txt");
        using JsonDocument provenance = JsonDocument.Parse(
            File.ReadAllBytes(provenancePath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        JsonElement provenanceRoot = provenance.RootElement;
        JsonElement package = provenanceRoot.GetProperty("package");
        JsonElement redistribution = provenanceRoot.GetProperty("redistribution");
        JsonElement upstream = provenanceRoot.GetProperty("nativeUpstream");

        Assert.Multiple(() =>
        {
            Assert.That(
                provenanceRoot.GetProperty("schemaVersion").GetString(),
                Is.EqualTo("njulf-third-party-native-provenance/v1"));
            Assert.That(package.GetProperty("id").GetString(), Is.EqualTo(KtxPackageId));
            Assert.That(
                package.GetProperty("version").GetString(),
                Is.EqualTo(KtxPackageVersion));
            Assert.That(
                package.GetProperty("licenseExpression").GetString(),
                Is.EqualTo("MIT"));
            Assert.That(
                package.GetProperty("declaredRepositoryCommit").GetString(),
                Does.Match("^[0-9a-f]{40}$"));
            Assert.That(
                redistribution.GetProperty("payloadSourceMatch")
                    .GetProperty("commit").GetString(),
                Does.Match("^[0-9a-f]{40}$"));
            Assert.That(
                upstream.GetProperty("exactSourceRevision").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
            Assert.That(
                upstream.GetProperty("limitation").GetString(),
                Does.Contain("does not publish"));
        });

        string assetsJsonPath =
            Path.Combine(root, "Njulf.Assets", "obj", "project.assets.json");
        using JsonDocument assetsJson =
            JsonDocument.Parse(File.ReadAllBytes(assetsJsonPath));
        string packageKey = $"{KtxPackageId}/{KtxPackageVersion}";
        JsonElement packageLibrary =
            assetsJson.RootElement.GetProperty("libraries").GetProperty(packageKey);
        string packageRoot = assetsJson.RootElement.GetProperty("packageFolders")
            .EnumerateObject()
            .Select(folder => Path.Combine(
                folder.Name,
                KtxPackageId.ToLowerInvariant(),
                KtxPackageVersion))
            .First(Directory.Exists);

        string[] restoredPayloadPaths = packageLibrary.GetProperty("files")
            .EnumerateArray()
            .Select(static path => path.GetString()!)
            .Where(static path =>
                string.Equals(
                    path,
                    "lib/net8.0/Ktx2.NET.dll",
                    StringComparison.Ordinal) ||
                path.StartsWith("runtimes/", StringComparison.Ordinal) &&
                path.Contains("/native/", StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        JsonElement[] manifestAssets = redistribution.GetProperty("assets")
            .EnumerateArray()
            .ToArray();
        string[] manifestPayloadPaths = manifestAssets
            .Select(static asset =>
                asset.GetProperty("packageRelativePath").GetString()!)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.That(
            manifestPayloadPaths,
            Is.EqualTo(restoredPayloadPaths),
            "The provenance manifest must enumerate every redistributed managed/native package payload.");

        foreach (JsonElement asset in manifestAssets)
        {
            string relativePath =
                asset.GetProperty("packageRelativePath").GetString()!;
            string restoredPath = Path.Combine(
                packageRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Multiple(() =>
            {
                Assert.That(
                    File.Exists(restoredPath),
                    Is.True,
                    $"Restored payload '{relativePath}' is missing.");
                Assert.That(
                    new FileInfo(restoredPath).Length,
                    Is.EqualTo(asset.GetProperty("length").GetInt64()),
                    $"{relativePath} length differs from reviewed provenance.");
                Assert.That(
                    ComputeSha256(restoredPath),
                    Is.EqualTo(asset.GetProperty("sha256").GetString()),
                    $"{relativePath} hash differs from reviewed provenance.");
            });
        }

        string[] nativeRids = manifestAssets
            .Where(static asset =>
                asset.GetProperty("kind").GetString() == "native")
            .Select(static asset =>
                asset.GetProperty("runtimeIdentifier").GetString()!)
            .OrderBy(static rid => rid, StringComparer.Ordinal)
            .ToArray();
        Assert.That(nativeRids, Is.EqualTo(new[] { "linux-x64", "win-x64" }));

        string nupkgPath = Path.Combine(
            packageRoot,
            package.GetProperty("nupkgFileName").GetString()!);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(nupkgPath), Is.True);
            Assert.That(
                new FileInfo(nupkgPath).Length,
                Is.EqualTo(package.GetProperty("nupkgLength").GetInt64()));
            Assert.That(
                ComputeSha256(nupkgPath),
                Is.EqualTo(package.GetProperty("nupkgSha256").GetString()));
            Assert.That(
                ComputeSha512Base64(nupkgPath),
                Is.EqualTo(package.GetProperty("nupkgSha512Base64").GetString()));
        });

        using JsonDocument packageMetadata = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(packageRoot, ".nupkg.metadata")));
        string nugetContentHash =
            packageMetadata.RootElement.GetProperty("contentHash").GetString()!;
        Assert.That(
            nugetContentHash,
            Is.EqualTo(
                package.GetProperty("nugetContentHashSha512Base64").GetString()));
        Assert.That(Convert.FromBase64String(nugetContentHash), Has.Length.EqualTo(64));

        string notice = File.ReadAllText(noticePath);
        Assert.Multiple(() =>
        {
            Assert.That(
                redistribution.GetProperty("noticeRelativePath").GetString(),
                Is.EqualTo(Path.GetFileName(noticePath)));
            Assert.That(
                ComputeSha256(noticePath),
                Is.EqualTo(redistribution.GetProperty("noticeSha256").GetString()));
            Assert.That(notice, Does.Contain($"{KtxPackageId} {KtxPackageVersion}"));
            Assert.That(
                notice,
                Does.Contain(package.GetProperty("nupkgSha256").GetString()!));
            Assert.That(
                notice,
                Does.Contain(redistribution.GetProperty("payloadSourceMatch")
                    .GetProperty("commit").GetString()!));
            Assert.That(notice, Does.Contain("MIT License"));
            Assert.That(notice, Does.Contain("Apache License"));
            Assert.That(notice, Does.Contain("BSD 3-Clause License"));
            Assert.That(notice, Does.Contain("exact KTX-Software source revision"));
        });
        foreach (JsonElement asset in manifestAssets)
        {
            Assert.That(
                notice,
                Does.Contain(asset.GetProperty("packageRelativePath").GetString()!));
            Assert.That(
                notice,
                Does.Contain(asset.GetProperty("sha256").GetString()!));
        }

        ValidateAssetToolBuildContract(
            Path.Combine(assetToolDirectory, "Njulf.AssetTool.csproj"),
            manifestAssets,
            ComputeSha256(noticePath),
            ComputeSha256(provenancePath));

        string assetsLockPath =
            Path.Combine(root, "Njulf.Assets", "packages.lock.json");
        using JsonDocument assetsLock =
            JsonDocument.Parse(File.ReadAllBytes(assetsLockPath));
        JsonElement lockedKtx = assetsLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0")
            .GetProperty(KtxPackageId);
        Assert.Multiple(() =>
        {
            Assert.That(lockedKtx.GetProperty("type").GetString(), Is.EqualTo("Direct"));
            Assert.That(
                lockedKtx.GetProperty("resolved").GetString(),
                Is.EqualTo(KtxPackageVersion));
            Assert.That(
                lockedKtx.GetProperty("contentHash").GetString(),
                Is.EqualTo(nugetContentHash));
        });
    }

    [Test]
    public void WebPRedistribution_MatchesEveryRestoredRuntimeAndReviewedUpstream()
    {
        string root = FindRepositoryRoot();
        string assetToolDirectory = Path.Combine(root, "Njulf.AssetTool");
        string provenancePath =
            Path.Combine(assetToolDirectory, "Imazen.WebP.provenance.json");
        string noticePath =
            Path.Combine(assetToolDirectory, "THIRD-PARTY-NOTICES.txt");
        using JsonDocument provenance = JsonDocument.Parse(
            File.ReadAllBytes(provenancePath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        JsonElement provenanceRoot = provenance.RootElement;
        JsonElement decoder = provenanceRoot.GetProperty("decoderContract");
        JsonElement packaging = provenanceRoot.GetProperty("packaging");
        JsonElement metaPackage = packaging.GetProperty("metaPackage");
        JsonElement upstream = provenanceRoot.GetProperty("nativeUpstream");
        JsonElement redistribution = provenanceRoot.GetProperty("redistribution");

        Assert.Multiple(() =>
        {
            Assert.That(
                provenanceRoot.GetProperty("schemaVersion").GetString(),
                Is.EqualTo("njulf-third-party-native-provenance/v1"));
            Assert.That(
                decoder.GetProperty("requiredDecoderVersionHex").GetString(),
                Is.EqualTo("0x010600"));
            Assert.That(
                decoder.GetProperty("requiredDecoderVersion").GetString(),
                Is.EqualTo("1.6.0"));
            Assert.That(
                decoder.GetProperty("defaultMaximumEncodedBytes").GetInt32(),
                Is.EqualTo(WebPTextureDecoder.DefaultMaximumEncodedBytes));
            Assert.That(
                decoder.GetProperty("defaultMaximumDecodedPixels").GetInt64(),
                Is.EqualTo(WebPTextureDecoder.DefaultMaximumDecodedPixels));
            Assert.That(
                packaging.GetProperty("licenseExpression").GetString(),
                Is.EqualTo("MIT"));
            Assert.That(
                packaging.GetProperty("declaredRepositoryCommit").GetString(),
                Is.EqualTo("462cd4a3bb76c171ff818cd16b0779614c3f8044"));
            Assert.That(
                upstream.GetProperty("releaseTag").GetString(),
                Is.EqualTo("v1.6.0"));
            Assert.That(
                upstream.GetProperty("exactSourceRevision").GetString(),
                Is.EqualTo("4fa21912338357f89e4fd51cf2368325b59e9bd9"));
            Assert.That(
                upstream.GetProperty("license").GetString(),
                Is.EqualTo("BSD-3-Clause"));
            Assert.That(
                WebPTextureDecoder.DecoderVersion,
                Does.Contain("libwebp/1.6.0"));
            Assert.That(
                WebPTextureDecoder.DecoderVersion,
                Does.Contain("NativeRuntime.All/1.6.1"));
        });

        string assetsJsonPath =
            Path.Combine(root, "Njulf.Assets", "obj", "project.assets.json");
        using JsonDocument assetsJson =
            JsonDocument.Parse(File.ReadAllBytes(assetsJsonPath));
        JsonElement assetsRoot = assetsJson.RootElement;
        JsonElement libraries = assetsRoot.GetProperty("libraries");
        string packageFolder = assetsRoot.GetProperty("packageFolders")
            .EnumerateObject()
            .Select(static folder => folder.Name)
            .First();
        Assert.Multiple(() =>
        {
            Assert.That(
                libraries.TryGetProperty("Imazen.WebP/11.0.0", out _),
                Is.False,
                "The managed wrapper is not used; it would add System.Drawing and bypass Njulf's bounded decode.");
            Assert.That(
                libraries.EnumerateObject().Any(static library =>
                    library.Name.StartsWith(
                        "System.Drawing.Common/",
                        StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "WebP ingestion must remain independent of System.Drawing.Common.");
        });

        ValidateNuGetPackageIdentity(
            packageFolder,
            libraries,
            metaPackage,
            WebPMetaPackageId,
            WebPRuntimePackageVersion);

        JsonElement[] runtimePackages = redistribution
            .GetProperty("runtimePackages")
            .EnumerateArray()
            .ToArray();
        Assert.That(runtimePackages, Has.Length.EqualTo(7));
        var restoredPayloads = new List<string>();
        foreach (JsonElement runtimePackage in runtimePackages)
        {
            string packageId = runtimePackage.GetProperty("id").GetString()!;
            string version = runtimePackage.GetProperty("version").GetString()!;
            ValidateNuGetPackageIdentity(
                packageFolder,
                libraries,
                runtimePackage,
                packageId,
                version);

            string packageRoot = Path.Combine(
                packageFolder,
                packageId.ToLowerInvariant(),
                version);
            string packageKey = $"{packageId}/{version}";
            string[] restoredNativePaths = libraries.GetProperty(packageKey)
                .GetProperty("files")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .Where(static path =>
                    path.StartsWith("runtimes/", StringComparison.Ordinal) &&
                    path.Contains("/native/", StringComparison.Ordinal))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            JsonElement[] assets = runtimePackage.GetProperty("assets")
                .EnumerateArray()
                .ToArray();
            string[] reviewedNativePaths = assets
                .Select(static asset =>
                    asset.GetProperty("packageRelativePath").GetString()!)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.That(
                reviewedNativePaths,
                Is.EqualTo(restoredNativePaths),
                $"{packageId} provenance must enumerate every native payload.");

            foreach (JsonElement asset in assets)
            {
                string relativePath =
                    asset.GetProperty("packageRelativePath").GetString()!;
                string restoredPath = Path.Combine(
                    packageRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                string testOutputPath = Path.Combine(
                    AppContext.BaseDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                restoredPayloads.Add(relativePath);
                Assert.Multiple(() =>
                {
                    Assert.That(
                        File.Exists(restoredPath),
                        Is.True,
                        $"Restored payload '{relativePath}' is missing.");
                    Assert.That(
                        new FileInfo(restoredPath).Length,
                        Is.EqualTo(asset.GetProperty("length").GetInt64()),
                        $"{relativePath} length differs from reviewed provenance.");
                    Assert.That(
                        ComputeSha256(restoredPath),
                        Is.EqualTo(asset.GetProperty("sha256").GetString()),
                        $"{relativePath} hash differs from reviewed provenance.");
                    Assert.That(
                        File.Exists(testOutputPath),
                        Is.True,
                        $"Build output is missing reviewed runtime payload '{relativePath}'.");
                    Assert.That(
                        ComputeSha256(testOutputPath),
                        Is.EqualTo(asset.GetProperty("sha256").GetString()),
                        $"Build output payload '{relativePath}' differs from reviewed provenance.");
                });
            }
        }

        string[] runtimeIdentifiers = restoredPayloads
            .Select(static path => path.Split('/')[1])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(
                restoredPayloads,
                Has.Count.EqualTo(
                    redistribution.GetProperty("completeNativePayloadCount").GetInt32()));
            Assert.That(
                runtimeIdentifiers,
                Is.EqualTo(new[]
                {
                    "linux-arm64",
                    "linux-x64",
                    "osx-arm64",
                    "osx-x64",
                    "win-arm64",
                    "win-x64",
                    "win-x86"
                }));
        });

        string notice = File.ReadAllText(noticePath);
        Assert.Multiple(() =>
        {
            Assert.That(
                ComputeSha256(noticePath),
                Is.EqualTo(redistribution.GetProperty("noticeSha256").GetString()));
            Assert.That(notice, Does.Contain("Imazen.WebP native runtime packages 1.6.1"));
            Assert.That(notice, Does.Contain("libwebp 1.6.0"));
            Assert.That(
                notice,
                Does.Contain(upstream.GetProperty("exactSourceRevision").GetString()!));
            Assert.That(notice, Does.Contain("MIT License"));
            Assert.That(notice, Does.Contain("BSD-3-Clause"));
        });

        XDocument assetToolProject = XDocument.Load(
            Path.Combine(assetToolDirectory, "Njulf.AssetTool.csproj"));
        XElement webPContent = assetToolProject.Descendants()
            .Single(element =>
                element.Name.LocalName == "None" &&
                string.Equals(
                    element.Attribute("Update")?.Value,
                    "Imazen.WebP.provenance.json",
                    StringComparison.Ordinal));
        XElement webPHash = assetToolProject.Descendants()
            .Single(element =>
                element.Name.LocalName == "WebPProvenanceManifestSha256");
        Assert.Multiple(() =>
        {
            Assert.That(
                webPContent.Attribute("CopyToOutputDirectory")?.Value,
                Is.EqualTo("PreserveNewest"));
            Assert.That(
                webPContent.Attribute("CopyToPublishDirectory")?.Value,
                Is.EqualTo("PreserveNewest"));
            Assert.That(
                webPHash.Value,
                Is.EqualTo(ComputeSha256(provenancePath)));
        });

        XDocument assetsProject = XDocument.Load(
            Path.Combine(root, "Njulf.Assets", "Njulf.Assets.csproj"));
        XElement webPPackageReference = assetsProject.Descendants()
            .Single(element =>
                element.Name.LocalName == "PackageReference" &&
                string.Equals(
                    element.Attribute("Include")?.Value,
                    WebPMetaPackageId,
                    StringComparison.Ordinal));
        XDocument sampleProject = XDocument.Load(
            Path.Combine(root, "NjulfHelloGame", "NjulfHelloGame.csproj"));
        XElement sampleProvenance = sampleProject.Descendants()
            .Single(element =>
                element.Name.LocalName == "None" &&
                string.Equals(
                    element.Attribute("Link")?.Value,
                    "Imazen.WebP.provenance.json",
                    StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(
                webPPackageReference.Attribute("Version")?.Value,
                Is.EqualTo(WebPRuntimePackageVersion));
            Assert.That(
                webPPackageReference.Descendants()
                    .Any(static element =>
                        element.Name.LocalName == "ExcludeAssets" &&
                        element.Value.Contains("runtime", StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "Cooked Release hosts support WebP hot reload and must retain the pinned native runtime.");
            Assert.That(
                sampleProvenance.Attribute("CopyToOutputDirectory")?.Value,
                Is.EqualTo("PreserveNewest"));
            Assert.That(
                sampleProvenance.Attribute("CopyToPublishDirectory")?.Value,
                Is.EqualTo("PreserveNewest"));
        });

        string assetsLockPath =
            Path.Combine(root, "Njulf.Assets", "packages.lock.json");
        using JsonDocument assetsLock =
            JsonDocument.Parse(File.ReadAllBytes(assetsLockPath));
        JsonElement lockedDependencies = assetsLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0");
        JsonElement lockedMeta = lockedDependencies.GetProperty(WebPMetaPackageId);
        Assert.Multiple(() =>
        {
            Assert.That(lockedMeta.GetProperty("type").GetString(), Is.EqualTo("Direct"));
            Assert.That(
                lockedMeta.GetProperty("resolved").GetString(),
                Is.EqualTo(WebPRuntimePackageVersion));
            Assert.That(
                lockedMeta.GetProperty("contentHash").GetString(),
                Is.EqualTo(
                    metaPackage.GetProperty("nugetContentHashSha512Base64").GetString()));
        });
        foreach (JsonElement runtimePackage in runtimePackages)
        {
            string packageId = runtimePackage.GetProperty("id").GetString()!;
            JsonElement lockedRuntime = lockedDependencies.GetProperty(packageId);
            Assert.Multiple(() =>
            {
                Assert.That(
                    lockedRuntime.GetProperty("type").GetString(),
                    Is.EqualTo("Transitive"));
                Assert.That(
                    lockedRuntime.GetProperty("resolved").GetString(),
                    Is.EqualTo(WebPRuntimePackageVersion));
                Assert.That(
                    lockedRuntime.GetProperty("contentHash").GetString(),
                    Is.EqualTo(
                        runtimePackage
                            .GetProperty("nugetContentHashSha512Base64")
                            .GetString()));
            });
        }
    }

    private static void ValidateNuGetPackageIdentity(
        string packageFolder,
        JsonElement libraries,
        JsonElement reviewedPackage,
        string packageId,
        string version)
    {
        string packageRoot = Path.Combine(
            packageFolder,
            packageId.ToLowerInvariant(),
            version);
        string packageKey = $"{packageId}/{version}";
        Assert.That(
            libraries.TryGetProperty(packageKey, out _),
            Is.True,
            $"Restored assets omit {packageKey}.");

        string nupkgFileName = reviewedPackage.TryGetProperty(
                "nupkgFileName",
                out JsonElement explicitFileName)
            ? explicitFileName.GetString()!
            : $"{packageId.ToLowerInvariant()}.{version}.nupkg";
        string nupkgPath = Path.Combine(packageRoot, nupkgFileName);
        using JsonDocument packageMetadata = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(packageRoot, ".nupkg.metadata")));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(nupkgPath), Is.True);
            Assert.That(
                new FileInfo(nupkgPath).Length,
                Is.EqualTo(reviewedPackage.GetProperty("nupkgLength").GetInt64()));
            Assert.That(
                ComputeSha256(nupkgPath),
                Is.EqualTo(reviewedPackage.GetProperty("nupkgSha256").GetString()));
            Assert.That(
                ComputeSha512Base64(nupkgPath),
                Is.EqualTo(
                    reviewedPackage.GetProperty("nupkgSha512Base64").GetString()));
            Assert.That(
                packageMetadata.RootElement.GetProperty("contentHash").GetString(),
                Is.EqualTo(
                    reviewedPackage
                        .GetProperty("nugetContentHashSha512Base64")
                        .GetString()));
        });
    }

    private static void ValidateProjectLock(
        string projectPath,
        ICollection<string> failures)
    {
        string projectName = Path.GetFileName(projectPath);
        string lockPath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "packages.lock.json");
        if (!File.Exists(lockPath))
        {
            failures.Add($"{projectName}: packages.lock.json is missing.");
            return;
        }

        using JsonDocument lockDocument =
            JsonDocument.Parse(File.ReadAllBytes(lockPath));
        JsonElement root = lockDocument.RootElement;
        if (root.GetProperty("version").GetInt32() != 1)
            failures.Add($"{projectName}: lock format is not version 1.");
        if (!root.GetProperty("dependencies").TryGetProperty(
                "net10.0",
                out JsonElement lockedDependencies))
        {
            failures.Add($"{projectName}: net10.0 lock target is absent.");
            return;
        }

        foreach (JsonProperty dependency in lockedDependencies.EnumerateObject())
        {
            JsonElement value = dependency.Value;
            if (!value.TryGetProperty("type", out JsonElement typeElement))
            {
                failures.Add(
                    $"{projectName}: locked entry '{dependency.Name}' has no dependency type.");
                continue;
            }

            string? dependencyType = typeElement.GetString();
            if (dependencyType == "Project")
                continue;
            if (dependencyType is not ("Direct" or "Transitive"))
            {
                failures.Add(
                    $"{projectName}: locked entry '{dependency.Name}' has unknown type '{dependencyType}'.");
                continue;
            }
            if (!value.TryGetProperty("resolved", out _) ||
                !value.TryGetProperty("contentHash", out JsonElement contentHash))
            {
                failures.Add(
                    $"{projectName}: locked package '{dependency.Name}' lacks resolved identity/hash metadata.");
                continue;
            }
            try
            {
                if (Convert.FromBase64String(contentHash.GetString()!).Length != 64)
                {
                    failures.Add(
                        $"{projectName}: '{dependency.Name}' content hash is not SHA-512.");
                }
            }
            catch (FormatException)
            {
                failures.Add(
                    $"{projectName}: '{dependency.Name}' content hash is malformed.");
            }
        }

        XDocument project = XDocument.Load(projectPath);
        foreach (XElement packageReference in project.Descendants()
                     .Where(static element =>
                         element.Name.LocalName == "PackageReference"))
        {
            string? packageId = packageReference.Attribute("Include")?.Value;
            string? version =
                packageReference.Attribute("Version")?.Value ??
                packageReference.Elements()
                    .SingleOrDefault(static element =>
                        element.Name.LocalName == "Version")
                    ?.Value;
            if (string.IsNullOrWhiteSpace(packageId) ||
                string.IsNullOrWhiteSpace(version))
            {
                failures.Add(
                    $"{projectName}: a direct PackageReference has no literal identity/version.");
                continue;
            }
            if (!lockedDependencies.TryGetProperty(packageId, out JsonElement locked))
            {
                failures.Add(
                    $"{projectName}: direct package '{packageId}' is absent from its lock.");
                continue;
            }
            string? type = locked.GetProperty("type").GetString();
            string? resolved = locked.GetProperty("resolved").GetString();
            string? requested = locked.GetProperty("requested").GetString();
            if (type != "Direct" ||
                resolved != version ||
                requested != $"[{version}, )")
            {
                failures.Add(
                    $"{projectName}: direct package '{packageId}' lock is stale " +
                    $"(type={type}, requested={requested}, resolved={resolved}, project={version}).");
            }
        }
    }

    private static void ValidateAssetToolBuildContract(
        string projectPath,
        IReadOnlyList<JsonElement> manifestAssets,
        string noticeSha256,
        string provenanceSha256)
    {
        XDocument project = XDocument.Load(projectPath);
        XElement[] contentItems = project.Descendants()
            .Where(static element => element.Name.LocalName == "None")
            .ToArray();
        foreach (string fileName in new[]
                 {
                     "THIRD-PARTY-NOTICES.txt",
                     "Ktx2.NET.provenance.json"
                 })
        {
            XElement item = contentItems.Single(element =>
                string.Equals(
                    element.Attribute("Update")?.Value,
                    fileName,
                    StringComparison.Ordinal));
            Assert.Multiple(() =>
            {
                Assert.That(
                    item.Attribute("CopyToOutputDirectory")?.Value,
                    Is.EqualTo("PreserveNewest"));
                Assert.That(
                    item.Attribute("CopyToPublishDirectory")?.Value,
                    Is.EqualTo("PreserveNewest"));
            });
        }

        Dictionary<string, string> buildHashes = project.Descendants()
            .Where(static element =>
                element.Name.LocalName == "Ktx2RedistributedAsset")
            .ToDictionary(
                static element => element.Elements()
                    .Single(child =>
                        child.Name.LocalName == "PackageRelativePath")
                    .Value,
                static element => element.Elements()
                    .Single(child =>
                        child.Name.LocalName == "ExpectedSha256")
                    .Value,
                StringComparer.Ordinal);
        Dictionary<string, string> manifestHashes = manifestAssets.ToDictionary(
            static asset =>
                asset.GetProperty("packageRelativePath").GetString()!,
            static asset => asset.GetProperty("sha256").GetString()!,
            StringComparer.Ordinal);
        Assert.That(buildHashes, Is.EqualTo(manifestHashes));

        Dictionary<string, string> buildProperties = project.Descendants()
            .Where(static element => element.Name.LocalName is
                "Ktx2ThirdPartyNoticeSha256" or
                "Ktx2ProvenanceManifestSha256")
            .ToDictionary(
                static element => element.Name.LocalName,
                static element => element.Value,
                StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(
                buildProperties["Ktx2ThirdPartyNoticeSha256"],
                Is.EqualTo(noticeSha256));
            Assert.That(
                buildProperties["Ktx2ProvenanceManifestSha256"],
                Is.EqualTo(provenanceSha256));
        });

        string[] targetNames = project.Descendants()
            .Where(static element => element.Name.LocalName == "Target")
            .Select(static element => element.Attribute("Name")?.Value)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(targetNames, Does.Contain("ValidateKtx2RedistributionInputs"));
            Assert.That(targetNames, Does.Contain("ValidateKtx2RedistributionOutputs"));
        });
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha512Base64(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory =
            new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Njulf repository root from the test directory.");
    }
}
