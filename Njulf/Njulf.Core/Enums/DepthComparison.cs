namespace Njulf.Core.Enums;

/// <summary>Compares incoming fragment depth with stored depth. Select one function; values are not combinable.</summary>
public enum DepthComparison
{
    /// <summary>Incoming depth is less than stored depth.</summary>
    Less = 4,
    /// <summary>Incoming depth is less than or equal to stored depth.</summary>
    LessEqual = 5,
    /// <summary>Incoming depth equals stored depth.</summary>
    Equal = 6,
    /// <summary>Incoming depth is greater than stored depth.</summary>
    Greater = 7,
    /// <summary>Incoming depth is greater than or equal to stored depth.</summary>
    GreaterEqual = 8,
    /// <summary>Incoming depth differs from stored depth.</summary>
    NotEqual = 9,
    /// <summary>Always pass the depth test.</summary>
    Always = 10,
    /// <summary>Never pass the depth test.</summary>
    Never = 11
}
