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
        private readonly List<ModelLightDefinition> _lights = new();
        private readonly List<Action> _disposeActions = new();
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;
        private readonly ReadOnlyCollection<Skeleton> _readOnlySkeletons;
        private readonly ReadOnlyCollection<Skin> _readOnlySkins;
        private readonly ReadOnlyCollection<AnimationClip> _readOnlyAnimationClips;
        private readonly ReadOnlyCollection<ModelLightDefinition> _readOnlyLights;
        private bool _disposed;
        private bool _disposeCompleted;

        public Model()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
            _readOnlySkeletons = _skeletons.AsReadOnly();
            _readOnlySkins = _skins.AsReadOnly();
            _readOnlyAnimationClips = _animationClips.AsReadOnly();
            _readOnlyLights = _lights.AsReadOnly();
        }

        public string Name { get; set; } = "Model";
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }

        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<Skeleton> Skeletons => _readOnlySkeletons;
        public IReadOnlyList<Skin> Skins => _readOnlySkins;
        public IReadOnlyList<AnimationClip> AnimationClips => _readOnlyAnimationClips;
        public IReadOnlyList<ModelLightDefinition> Lights => _readOnlyLights;

        public Model CreateInstance()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var instance = new Model
            {
                Name = Name,
                BoundingBox = BoundingBox,
                BoundingSphere = BoundingSphere
            };
            instance.AddSkeletons(_skeletons);
            instance.AddSkins(_skins);
            instance.AddAnimationClips(_animationClips);
            instance.AddLights(_lights);
            instance._renderObjects.EnsureCapacity(
                _renderObjects.Count);

            try
            {
                foreach (RenderObject renderObject in _renderObjects)
                {
                    RenderObject clone;
                    if (renderObject is SkinnedRenderObject skinned)
                    {
                        var animator = skinned.Animator != null
                            ? new Animator(
                                skinned.Animator.Skeleton,
                                skinned.Animator.Skins,
                                skinned.Animator.Clips)
                            : null;

                        clone = new SkinnedRenderObject(
                            skinned.Mesh!,
                            skinned.Material!)
                        {
                            SkinIndex = skinned.SkinIndex,
                            Animator = animator,
                            SkinningBindTransform =
                                skinned.SkinningBindTransform,
                            AnimatedBoundingBox =
                                skinned.AnimatedBoundingBox,
                            LocalMeshBounds =
                                skinned.LocalMeshBounds,
                            AssetReference =
                                skinned.AssetReference,
                            SkinnedVertexOffset =
                                skinned.SkinnedVertexOffset,
                            SkinningEnabled =
                                skinned.SkinningEnabled,
                            Name = skinned.Name,
                            WorldMatrix = skinned.WorldMatrix,
                            Visible = skinned.Visible,
                            IsStatic = skinned.IsStatic,
                            Enabled = skinned.Enabled,
                            UpdateOrder = skinned.UpdateOrder
                        };
                    }
                    else
                    {
                        clone = new RenderObject
                        {
                            Mesh = renderObject.Mesh,
                            Material = renderObject.Material,
                            LocalMeshBounds =
                                renderObject.LocalMeshBounds,
                            AssetReference =
                                renderObject.AssetReference,
                            Name = renderObject.Name,
                            WorldMatrix = renderObject.WorldMatrix,
                            Visible = renderObject.Visible,
                            IsStatic = renderObject.IsStatic,
                            Enabled = renderObject.Enabled,
                            UpdateOrder = renderObject.UpdateOrder
                        };
                    }

                    instance.Add(clone);
                    renderObject.CopyResourceLifetimeTo(clone);
                }

                return instance;
            }
            catch (Exception instanceFailure)
            {
                try
                {
                    instance.Dispose();
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Model instance creation and resource rollback both failed.",
                        instanceFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        public void Add(RenderObject renderObject)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(renderObject);
            _renderObjects.Add(renderObject);
        }

        public void AddSkeletons(IEnumerable<Skeleton> skeletons)
        {
            if (skeletons == null)
                throw new ArgumentNullException(nameof(skeletons));
            ObjectDisposedException.ThrowIf(_disposed, this);

            _skeletons.AddRange(skeletons);
        }

        public void AddSkins(IEnumerable<Skin> skins)
        {
            if (skins == null)
                throw new ArgumentNullException(nameof(skins));
            ObjectDisposedException.ThrowIf(_disposed, this);

            _skins.AddRange(skins);
        }

        public void AddAnimationClips(IEnumerable<AnimationClip> clips)
        {
            if (clips == null)
                throw new ArgumentNullException(nameof(clips));
            ObjectDisposedException.ThrowIf(_disposed, this);

            _animationClips.AddRange(clips);
        }

        public void AddLights(IEnumerable<ModelLightDefinition> lights)
        {
            ArgumentNullException.ThrowIfNull(lights);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lights.AddRange(lights);
        }

        public void Remove(RenderObject renderObject)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(renderObject);
            int index = _renderObjects.IndexOf(renderObject);
            if (index < 0)
                return;

            // The model is the owner. A failed release remains in the model so
            // callers can retry Remove or dispose the model without losing the
            // outstanding lease.
            renderObject.Dispose();
            _renderObjects.RemoveAt(index);
        }

        /// <summary>
        /// Transfers ownership of a render object out of this model without
        /// disposing it. The caller becomes responsible for disposal.
        /// </summary>
        public bool Detach(RenderObject renderObject)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(renderObject);
            return _renderObjects.Remove(renderObject);
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            List<Exception>? failures =
                DisposeOwnedRenderObjects();
            if (failures != null)
            {
                throw new AggregateException(
                    "One or more model-owned render objects could not be removed.",
                    failures);
            }
        }

        public void AddDisposeAction(Action disposeAction)
        {
            if (disposeAction == null)
                throw new ArgumentNullException(nameof(disposeAction));
            ObjectDisposedException.ThrowIf(_disposed, this);

            _disposeActions.Add(disposeAction);
        }

        public void Update(float deltaTime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var renderObject in _renderObjects)
            {
                if (renderObject.Enabled)
                    renderObject.Update(deltaTime);
            }
        }

        public void Dispose()
        {
            if (_disposeCompleted)
                return;
            _disposed = true;

            List<Exception>? failures =
                DisposeOwnedRenderObjects();
            for (int index = _disposeActions.Count - 1;
                 index >= 0;
                 index--)
            {
                try
                {
                    _disposeActions[index]();
                    _disposeActions.RemoveAt(index);
                }
                catch (Exception disposeFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(disposeFailure);
                }
            }

            _skeletons.Clear();
            _skins.Clear();
            _animationClips.Clear();

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more model-owned resources could not be disposed.",
                    failures);
            }

            _disposeCompleted = true;
        }

        private List<Exception>? DisposeOwnedRenderObjects()
        {
            List<Exception>? failures = null;
            for (int index = _renderObjects.Count - 1;
                 index >= 0;
                 index--)
            {
                try
                {
                    _renderObjects[index].Dispose();
                    _renderObjects.RemoveAt(index);
                }
                catch (Exception disposeFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(disposeFailure);
                }
            }

            return failures;
        }
    }
}
