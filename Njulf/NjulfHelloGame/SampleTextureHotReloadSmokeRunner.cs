using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal enum SampleTextureHotReloadCaptureStage : byte
{
    Initial = 0,
    Replacement = 1,
    Rollback = 2
}

internal sealed record SampleTextureHotReloadCapture(
    SampleTextureHotReloadCaptureStage Stage,
    LinearHdrCaptureState State,
    string OutputPath,
    int Width,
    int Height,
    float[]? Pixels,
    string Sha256,
    string Error);

internal interface ISampleTextureHotReloadSession
{
    uint TextureRevision { get; }
    uint MaterialProfileRevision { get; }
    int BindlessDescriptorCount { get; }
    int RenderedGeometryBindingCount { get; }
    ulong SourceContentHash { get; }
    Vector3 MeanDiffuseReflectance { get; }
    TextureContentReloadResult ReloadReplacement();
    TextureContentReloadResult ReloadOriginal();
    bool QueueCapture(SampleTextureHotReloadCaptureStage stage);
    SampleTextureHotReloadCapture GetCapture(
        SampleTextureHotReloadCaptureStage stage);
    void Restore();
}

/// <summary>
/// Renderer-thread production fixture for an authenticated cooked KTX2
/// replacement. The material is attached to live scene geometry and every
/// state is captured from pre-exposure SceneColor, so qualification proves
/// rendered publication rather than only CPU bookkeeping.
/// </summary>
internal sealed class SampleTextureHotReloadSession :
    ISampleTextureHotReloadSession
{
    private const string SourceIdentity =
        "smoke://authenticated-rendered-texture-hot-reload";
    private const uint Rgba8Srgb = 43;
    private const int Width = 2;
    private const int Height = 2;
    private const int MaximumCookedTextureBytes = 1 * 1024 * 1024;
    private const long MaximumCaptureBytes =
        PfmLinearImageCodec.MaximumEncodedBytes;
    private static ReadOnlySpan<byte> Ktx2Identifier =>
    [
        0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB,
        0x0D, 0x0A, 0x1A, 0x0A
    ];
    private static readonly byte[] OriginalPixels =
        CreateUniformRgba(48, 112, 208, 255);
    private static readonly byte[] ReplacementPixels =
        CreateUniformRgba(224, 40, 72, 255);

    private readonly TextureManager _textureManager;
    private readonly MaterialManager _materialManager;
    private readonly VulkanRenderer _renderer;
    private readonly SampleRenderSettingsSnapshot _settingsSnapshot;
    private readonly string _artifactDirectory;
    private readonly string _ktx2Path;
    private readonly List<SceneMaterialBinding> _sceneBindings = [];
    private readonly Dictionary<
        SampleTextureHotReloadCaptureStage,
        string> _capturePaths = [];
    private readonly Dictionary<
        SampleTextureHotReloadCaptureStage,
        SampleTextureHotReloadCapture> _completedCaptures = [];
    private readonly TextureHandle _texture;
    private readonly MaterialHandle _material;
    private int _encodedKtx2ByteLength;
    private bool _restored;

    public SampleTextureHotReloadSession(
        TextureManager textureManager,
        MaterialManager materialManager,
        Scene scene,
        VulkanRenderer renderer)
    {
        _textureManager =
            textureManager ?? throw new ArgumentNullException(nameof(textureManager));
        _materialManager =
            materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        ArgumentNullException.ThrowIfNull(scene);
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _settingsSnapshot =
            SampleRenderSettingsSnapshot.Capture(_renderer.Settings);
        _artifactDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "material-gi",
            "texture-hot-reload",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactDirectory);
        _ktx2Path = Path.Combine(
            _artifactDirectory,
            "rendered-hot-reload.ktx2");

        try
        {
            PublishCookedTexture(OriginalPixels);
            _texture = _textureManager.LoadTexture(
                CreateSource(),
                TextureSamplerDescription.Default,
                generateMipmaps: false,
                srgb: true,
                semantic: TextureSemantic.Color);
            _material = _materialManager.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "Smoke.RenderedCookedTextureHotReload",
                    BaseColorFactor = Vector4.One,
                    MetallicFactor = 0f,
                    RoughnessFactor = 0.65f,
                    BaseColor = new MaterialTextureBinding
                    {
                        Texture = _texture,
                        Sampler = TextureSamplerDescription.Default
                    }
                });

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible ||
                    !renderObject.Enabled ||
                    renderObject.Mesh is not MeshHandle { IsValid: true })
                {
                    continue;
                }

                _sceneBindings.Add(
                    new SceneMaterialBinding(
                        renderObject,
                        renderObject.Material));
                renderObject.Material = _material;
            }

            ConfigureEvidenceSettings(_renderer.Settings);
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public uint TextureRevision =>
        _textureManager.GetTextureContentRevision(_texture);

    public uint MaterialProfileRevision =>
        _materialManager.GetMaterialTransportProfile(_material)
            .AlgorithmVersion == 0
            ? 0
            : _materialManager.GetMaterialData(_material)
                .TransportProfileRevision;

    public int BindlessDescriptorCount =>
        _textureManager.TextureBindlessUsedCount;

    public int RenderedGeometryBindingCount => _sceneBindings.Count;

    public ulong SourceContentHash =>
        _textureManager.TryGetTextureTransportStatistics(
            _texture,
            out TextureTransportStatistics statistics)
            ? statistics.SourceContentHash
            : 0;

    public Vector3 MeanDiffuseReflectance =>
        _materialManager.GetMaterialTransportProfile(_material)
            .MeanDiffuseReflectance;

    public TextureContentReloadResult ReloadReplacement()
    {
        PublishCookedTexture(ReplacementPixels);
        return Reload();
    }

    public TextureContentReloadResult ReloadOriginal()
    {
        PublishCookedTexture(OriginalPixels);
        return Reload();
    }

    public bool QueueCapture(SampleTextureHotReloadCaptureStage stage)
    {
        if (_capturePaths.ContainsKey(stage))
        {
            throw new InvalidOperationException(
                $"Capture stage '{stage}' was queued more than once.");
        }

        string path = Path.Combine(
            _artifactDirectory,
            $"{(int)stage:00}-{stage.ToString().ToLowerInvariant()}.pfm");
        if (!_renderer.RequestLinearHdrCapture(path))
            return false;

        _capturePaths.Add(stage, path);
        return true;
    }

    public SampleTextureHotReloadCapture GetCapture(
        SampleTextureHotReloadCaptureStage stage)
    {
        if (_completedCaptures.TryGetValue(
                stage,
                out SampleTextureHotReloadCapture? completed))
        {
            return completed;
        }
        if (!_capturePaths.TryGetValue(stage, out string? path))
        {
            return new SampleTextureHotReloadCapture(
                stage,
                LinearHdrCaptureState.Unknown,
                string.Empty,
                0,
                0,
                null,
                string.Empty,
                "Capture was not queued.");
        }

        LinearHdrCaptureResult result =
            _renderer.GetLinearHdrCaptureResult(path);
        if (result.State != LinearHdrCaptureState.Completed)
        {
            return new SampleTextureHotReloadCapture(
                stage,
                result.State,
                result.OutputPath,
                0,
                0,
                null,
                string.Empty,
                result.Error);
        }

        SampleEvidenceFileContent capture = SampleEvidenceFileIo.Read(
            path,
            MaximumCaptureBytes,
            "Texture hot-reload HDR capture");
        LinearFloatImage image = PfmLinearImageCodec.Decode(capture.Bytes);
        foreach (float component in image.Pixels)
        {
            if (!float.IsFinite(component))
            {
                throw new InvalidDataException(
                    $"HDR capture '{path}' contains a non-finite component.");
            }
        }

        completed = new SampleTextureHotReloadCapture(
            stage,
            result.State,
            result.OutputPath,
            image.Width,
            image.Height,
            image.Pixels,
            capture.Sha256,
            string.Empty);
        _completedCaptures.Add(stage, completed);
        return completed;
    }

    public void Restore()
    {
        if (_restored)
            return;
        _restored = true;

        List<Exception>? failures = null;
        for (int index = _sceneBindings.Count - 1; index >= 0; index--)
        {
            SceneMaterialBinding binding = _sceneBindings[index];
            try
            {
                binding.RenderObject.Material = binding.OriginalMaterial;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            _settingsSnapshot.Restore(_renderer.Settings);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "Texture hot-reload qualification rollback was incomplete.",
                failures);
        }
    }

    private TextureContentReloadResult Reload() =>
        _textureManager.ReloadTextureContent(
            _texture,
            CreateSource(),
            generateMipmaps: false,
            srgb: true,
            semantic: TextureSemantic.Color);

    private ModelTextureSource CreateSource() => new()
    {
        DebugName = "Authenticated rendered texture hot-reload KTX2",
        SourceKind = TextureSourceKind.ExternalFile,
        FilePath = _ktx2Path,
        MimeType = "image/ktx2",
        CacheIdentity = "cooked:" + SourceIdentity,
        ContainerKind = TextureContainerKind.Ktx2,
        EncodedByteLength = _encodedKtx2ByteLength > 0
            ? _encodedKtx2ByteLength
            : throw new InvalidOperationException(
                "The authenticated hot-reload KTX2 has not been published.")
    };

    private void PublishCookedTexture(byte[] rgba)
    {
        byte[] ktx2 = CreateKtx2(rgba);
        SampleEvidenceFileContent published =
            SampleEvidenceFileIo.WriteAtomic(
                _ktx2Path,
                ktx2,
                MaximumCookedTextureBytes,
                "Authenticated texture hot-reload KTX2");
        _encodedKtx2ByteLength = published.Bytes.Length;
        ulong sourceHash = CookedHash.Bytes(rgba);
        TextureTransportStatistics statistics =
            TextureTransportImage.FromRgba8(
                rgba,
                Width,
                Height,
                TextureColorSpace.Srgb,
                TextureSemantic.Color,
                sourceHash,
                "Smoke.AuthoredRgba8/v1").Statistics;
        statistics.EnsureValid(SourceIdentity);
        var metadata = new CookedTextureMeta(
            CookedPackage.StableAssetId(SourceIdentity),
            SourceIdentity,
            sourceHash,
            Path.GetFileName(_ktx2Path),
            TextureColorSpace.Srgb,
            TextureSamplerDescription.Default,
            Width,
            Height,
            Width,
            Height,
            MipCount: 1,
            VulkanFormat: Rgba8Srgb,
            EncodedBytes: ktx2.Length)
        {
            Ktx2ContentHash = CookedHash.Bytes(ktx2),
            Semantic = TextureSemantic.Color,
            TransportStatistics = statistics,
            AlphaCoveragePreserved = false,
            AlphaCoverageCutoff = null
        };
        CookedPackage.WriteTextureMeta(
            Path.ChangeExtension(_ktx2Path, ".njtex"),
            metadata);
    }

    private static byte[] CreateKtx2(byte[] rgba)
    {
        if (rgba.Length != Width * Height * 4)
            throw new ArgumentException("Smoke RGBA payload has invalid dimensions.", nameof(rgba));

        const int payloadOffset = 104;
        var result = new byte[payloadOffset + Width * Height * 4];
        Ktx2Identifier.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), Rgba8Srgb);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), Width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), Height);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(80, 8), payloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(88, 8), (ulong)rgba.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(96, 8), (ulong)rgba.Length);
        rgba.CopyTo(result, payloadOffset);
        return result;
    }

    private static byte[] CreateUniformRgba(
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var pixels = new byte[Width * Height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = alpha;
        }

        return pixels;
    }

    private static void ConfigureEvidenceSettings(RenderSettings settings)
    {
        settings.Debug.Enabled = true;
        settings.Debug.AllowScreenshots = true;
        settings.Materials.DebugView = MaterialDebugView.BaseColor;
        settings.Animation.Enabled = false;
        settings.Particles.Enabled = false;
        settings.AntiAliasing.Mode = AntiAliasingMode.None;
        settings.AntiAliasing.JitterEnabled = false;
        settings.DynamicResolution.Enabled = false;
    }

    private sealed record SceneMaterialBinding(
        RenderObject RenderObject,
        object? OriginalMaterial);
}

