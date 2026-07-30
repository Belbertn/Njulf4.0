using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Rejects ambiguous JSON before it reaches a typed deserializer. System.Text.Json
/// otherwise accepts repeated names and lets a later value replace an earlier
/// one, which is unsuitable for pinned release evidence.
/// </summary>
internal static class StrictJsonContract
{
    public static void RejectDuplicateProperties(
        ReadOnlySpan<byte> utf8Json,
        int maximumDepth,
        string role)
    {
        if (utf8Json.IsEmpty)
            throw new InvalidDataException($"{role} JSON is empty.");
        if (maximumDepth <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                "The JSON depth bound must be positive.");
        if (string.IsNullOrWhiteSpace(role) || role.Length > 512)
            throw new ArgumentException("A bounded JSON role is required.", nameof(role));

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth
            });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.PropertyName:
                    if (!containers.TryPeek(out HashSet<string>? names) ||
                        names is null)
                    {
                        throw new InvalidDataException(
                            $"{role} contains a JSON property outside an object.");
                    }

                    string name = reader.GetString() ??
                        throw new InvalidDataException(
                            $"{role} contains a null JSON property name.");
                    if (!names.Add(name))
                    {
                        throw new InvalidDataException(
                            $"{role} contains a duplicate JSON property.");
                    }
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"{role} contains unbalanced JSON containers.");
                    }
                    containers.Pop();
                    break;
            }
        }

        if (containers.Count != 0)
        {
            throw new InvalidDataException(
                $"{role} contains unbalanced JSON containers.");
        }
    }
}
