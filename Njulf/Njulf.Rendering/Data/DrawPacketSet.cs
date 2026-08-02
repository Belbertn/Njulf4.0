using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Immutable snapshot of the shadow draw-command topology produced by a scene payload rebuild.
    /// Signatures are computed once when the snapshot is published and reused until its revision changes.
    /// </summary>
    internal sealed class DrawPacketSet
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        private readonly GPUMeshletDrawCommand[] _directionalShadowCommands;
        private readonly GPUMeshletDrawCommand[] _localShadowCommands;
        private readonly GPUMeshletDrawCommand[] _directionalStaticShadowCommands;
        private readonly GPUMeshletDrawCommand[] _directionalDynamicShadowCommands;
        private readonly GPUMeshletDrawCommand[] _localStaticShadowCommands;
        private readonly GPUMeshletDrawCommand[] _localDynamicShadowCommands;

        public static DrawPacketSet Empty { get; } = new(
            revision: 0,
            Array.Empty<GPUMeshletDrawCommand>(),
            Array.Empty<GPUMeshletDrawCommand>(),
            Array.Empty<GPUMeshletDrawCommand>(),
            Array.Empty<GPUMeshletDrawCommand>(),
            Array.Empty<GPUMeshletDrawCommand>(),
            Array.Empty<GPUMeshletDrawCommand>());

        private DrawPacketSet(
            ulong revision,
            IReadOnlyList<GPUMeshletDrawCommand> directionalShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> directionalStaticShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> directionalDynamicShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localStaticShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localDynamicShadowCommands)
        {
            Revision = revision;
            _directionalShadowCommands = Copy(directionalShadowCommands);
            _localShadowCommands = Copy(localShadowCommands);
            _directionalStaticShadowCommands = Copy(directionalStaticShadowCommands);
            _directionalDynamicShadowCommands = Copy(directionalDynamicShadowCommands);
            _localStaticShadowCommands = Copy(localStaticShadowCommands);
            _localDynamicShadowCommands = Copy(localDynamicShadowCommands);

            DirectionalShadowSignature = ComputeSignature(_directionalShadowCommands);
            LocalShadowSignature = ComputeSignature(_localShadowCommands);
            DirectionalStaticShadowSignature = ComputeSignature(_directionalStaticShadowCommands);
            DirectionalDynamicShadowSignature = ComputeSignature(_directionalDynamicShadowCommands);
            LocalStaticShadowSignature = ComputeSignature(_localStaticShadowCommands);
            LocalDynamicShadowSignature = ComputeSignature(_localDynamicShadowCommands);
        }

        public ulong Revision { get; }

        public ReadOnlyMemory<GPUMeshletDrawCommand> DirectionalShadowCommands => _directionalShadowCommands;
        public ReadOnlyMemory<GPUMeshletDrawCommand> LocalShadowCommands => _localShadowCommands;
        public ReadOnlyMemory<GPUMeshletDrawCommand> DirectionalStaticShadowCommands => _directionalStaticShadowCommands;
        public ReadOnlyMemory<GPUMeshletDrawCommand> DirectionalDynamicShadowCommands => _directionalDynamicShadowCommands;
        public ReadOnlyMemory<GPUMeshletDrawCommand> LocalStaticShadowCommands => _localStaticShadowCommands;
        public ReadOnlyMemory<GPUMeshletDrawCommand> LocalDynamicShadowCommands => _localDynamicShadowCommands;

        public ulong DirectionalShadowSignature { get; }
        public ulong LocalShadowSignature { get; }
        public ulong DirectionalStaticShadowSignature { get; }
        public ulong DirectionalDynamicShadowSignature { get; }
        public ulong LocalStaticShadowSignature { get; }
        public ulong LocalDynamicShadowSignature { get; }

        public static DrawPacketSet Create(
            ulong revision,
            IReadOnlyList<GPUMeshletDrawCommand> directionalShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> directionalStaticShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> directionalDynamicShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localStaticShadowCommands,
            IReadOnlyList<GPUMeshletDrawCommand> localDynamicShadowCommands)
        {
            if (revision == 0)
                throw new ArgumentOutOfRangeException(nameof(revision), "A published draw-packet revision must be non-zero.");

            return new DrawPacketSet(
                revision,
                directionalShadowCommands,
                localShadowCommands,
                directionalStaticShadowCommands,
                directionalDynamicShadowCommands,
                localStaticShadowCommands,
                localDynamicShadowCommands);
        }

        private static GPUMeshletDrawCommand[] Copy(IReadOnlyList<GPUMeshletDrawCommand> commands)
        {
            ArgumentNullException.ThrowIfNull(commands);
            if (commands.Count == 0)
                return Array.Empty<GPUMeshletDrawCommand>();

            var copy = new GPUMeshletDrawCommand[commands.Count];
            for (int i = 0; i < commands.Count; i++)
                copy[i] = commands[i];
            return copy;
        }

        private static ulong ComputeSignature(ReadOnlySpan<GPUMeshletDrawCommand> commands)
        {
            ulong hash = OffsetBasis;
            hash = (hash ^ (uint)commands.Length) * Prime;
            for (int i = 0; i < commands.Length; i++)
            {
                ref readonly GPUMeshletDrawCommand command = ref commands[i];
                hash = (hash ^ command.MeshletIndex) * Prime;
                hash = (hash ^ command.InstanceId) * Prime;
                hash = (hash ^ command.MaterialIndex) * Prime;
            }

            return hash;
        }
    }
}