internal sealed class SampleTextureHotReloadSmokeRunner
{
    private const int MaximumCaptureWaitFrames = 120;

    private readonly ISampleTextureHotReloadSession _session;
    private readonly Func<string> _getDeviceIdentity;
    private readonly Action<SampleSmokeOperationResult> _record;
    private readonly Action _exit;
    private readonly string _initialDeviceIdentity;
    private readonly uint _initialTextureRevision;
    private readonly uint _initialMaterialProfileRevision;
    private readonly int _initialBindlessDescriptorCount;
    private readonly ulong _initialSourceHash;
    private readonly Vector3 _initialMeanDiffuse;

    private RunnerStage _stage;
    private int _captureWaitFrames;
    private TextureContentReloadResult _replacementResult;
    private TextureContentReloadResult _rollbackResult;
    private uint _replacementMaterialProfileRevision;
    private SampleTextureHotReloadCapture? _initialCapture;
    private SampleTextureHotReloadCapture? _replacementCapture;
    private bool _completed;

    public SampleTextureHotReloadSmokeRunner(
        ISampleTextureHotReloadSession session,
        Func<string> getDeviceIdentity,
        Action<SampleSmokeOperationResult> record,
        Action exit)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getDeviceIdentity =
            getDeviceIdentity ?? throw new ArgumentNullException(nameof(getDeviceIdentity));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _initialDeviceIdentity = _getDeviceIdentity();
        _initialTextureRevision = _session.TextureRevision;
        _initialMaterialProfileRevision = _session.MaterialProfileRevision;
        _initialBindlessDescriptorCount = _session.BindlessDescriptorCount;
        _initialSourceHash = _session.SourceContentHash;
        _initialMeanDiffuse = _session.MeanDiffuseReflectance;
    }

    public bool Completed => _completed;
    public string? Failure { get; private set; }

    public void OnFrameRendered(int frameIndex)
    {
        if (_completed)
            return;

        try
        {
            switch (_stage)
            {
                case RunnerStage.QueueInitial:
                    if (_session.RenderedGeometryBindingCount <= 0)
                    {
                        throw new InvalidOperationException(
                            "No live rendered mesh geometry accepted the qualification material.");
                    }
                    QueueCapture(SampleTextureHotReloadCaptureStage.Initial);
                    _stage = RunnerStage.AwaitInitial;
                    break;
                case RunnerStage.AwaitInitial:
                    if (!TryGetCompletedCapture(
                            SampleTextureHotReloadCaptureStage.Initial,
                            out _initialCapture))
                    {
                        break;
                    }
                    _replacementResult = _session.ReloadReplacement();
                    _stage = RunnerStage.PresentReplacement;
                    break;
                case RunnerStage.PresentReplacement:
                    QueueCapture(SampleTextureHotReloadCaptureStage.Replacement);
                    _stage = RunnerStage.AwaitReplacement;
                    break;
                case RunnerStage.AwaitReplacement:
                    if (!TryGetCompletedCapture(
                            SampleTextureHotReloadCaptureStage.Replacement,
                            out _replacementCapture))
                    {
                        break;
                    }
                    ValidateReplacement();
                    _replacementMaterialProfileRevision =
                        _session.MaterialProfileRevision;
                    _rollbackResult = _session.ReloadOriginal();
                    _stage = RunnerStage.PresentRollback;
                    break;
                case RunnerStage.PresentRollback:
                    QueueCapture(SampleTextureHotReloadCaptureStage.Rollback);
                    _stage = RunnerStage.AwaitRollback;
                    break;
                case RunnerStage.AwaitRollback:
                    if (!TryGetCompletedCapture(
                            SampleTextureHotReloadCaptureStage.Rollback,
                            out SampleTextureHotReloadCapture? rollbackCapture))
                    {
                        break;
                    }
                    SampleTextureHotReloadCapture completedRollback =
                        rollbackCapture ??
                        throw new InvalidOperationException(
                            "Completed rollback capture was not published.");
                    ValidateRollback(completedRollback);
                    Complete(frameIndex, completedRollback);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported hot-reload qualification stage '{_stage}'.");
            }
        }
        catch (Exception exception)
        {
            Fail(frameIndex, exception.Message);
        }
    }

    private void QueueCapture(SampleTextureHotReloadCaptureStage stage)
    {
        if (!_session.QueueCapture(stage))
        {
            throw new InvalidOperationException(
                $"Renderer rejected the required {stage} linear HDR capture.");
        }

        _captureWaitFrames = 0;
    }

    private bool TryGetCompletedCapture(
        SampleTextureHotReloadCaptureStage stage,
        out SampleTextureHotReloadCapture? capture)
    {
        capture = _session.GetCapture(stage);
        if (capture.State == LinearHdrCaptureState.Completed)
        {
            ValidateCapture(capture);
            _captureWaitFrames = 0;
            return true;
        }
        if (capture.State == LinearHdrCaptureState.Failed)
        {
            throw new InvalidOperationException(
                $"{stage} HDR capture failed: {capture.Error}");
        }
        if (capture.State == LinearHdrCaptureState.Unknown)
        {
            throw new InvalidOperationException(
                $"{stage} HDR capture request disappeared before completion.");
        }

        _captureWaitFrames++;
        if (_captureWaitFrames > MaximumCaptureWaitFrames)
        {
            throw new TimeoutException(
                $"{stage} HDR capture did not complete within " +
                $"{MaximumCaptureWaitFrames} rendered frames.");
        }

        capture = null;
        return false;
    }

    private void ValidateReplacement()
    {
        if (!_replacementResult.Changed)
            throw new InvalidOperationException("Replacement KTX2 was not published.");
        if (_replacementResult.NotifiedAliasCount < 1)
            throw new InvalidOperationException("Replacement notified no live descriptor alias.");
        if (_session.TextureRevision <= _initialTextureRevision)
            throw new InvalidOperationException("Texture revision did not advance.");
        if (_session.MaterialProfileRevision <= _initialMaterialProfileRevision)
            throw new InvalidOperationException("Material profile did not recompile.");
        if (_session.SourceContentHash == 0 ||
            _session.SourceContentHash == _initialSourceHash)
        {
            throw new InvalidOperationException("Replacement source identity did not change.");
        }
        if (_session.BindlessDescriptorCount != _initialBindlessDescriptorCount)
            throw new InvalidOperationException("Bindless descriptor occupancy changed.");
        if (ApproximatelyEqual(
                _session.MeanDiffuseReflectance,
                _initialMeanDiffuse,
                1e-6f))
        {
            throw new InvalidOperationException(
                "Replacement did not update compact material transport.");
        }

        PixelDifference difference = Compare(
            _initialCapture!,
            _replacementCapture!);
        if (difference.ChangedPixelCount < 4 ||
            difference.MaximumAbsoluteDifference < 0.05f ||
            difference.MeanAbsoluteDifference < 1e-7)
        {
            throw new InvalidOperationException(
                "Rendered replacement evidence did not contain a material-visible " +
                $"change (pixels={difference.ChangedPixelCount}, " +
                $"mean={difference.MeanAbsoluteDifference:R}, " +
                $"max={difference.MaximumAbsoluteDifference:R}).");
        }
    }

    private void ValidateRollback(SampleTextureHotReloadCapture rollback)
    {
        if (!_rollbackResult.Changed ||
            _rollbackResult.NotifiedAliasCount < 1)
        {
            throw new InvalidOperationException(
                "Rollback KTX2 was not published to a live descriptor alias.");
        }
        if (_session.TextureRevision <= _replacementResult.ContentRevision)
            throw new InvalidOperationException("Rollback texture revision did not advance.");
        if (_session.MaterialProfileRevision <= _replacementMaterialProfileRevision)
            throw new InvalidOperationException("Rollback material profile did not recompile.");
        if (_session.SourceContentHash != _initialSourceHash)
            throw new InvalidOperationException("Rollback source hash was not restored.");
        if (_session.BindlessDescriptorCount != _initialBindlessDescriptorCount)
            throw new InvalidOperationException("Rollback changed descriptor occupancy.");
        if (!ApproximatelyEqual(
                _session.MeanDiffuseReflectance,
                _initialMeanDiffuse,
                1e-5f))
        {
            throw new InvalidOperationException(
                "Rollback did not restore compact material transport.");
        }
        if (!string.Equals(
                _getDeviceIdentity(),
                _initialDeviceIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Renderer device changed during in-process rollback.");
        }

        PixelDifference replacement = Compare(
            _initialCapture!,
            _replacementCapture!);
        PixelDifference restored = Compare(_initialCapture!, rollback);
        if (restored.MeanAbsoluteDifference >
                Math.Max(2e-5, replacement.MeanAbsoluteDifference * 0.02) ||
            restored.MaximumAbsoluteDifference >
                Math.Max(0.005f, replacement.MaximumAbsoluteDifference * 0.05f))
        {
            throw new InvalidOperationException(
                "Rendered rollback did not restore the initial BaseColor signal " +
                $"(mean={restored.MeanAbsoluteDifference:R}, " +
                $"max={restored.MaximumAbsoluteDifference:R}).");
        }
    }

    private void Complete(
        int frameIndex,
        SampleTextureHotReloadCapture rollbackCapture)
    {
        _session.Restore();
        _completed = true;
        _record(new SampleSmokeOperationResult(
            "texture-hot-reload",
            "passed",
            frameIndex,
            $"container=KTX2, renderedGeometry={_session.RenderedGeometryBindingCount}, " +
            $"textureRevision={_initialTextureRevision}->{_session.TextureRevision}, " +
            $"profileRevision={_initialMaterialProfileRevision}->{_session.MaterialProfileRevision}, " +
            $"descriptorCount={_initialBindlessDescriptorCount}, " +
            $"captures={_initialCapture!.Sha256[..12]}:" +
            $"{_replacementCapture!.Sha256[..12]}:" +
            $"{rollbackCapture.Sha256[..12]}, rollback=true, rendererRestarted=false"));
        _record(new SampleSmokeOperationResult(
            "device-loss-recovery",
            "rejected-unsupported",
            frameIndex,
            "No safe deterministic device-loss injection is exposed; unsafe driver/device fault injection was not attempted."));
        _exit();
    }

    private void Fail(int frameIndex, string failure)
    {
        try
        {
            _session.Restore();
        }
        catch (Exception restoreFailure)
        {
            failure += $" Rollback also failed: {restoreFailure.Message}";
        }

        Failure = failure;
        _completed = true;
        _record(new SampleSmokeOperationResult(
            "texture-hot-reload",
            "failed",
            frameIndex,
            failure));
        _exit();
    }

    private static void ValidateCapture(SampleTextureHotReloadCapture capture)
    {
        if (capture.Width <= 0 ||
            capture.Height <= 0 ||
            capture.Pixels is not { Length: > 0 } pixels ||
            pixels.Length != checked(capture.Width * capture.Height * 3))
        {
            throw new InvalidDataException(
                $"{capture.Stage} HDR capture has invalid dimensions or RGB payload.");
        }
        if (capture.Sha256.Length != 64)
        {
            throw new InvalidDataException(
                $"{capture.Stage} HDR capture has no SHA-256 identity.");
        }
        foreach (float component in pixels)
        {
            if (!float.IsFinite(component))
            {
                throw new InvalidDataException(
                    $"{capture.Stage} HDR capture contains a non-finite component.");
            }
        }
    }

    private static PixelDifference Compare(
        SampleTextureHotReloadCapture left,
        SampleTextureHotReloadCapture right)
    {
        if (left.Width != right.Width ||
            left.Height != right.Height ||
            left.Pixels!.Length != right.Pixels!.Length)
        {
            throw new InvalidDataException(
                $"HDR capture dimensions differ: {left.Width}x{left.Height} and " +
                $"{right.Width}x{right.Height}.");
        }

        double total = 0;
        float maximum = 0;
        int changedPixels = 0;
        for (int offset = 0; offset < left.Pixels.Length; offset += 3)
        {
            float red = MathF.Abs(left.Pixels[offset] - right.Pixels[offset]);
            float green =
                MathF.Abs(left.Pixels[offset + 1] - right.Pixels[offset + 1]);
            float blue =
                MathF.Abs(left.Pixels[offset + 2] - right.Pixels[offset + 2]);
            float pixelMaximum = Math.Max(red, Math.Max(green, blue));
            if (pixelMaximum > 0.002f)
                changedPixels++;
            maximum = Math.Max(maximum, pixelMaximum);
            total += red + green + blue;
        }

        return new PixelDifference(
            total / left.Pixels.Length,
            maximum,
            changedPixels);
    }

    private static bool ApproximatelyEqual(
        Vector3 left,
        Vector3 right,
        float epsilon) =>
        MathF.Abs(left.X - right.X) <= epsilon &&
        MathF.Abs(left.Y - right.Y) <= epsilon &&
        MathF.Abs(left.Z - right.Z) <= epsilon;

    private enum RunnerStage : byte
    {
        QueueInitial = 0,
        AwaitInitial = 1,
        PresentReplacement = 2,
        AwaitReplacement = 3,
        PresentRollback = 4,
        AwaitRollback = 5
    }

    private readonly record struct PixelDifference(
        double MeanAbsoluteDifference,
        float MaximumAbsoluteDifference,
        int ChangedPixelCount);
}
