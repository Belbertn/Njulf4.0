using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>Explicitly places model-space light definitions in a live scene.</summary>
public static class ModelLightInstantiator
{
    public static ModelLightInstanceSet Instantiate(
        Model model,
        IMutableSceneLightStore store,
        Matrix4x4 worldTransform,
        Guid? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        Guid resolvedId = instanceId ?? Guid.NewGuid();
        if (resolvedId == Guid.Empty)
            throw new ArgumentException("A model-light instance ID cannot be empty.", nameof(instanceId));

        return ModelLightInstanceSet.Create(
            model.Lights,
            store,
            worldTransform,
            resolvedId);
    }
}

public sealed class ModelLightInstanceSet : IDisposable
{
    private readonly ModelLightDefinition[] _definitions;
    private readonly IMutableSceneLightStore _store;
    private readonly Guid[] _ids;
    private SceneLightDocument[] _documents;
    private bool _disposed;

    private ModelLightInstanceSet(
        ModelLightDefinition[] definitions,
        IMutableSceneLightStore store,
        Guid instanceId,
        Guid[] ids,
        SceneLightDocument[] documents,
        Matrix4x4 worldTransform)
    {
        _definitions = definitions;
        _store = store;
        InstanceId = instanceId;
        _ids = ids;
        _documents = documents;
        WorldTransform = worldTransform;
    }

    public Guid InstanceId { get; }
    public IReadOnlyList<Guid> LightIds => _ids;
    public Matrix4x4 WorldTransform { get; private set; }

    internal static ModelLightInstanceSet Create(
        IReadOnlyList<ModelLightDefinition> definitions,
        IMutableSceneLightStore store,
        Matrix4x4 worldTransform,
        Guid instanceId)
    {
        ValidateTransform(worldTransform, out _);
        var copied = definitions.ToArray();
        var ids = new Guid[copied.Length];
        var documents = new SceneLightDocument[copied.Length];
        for (int index = 0; index < copied.Length; index++)
        {
            ids[index] = CreateStableId(instanceId, copied[index], index);
            documents[index] = CreateDocument(
                copied[index],
                ids[index],
                worldTransform);
        }

        int added = 0;
        try
        {
            for (; added < documents.Length; added++)
                store.Add(ids[added], documents[added]);
        }
        catch (Exception addFailure)
        {
            List<Exception>? rollbackFailures = null;
            for (int index = added - 1; index >= 0; index--)
            {
                try
                {
                    if (!store.TryRemove(ids[index]))
                    {
                        (rollbackFailures ??= []).Add(new InvalidOperationException(
                            $"Could not roll back imported light '{ids[index]}'."));
                    }
                }
                catch (Exception failure)
                {
                    (rollbackFailures ??= []).Add(failure);
                }
            }

            if (rollbackFailures is { Count: > 0 })
            {
                rollbackFailures.Insert(0, addFailure);
                throw new AggregateException(
                    "Model-light placement and rollback both failed.",
                    rollbackFailures);
            }
            throw;
        }

        return new ModelLightInstanceSet(
            copied,
            store,
            instanceId,
            ids,
            documents,
            worldTransform);
    }

