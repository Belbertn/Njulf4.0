namespace Njulf.Core.Enums
{
    /// <summary>Depth testing and writing configuration, independent of the comparison function. Does not configure stencil.</summary>
    public enum DepthStencilState
    {
        /// <summary>Enable depth testing and depth writes.</summary>
        Default = 0,
        /// <summary>Enable depth testing without writing depth.</summary>
        DepthRead = 1,
        /// <summary>Write depth without rejecting fragments by depth (an always-passing test).</summary>
        DepthWrite = 2,
        /// <summary>Disable depth testing and depth writes.</summary>
        None = 3
    }
}
