using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Njulf.Core.Animation;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Model : IDisposable
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<Skeleton> _skeletons = new();
        private readonly List<Skin> _skins = new();
        private readonly List<AnimationClip> _animationClips = new();
        private readonly List<Action> _disposeActions = new();
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;
        private readonly ReadOnlyCollection<Skeleton> _readOnlySkeletons;
        private readonly ReadOnlyCollection<Skin> _readOnlySkins;
        private readonly ReadOnlyCollection<AnimationClip> _readOnlyAnimationClips;

        public Model()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
            _readOnlySkeletons = _skeletons.AsReadOnly();
            _readOnlySkins = _skins.AsReadOnly();
            _readOnlyAnimationClips = _animationClips.AsReadOnly();
        }
        
        public string Name { get; set; } = "Model";
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        
        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<Skeleton> Skeletons => _readOnlySkeletons;
        public IReadOnlyList<Skin> Skins => _readOnlySkins;
        public IReadOnlyList<AnimationClip> AnimationClips => _readOnlyAnimationClips;

        public Model CreateInstance()
        {
            var instance = new Model
            {
                Name = Name,
                BoundingBox = BoundingBox,
                BoundingSphere = BoundingSphere
            };
            instance.AddSkeletons(_skeletons);
            instance.AddSkins(_skins);
            instance.AddAnimationClips(_animationClips);

            foreach (RenderObject renderObject in _renderObjects)
            {
                if (renderObject is SkinnedRenderObject skinned)
                {
                    var animator = skinned.Animator != null
                        ? new Animator(skinned.Animator.Skeleton, skinned.Animator.Skins, skinned.Animator.Clips)
                        : null;

                    instance.Add(new SkinnedRenderObject(skinned.Mesh!, skinned.Material!)
                    {
                        SkinIndex = skinned.SkinIndex,
                        Animator = animator,
                        AnimatedBoundingBox = skinned.AnimatedBoundingBox,
                        SkinnedVertexOffset = skinned.SkinnedVertexOffset,
                        SkinningEnabled = skinned.SkinningEnabled,
                        Name = skinned.Name,
                        WorldMatrix = skinned.WorldMatrix,
                        Visible = skinned.Visible,
                        Enabled = skinned.Enabled,
                        UpdateOrder = skinned.UpdateOrder
                    });
                    continue;
                }

                instance.Add(new RenderObject
                {
                    Mesh = renderObject.Mesh,
                    Material = renderObject.Material,
                    Name = renderObject.Name,
                    WorldMatrix = renderObject.WorldMatrix,
                    Visible = renderObject.Visible,
                    Enabled = renderObject.Enabled,
                    UpdateOrder = renderObject.UpdateOrder
                });
            }

            return instance;
        }
        
        public void Add(RenderObject renderObject)
        {
            _renderObjects.Add(renderObject);
        }

        public void AddSkeletons(IEnumerable<Skeleton> skeletons)
        {
            if (skeletons == null)
                throw new ArgumentNullException(nameof(skeletons));

            _skeletons.AddRange(skeletons);
        }

        public void AddSkins(IEnumerable<Skin> skins)
        {
            if (skins == null)
                throw new ArgumentNullException(nameof(skins));

            _skins.AddRange(skins);
        }

        public void AddAnimationClips(IEnumerable<AnimationClip> clips)
        {
            if (clips == null)
                throw new ArgumentNullException(nameof(clips));

            _animationClips.AddRange(clips);
        }
        
        public void Remove(RenderObject renderObject)
        {
            _renderObjects.Remove(renderObject);
        }
        
        public void Clear()
        {
            _renderObjects.Clear();
        }

        public void AddDisposeAction(Action disposeAction)
        {
            if (disposeAction == null)
                throw new ArgumentNullException(nameof(disposeAction));

            _disposeActions.Add(disposeAction);
        }
        
        public void Update(float deltaTime)
        {
            foreach (var renderObject in _renderObjects)
            {
                if (renderObject.Enabled)
                    renderObject.Update(deltaTime);
            }
        }
        
        public void Dispose()
        {
            foreach (var renderObject in _renderObjects)
            {
                renderObject.Dispose();
            }

            foreach (Action disposeAction in _disposeActions)
                disposeAction();

            _renderObjects.Clear();
            _disposeActions.Clear();
            _skeletons.Clear();
            _skins.Clear();
            _animationClips.Clear();
        }
    }
}
