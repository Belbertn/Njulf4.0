using System;

namespace Njulf.Rendering.Resources
{
    public readonly struct MaterialHandle : IEquatable<MaterialHandle>
    {
        public MaterialHandle(int index, uint generation)
        {
            Index = index;
            Generation = generation;
        }

        public int Index { get; }
        public uint Generation { get; }

        public bool IsValid => Index >= 0 && Generation > 0;

        public static MaterialHandle Invalid { get; } = new MaterialHandle(-1, 0);

        public bool Equals(MaterialHandle other) => Index == other.Index && Generation == other.Generation;

        public override bool Equals(object? obj) => obj is MaterialHandle other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Index, Generation);

        public static bool operator ==(MaterialHandle left, MaterialHandle right) => left.Equals(right);

        public static bool operator !=(MaterialHandle left, MaterialHandle right) => !left.Equals(right);

        public override string ToString() => IsValid
            ? $"MaterialHandle(Index={Index}, Generation={Generation})"
            : "MaterialHandle.Invalid";
    }
}
