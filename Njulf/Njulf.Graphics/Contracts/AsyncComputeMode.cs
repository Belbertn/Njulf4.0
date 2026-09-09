using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

    /// <summary>
    /// Controls how the renderer is allowed to place work on the asynchronous compute queue.
    /// <see cref="ForceEnabledForValidation"/> still performs every capability and resource-plan
    /// validation; it only bypasses the profitability decision.
    /// </summary>
    public enum AsyncComputeMode
    {
        Disabled = 0,
        Auto = 1,
        ForceEnabledForValidation = 2
    }
