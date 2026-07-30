using System.Security.Cryptography;
using System.Text.Json;

namespace Njulf.Assets.Cooked;

public sealed record CookedDetachedSignature(string Algorithm, string Sha256, string Signature, string PublicKeyFingerprint);

public static class CookedPackageSigner
{
    public const string PublicKeyEnvironmentVariable = "NJULF_COOKED_ASSET_PUBLIC_KEY";
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const int MaximumDetachedSignatureBytes = 64 * 1024;
    public const int MaximumPemFileBytes = 1024 * 1024;

    public static string SignaturePath(string assetPath) => Path.GetFullPath(assetPath) + ".sig";

    public static void GenerateKeyPair(string privateKeyPath, string publicKeyPath)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteTextAtomic(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
        WriteTextAtomic(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
    }

    public static string SignFile(string assetPath, string privateKeyPathOrPem)
    {
        assetPath = Path.GetFullPath(assetPath);
        byte[] hash = HashFile(assetPath);
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(ReadPem(privateKeyPathOrPem));
        byte[] signature = key.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        var value = new CookedDetachedSignature(
            Algorithm,
            Convert.ToHexStringLower(hash),
            Convert.ToBase64String(signature),
            Convert.ToHexStringLower(SHA256.HashData(publicKey)));
        string path = SignaturePath(assetPath);
        WriteBytesAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, CookedJson.Options));
        return path;
    }

    public static void VerifyRequired(string assetPath, string? publicKeyPathOrPem = null)
    {
        assetPath = Path.GetFullPath(assetPath);
        VerifyRequiredHash(assetPath, HashFile(assetPath), publicKeyPathOrPem);
    }

    /// <summary>
    /// Verifies a detached signature against an exact caller-owned content
    /// snapshot. The path is used only to locate the signature and report
    /// diagnostics; the asset path is never reopened for hashing.
    /// </summary>
    public static void VerifyRequired(
        string assetPath,
        ReadOnlySpan<byte> assetContent,
        string? publicKeyPathOrPem = null)
    {
        assetPath = Path.GetFullPath(assetPath);
        VerifyRequiredHash(
            assetPath,
            SHA256.HashData(assetContent),
            publicKeyPathOrPem);
    }

    private static void VerifyRequiredHash(
        string assetPath,
        byte[] contentHash,
        string? publicKeyPathOrPem)
    {
        string signaturePath = SignaturePath(assetPath);
        if (!File.Exists(signaturePath))
            throw new CookedAssetHashException(assetPath, $"required detached signature '{Path.GetFileName(signaturePath)}' is missing");
        publicKeyPathOrPem ??= Environment.GetEnvironmentVariable(PublicKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(publicKeyPathOrPem))
            throw new CookedAssetHashException(assetPath, $"signature verification requires {PublicKeyEnvironmentVariable}");
        CookedDetachedSignature signature;
        try
        {
            byte[] signatureContent = ReadBoundedSignature(signaturePath);
            signature = JsonSerializer.Deserialize<CookedDetachedSignature>(
                    signatureContent,
                    CookedJson.Options)
                ?? throw new JsonException("Signature object is empty.");
        }
        catch (JsonException ex)
        {
            throw new CookedAssetHashException(assetPath, $"detached signature is malformed ({ex.Message})");
        }
        if (!string.Equals(signature.Algorithm, Algorithm, StringComparison.Ordinal))
            throw new CookedAssetHashException(assetPath, $"unsupported signature algorithm '{signature.Algorithm}'");
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    contentHash,
                    Convert.FromHexString(signature.Sha256)))
            {
                throw new CookedAssetHashException(assetPath, "detached signature content hash does not match the file");
            }
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(ReadPem(publicKeyPathOrPem));
            byte[] exported = key.ExportSubjectPublicKeyInfo();
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(exported));
            if (!string.Equals(fingerprint, signature.PublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new CookedAssetHashException(assetPath, "detached signature public-key fingerprint does not match");
            if (!key.VerifyHash(
                    contentHash,
                    Convert.FromBase64String(signature.Signature),
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new CookedAssetHashException(assetPath, "detached signature verification failed");
            }
        }
        catch (CookedAssetHashException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new CookedAssetHashException(assetPath, $"detached signature is invalid ({ex.Message})");
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static string ReadPem(string pathOrPem)
    {
        if (!File.Exists(pathOrPem))
            return pathOrPem;

        byte[] snapshot = AssetArtifactFileIo.ReadBoundedSnapshot(
            Path.GetFullPath(pathOrPem),
            MaximumPemFileBytes,
            "Cooked signing key");
        return System.Text.Encoding.UTF8.GetString(snapshot);
    }

    private static byte[] ReadBoundedSignature(string signaturePath)
    {
        try
        {
            using var stream = new FileStream(
                signaturePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumDetachedSignatureBytes)
            {
                throw new CookedAssetHashException(
                    signaturePath,
                    $"detached signature contains {stream.Length} bytes; the runtime " +
                    $"limit is {MaximumDetachedSignatureBytes} bytes");
            }

            var content = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length));
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
            {
                throw new CookedAssetHashException(
                    signaturePath,
                    "detached signature changed while its immutable snapshot was read");
            }

            return content;
        }
        catch (CookedAssetHashException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CookedAssetHashException(
                signaturePath,
                $"detached signature could not be read ({exception.Message})");
        }
    }

    private static void WriteTextAtomic(string path, string text) => WriteBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void WriteBytesAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
