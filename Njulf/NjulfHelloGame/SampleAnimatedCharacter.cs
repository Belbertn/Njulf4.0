using System;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal static class SampleAnimatedCharacter
{
    private const string CharacterPath = "Strut.glb";
    private const float TargetHeight = 1.75f;
    private static readonly CoreVector3 TargetGroundCenter = new(1.35f, 0.0f, 3.6f);

    public static Model Configure(Scene scene, IContentManager content)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (content == null)
            throw new ArgumentNullException(nameof(content));

        Model asset = content.Load<Model>(CharacterPath)
            ?? throw new InvalidOperationException($"Content manager returned null for animated character '{CharacterPath}'.");
        Model character = asset.CreateInstance()
            ?? throw new InvalidOperationException($"Animated character '{CharacterPath}' did not create an instance.");

        int playingAnimators = StartFirstAnimationClip(character);
        CoreMatrix4x4 world = CreateCharacterWorld(character);
        foreach (RenderObject renderObject in character.RenderObjects)
        {
            renderObject.Name = $"AnimatedCharacter.Strut.{renderObject.Name}";
            renderObject.WorldMatrix = world;
            renderObject.Visible = true;
            scene.Add(renderObject);

            if (renderObject is IUpdateable updateable)
                scene.Add(updateable);
        }

        Console.WriteLine(
            $"Loaded animated character '{CharacterPath}': objects={character.RenderObjects.Count}, " +
            $"skeletons={character.Skeletons.Count}, skins={character.Skins.Count}, clips={character.AnimationClips.Count}, playingAnimators={playingAnimators}.");

        return character;
    }

    private static int StartFirstAnimationClip(Model character)
    {
        int playing = 0;
        foreach (RenderObject renderObject in character.RenderObjects)
        {
            if (renderObject is not SkinnedRenderObject skinned ||
                skinned.Animator == null ||
                skinned.Animator.Clips.Count == 0)
            {
                continue;
            }

            skinned.Animator.Play(skinned.Animator.Clips[0], loop: true);
            playing++;
        }

        return playing;
    }

    private static CoreMatrix4x4 CreateCharacterWorld(Model character)
    {
        CoreVector3 size = character.BoundingBox.Size;
        float sourceHeight = size.Y > 0.0001f ? size.Y : 1.0f;
        float scale = TargetHeight / sourceHeight;
        CoreVector3 center = character.BoundingBox.Center;
        CoreVector3 min = character.BoundingBox.Min;

        var translation = new CoreVector3(
            TargetGroundCenter.X - center.X * scale,
            TargetGroundCenter.Y - min.Y * scale,
            TargetGroundCenter.Z - center.Z * scale);

        return CoreMatrix4x4.CreateScale(new CoreVector3(scale)) *
               CoreMatrix4x4.CreateTranslation(translation);
    }
}
