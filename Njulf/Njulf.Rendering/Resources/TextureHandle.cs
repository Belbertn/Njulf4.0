using System;

namespace Njulf.Rendering.Resources
{
    public struct TextureHandle : IEquatable<TextureHandle>
    {
        public int Index { get; }
        public uint Generation { get; }
        
        public TextureHandle(int index, uint generation)
        {
            Index = index;
            Generation = generation;
        }
        
        public bool IsValid => Index >= 0 && Generation > 0;
        public static readonly TextureHandle Invalid = new TextureHandle(-1, 0);
        
        public bool Equals(TextureHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is TextureHandle other && Equals(other);
        public override int GetHashCode() => unchecked((Index * 397) ^ (int)Generation);
        public static bool operator ==(TextureHandle left, TextureHandle right) => left.Equals(right);
        public static bool operator !=(TextureHandle left, TextureHandle right) => !left.Equals(right);
    }
}