    public void UpdateTransform(Matrix4x4 worldTransform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTransform(worldTransform, out _);
        var replacements = new SceneLightDocument[_definitions.Length];
        for (int index = 0; index < replacements.Length; index++)
        {
            replacements[index] = CreateDocument(
                _definitions[index],
                _ids[index],
                worldTransform);
        }

        int updated = 0;
        try
        {
            for (; updated < replacements.Length; updated++)
            {
                if (!_store.TryUpdate(_ids[updated], replacements[updated]))
                {
                    throw new InvalidOperationException(
                        $"Scene light '{_ids[updated]}' no longer exists.");
                }
            }
        }
        catch (Exception updateFailure)
        {
            List<Exception>? rollbackFailures = null;
            for (int index = updated - 1; index >= 0; index--)
            {
                try
                {
                    if (!_store.TryUpdate(_ids[index], _documents[index]))
                    {
                        (rollbackFailures ??= []).Add(new InvalidOperationException(
                            $"Could not roll back imported light '{_ids[index]}'."));
                    }
                }
                catch (Exception failure)
                {
                    (rollbackFailures ??= []).Add(failure);
                }
            }

            if (rollbackFailures is { Count: > 0 })
            {
                rollbackFailures.Insert(0, updateFailure);
                throw new AggregateException(
                    "Model-light transform update and rollback both failed.",
                    rollbackFailures);
            }
            throw;
        }

        _documents = replacements;
        WorldTransform = worldTransform;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var failures = new List<Exception>();
        for (int index = _ids.Length - 1; index >= 0; index--)
        {
            try
            {
                if (!_store.TryRemove(_ids[index]))
                {
                    failures.Add(new InvalidOperationException(
                        $"Scene light '{_ids[index]}' no longer exists."));
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
        _disposed = true;
        if (failures.Count > 0)
            throw new AggregateException("One or more imported lights could not be removed.", failures);
    }

    private static SceneLightDocument CreateDocument(
        ModelLightDefinition source,
        Guid id,
        Matrix4x4 worldTransform)
    {
        ValidateTransform(worldTransform, out float maximumScale);
        Vector3 position = source.Position * worldTransform;
        Vector3 direction = TransformScaleFreeDirection(
            source.Direction,
            worldTransform);
        return new SceneLightDocument
        {
            Id = id,
            Name = source.Name,
            Type = source.Type.ToString(),
            Position = new SceneVector3(position.X, position.Y, position.Z),
            Direction = new SceneVector3(direction.X, direction.Y, direction.Z),
            Color = new SceneVector3(source.Color.X, source.Color.Y, source.Color.Z),
            Intensity = source.Intensity,
            Range = source.Range * maximumScale,
            SpotAngle = source.OuterConeAngle,
            InnerSpotAngle = source.InnerConeAngle,
            AttenuationMode = source.AttenuationMode.ToString(),
            AttenuationConstant = source.AttenuationConstant,
            AttenuationLinear = source.AttenuationLinear / maximumScale,
            AttenuationQuadratic = source.AttenuationQuadratic /
                (maximumScale * maximumScale),
            CastsShadows = false,
            ShadowStrength = 1f,
            ShadowNearPlane = 0.1f,
            ShadowFarPlane = source.Range * maximumScale
        };
    }

    private static Vector3 TransformScaleFreeDirection(
        Vector3 source,
        Matrix4x4 transform)
    {
        float row0 = MathF.Sqrt(transform.M11 * transform.M11 +
            transform.M12 * transform.M12 + transform.M13 * transform.M13);
        float row1 = MathF.Sqrt(transform.M21 * transform.M21 +
            transform.M22 * transform.M22 + transform.M23 * transform.M23);
        float row2 = MathF.Sqrt(transform.M31 * transform.M31 +
            transform.M32 * transform.M32 + transform.M33 * transform.M33);
        var result = new Vector3(
            source.X * transform.M11 / row0 +
                source.Y * transform.M21 / row1 +
                source.Z * transform.M31 / row2,
            source.X * transform.M12 / row0 +
                source.Y * transform.M22 / row1 +
                source.Z * transform.M32 / row2,
            source.X * transform.M13 / row0 +
                source.Y * transform.M23 / row1 +
                source.Z * transform.M33 / row2);
        float length = result.Length();
        if (!float.IsFinite(length) || length <= float.Epsilon)
            throw new ArgumentException("The placement transform produces an invalid light direction.");
        return result / length;
    }

    private static void ValidateTransform(
        Matrix4x4 transform,
        out float maximumScale)
    {
        float[] values =
        [
            transform.M11, transform.M12, transform.M13, transform.M14,
            transform.M21, transform.M22, transform.M23, transform.M24,
            transform.M31, transform.M32, transform.M33, transform.M34,
            transform.M41, transform.M42, transform.M43, transform.M44
        ];
        if (values.Any(static value => !float.IsFinite(value)))
            throw new ArgumentException("The model-light placement transform must be finite.");

        Vector3 scale = transform.Scale;
        maximumScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        if (scale.X <= float.Epsilon || scale.Y <= float.Epsilon ||
            scale.Z <= float.Epsilon || !float.IsFinite(maximumScale))
        {
            throw new ArgumentException(
                "The model-light placement transform must have finite, non-zero axis scales.");
        }
    }

    private static Guid CreateStableId(
        Guid instanceId,
        ModelLightDefinition definition,
        int ordinal)
    {
        Span<byte> input = stackalloc byte[28];
        instanceId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], definition.SourceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(input[20..], definition.SourceNodeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(input[24..], ordinal);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[7] = (byte)((hash[7] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16]);
    }
}
