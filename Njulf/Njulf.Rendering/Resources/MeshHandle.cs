using System;

namespace Njulf.Rendering.Resources
{
    public struct MeshHandle : IEquatable<MeshHandle>
    {
        public int Index { get; }
        public uint Generation { get; }
        
        public MeshHandle(int index, uint generation)
        {
            Index = index;
            Generation = generation;
        }
        
        public bool IsValid => Index >= 0 && Generation > 0;
        public static readonly MeshHandle Invalid = new MeshHandle(-1, 0);
        
        public bool Equals(MeshHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is MeshHandle other && Equals(other);
        public override int GetHashCode() => unchecked((Index * 397) ^ (int)Generation);
        public static bool operator ==(MeshHandle left, MeshHandle right) => left.Equals(right);
        public static bool operator !=(MeshHandle left, MeshHandle right) => !left.Equals(right);
    }
}
