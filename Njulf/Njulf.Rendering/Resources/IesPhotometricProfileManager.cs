using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Njulf.Assets;
using Njulf.Assets.Scenes;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Lazily parses, resamples, uploads, and caches optional LM-63 Type-C profiles.
/// Resolution failures deliberately leave lights on their unit photometric response.
/// </summary>
public sealed class IesPhotometricProfileManager : IPhotometricProfileResolver, IDisposable
{
    public const uint TextureWidth = 256;
    public const uint TextureHeight = 128;
    public const int MaximumProfileCount = 128;

    private readonly TextureManager _textureManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entriesByKey =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Entry> _entriesById = new();
    private int _nextId = 1;
    private uint _nextRevision = 1;
    private bool _disposed;

    public IesPhotometricProfileManager(
        TextureManager textureManager,
        BindlessHeap bindlessHeap)
    {
        _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
    }

    public int ProfileCount { get; private set; }
    public ulong EstimatedBytes =>
        checked((ulong)ProfileCount * TextureWidth * TextureHeight * sizeof(ushort));
    public ulong LoadSuccessCount { get; private set; }
    public ulong LoadFailureCount { get; private set; }
    public string? LastFailure { get; private set; }

    public bool TryResolve(
        SceneAssetReferenceDocument source,
        out PhotometricProfileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ThrowIfDisposed();
            handle = default;
            try
            {
                if (string.IsNullOrWhiteSpace(source.Path))
                    throw new InvalidDataException("IES asset path is empty.");
                if (!string.IsNullOrWhiteSpace(source.SubObject) && source.SubObject != "*")
                    throw new NotSupportedException("IES assets do not expose sub-objects.");

                string path = Path.GetFullPath(source.Path);
                var info = new FileInfo(path);
                if (!info.Exists)
                    throw new FileNotFoundException("IES profile was not found.", path);
                if (info.Length <= 0 || info.Length > IesPhotometricProfileParser.MaximumTextLength)
                    throw new InvalidDataException("IES profile is empty or exceeds the 4 MiB safety limit.");
                string key = FormattableString.Invariant(
                    $"{path}|{source.ContentHash}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                if (_entriesByKey.TryGetValue(key, out Entry? cached))
                {
                    handle = cached.Handle;
                    return true;
                }
                if (ProfileCount >= MaximumProfileCount)
                    throw new InvalidOperationException($"The IES cache limit of {MaximumProfileCount} profiles was reached.");

                byte[] bytes = File.ReadAllBytes(path);
                ValidateContentHash(source.ContentHash, bytes);
                IesPhotometricProfile profile =
                    IesPhotometricProfileParser.Parse(Encoding.Latin1.GetString(bytes));
                float[] resampled = profile.Resample((int)TextureWidth, (int)TextureHeight);
                var half = new Half[resampled.Length];
                for (int i = 0; i < resampled.Length; i++)
                    half[i] = (Half)resampled[i];
                byte[] payload = MemoryMarshal.AsBytes(half.AsSpan()).ToArray();
                var sampler = new TextureSamplerDescription(
                    TextureWrapMode.Repeat,
                    TextureWrapMode.ClampToEdge,
                    TextureFilterMode.Linear,
                    TextureFilterMode.Linear,
                    TextureMipFilterMode.Nearest,
                    1f);
                TextureHandle texture = TextureHandle.Invalid;
                try
                {
                    texture = _textureManager.CreateTexture(
                        TextureWidth,
                        TextureHeight,
                        Format.R16Sfloat,
                        bindlessHeap: _bindlessHeap,
                        samplerDescription: sampler,
                        requireWithinMemoryBudget: true,
                        debugName: $"IES Profile: {Path.GetFileName(path)}");
                    _textureManager.UploadTextureData(
                        texture,
                        payload,
                        TextureWidth,
                        TextureHeight,
                        Format.R16Sfloat);
                    int textureIndex = _textureManager.GetBindlessTextureIndex(texture);
                    if (textureIndex < BindlessIndex.FirstDynamicTextureIndex)
                        throw new InvalidOperationException("IES texture did not receive a dynamic bindless index.");
                    int id = _nextId++;
                    uint revision = _nextRevision++;
                    if (_nextRevision == 0)
                        _nextRevision = 1;
                    handle = new PhotometricProfileHandle(id, textureIndex, revision);
                    var entry = new Entry(handle, texture, source, key, profile.PeakCandela);
                    _entriesByKey.Add(key, entry);
                    _entriesById.Add(id, entry);
                    ProfileCount++;
                    LoadSuccessCount++;
                    LastFailure = null;
                    return true;
                }
                catch
                {
                    if (texture.IsValid)
                        _textureManager.DestroyTexture(texture);
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or NotSupportedException or ArgumentException or
                InvalidOperationException or OverflowException or VulkanException)
            {
                LoadFailureCount++;
                LastFailure = ex.Message;
                System.Diagnostics.Debug.WriteLine(
                    $"IES profile load failed for '{source.Path}': {ex.Message}");
                return false;
            }
        }
    }

    public bool TryGetReference(
        PhotometricProfileHandle handle,
        out SceneAssetReferenceDocument? source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (handle.IsValid &&
                _entriesById.TryGetValue(handle.Value, out Entry? entry) &&
                entry.Handle == handle)
            {
                source = entry.Source;
                return true;
            }
            source = null;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Entry entry in _entriesById.Values)
            {
                if (entry.Texture.IsValid)
                    _textureManager.DestroyTexture(entry.Texture);
            }
            _entriesById.Clear();
            _entriesByKey.Clear();
            ProfileCount = 0;
        }
    }

    private static void ValidateContentHash(string? expected, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return;
        string normalized = expected.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..];
        // Scene references historically permit application-defined hashes. Enforce the
        // portable SHA-256 form when present and preserve other schemes as cache identity.
        if (!IsSha256Hex(normalized))
            return;
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("IES profile SHA-256 does not match the scene reference.");
    }

    private static bool IsSha256Hex(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
            return false;
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record Entry(
        PhotometricProfileHandle Handle,
        TextureHandle Texture,
        SceneAssetReferenceDocument Source,
        string CacheKey,
        float PeakCandela);
}
