using System;
using System.IO;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Reads a bounded artifact through one non-writable shared handle. Admission,
/// the exact read, and the final length check therefore all describe the same
/// opened file rather than separate path lookups.
/// </summary>
internal static class BoundedFileReader
{
    public static byte[] ReadStable(
        string path,
        int maximumBytes,
        string role,
        long? expectedLength = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An artifact path is required.", nameof(path));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                maximumBytes,
                "The artifact byte bound must be positive.");
        if (string.IsNullOrWhiteSpace(role) || role.Length > 512)
            throw new ArgumentException(
                "A bounded artifact role is required.",
                nameof(role));
        if (expectedLength is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedLength),
                expectedLength,
                "An expected artifact length must be positive when supplied.");

        string fullPath = Path.GetFullPath(path);
        using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength <= 0 ||
            admittedLength > maximumBytes ||
            (expectedLength.HasValue &&
             admittedLength != expectedLength.GetValueOrDefault()))
        {
            throw new InvalidDataException(
                $"{role} '{fullPath}' has an invalid bounded length.");
        }

        var bytes = new byte[checked((int)admittedLength)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new IOException(
                $"{role} '{fullPath}' changed length while it was being read.");
        }

        return bytes;
    }
}
