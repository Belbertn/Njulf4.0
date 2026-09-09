using System;

namespace Njulf.Core.Scene;

/// <summary>
/// Identifies scene-owned data independently of collection order or display names.
/// </summary>
public interface IIdentifiedSceneEntity
{
    Guid Id { get; }
}
