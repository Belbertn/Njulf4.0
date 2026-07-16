using Njulf.Core.Scene;
using Njulf.Core.Vfx;

namespace Njulf.Assets.Scenes;

/// <summary>Application-owned effect resolver for scene documents; particle effect authoring stays renderer-neutral.</summary>
public interface ISceneParticleEffectStore
{
    ParticleEffect Load(SceneAssetReference reference);
}
