using System;

namespace Njulf.Rendering.Memory
{
    public struct BufferHandle : IEquatable<BufferHandle>
    {
        public int Index { get; }
        public uint Generation { get; }
        
        public BufferHandle(int index, uint generation)
        {
            Index = index;
            Generation = generation;
        }
        
        public bool IsValid => Index >= 0 && Generation > 0;
        public static readonly BufferHandle Invalid = new BufferHandle(-1, 0);
        
        public bool Equals(BufferHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is BufferHandle other && Equals(other);
        public override int GetHashCode() => unchecked((Index * 397) ^ (int)Generation);
        public static bool operator ==(BufferHandle left, BufferHandle right) => left.Equals(right);
        public static bool operator !=(BufferHandle left, BufferHandle right) => !left.Equals(right);
    }
}
