using System;
using System.Collections.Generic;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class AnimationTests
    {
        [Test]
        public void Animator_EvaluatesHierarchyAndInverseBindSkinMatrix()
        {
            Skeleton skeleton = CreateTwoJointSkeleton();
            Skin skin = new Skin
            {
                Name = "TestSkin",
                Skeleton = skeleton,
                JointIndices = new[] { 0, 1 },
                InverseBindMatrices = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(new Vector3(-1f, 0f, 0f)) }
            };
            AnimationClip clip = new AnimationClip
            {
                Name = "Stretch",
                DurationSeconds = 1f,
                Channels = new[]
                {
                    new AnimationChannel
                    {
                        TargetJointIndex = 1,
                        Path = AnimationChannelPath.Translation,
                        Sampler = new AnimationSampler
                        {
                            InputTimes = new[] { 0f, 1f },
                            OutputValues = new[]
                            {
                                new Vector4(1f, 0f, 0f, 0f),
                                new Vector4(2f, 0f, 0f, 0f)
                            }
                        }
                    }
                }
            };

            var animator = new Animator(skeleton, new[] { skin }, new[] { clip });
            animator.Play(clip, loop: false);
            animator.Seek(0.5f);

            Vector3 skinned = CpuSkinning.SkinPosition(
                new Vector3(1f, 0f, 0f),
                new VertexJointIndices(1, 0, 0, 0),
                new VertexJointWeights(1f, 0f, 0f, 0f),
                animator.GetSkinMatrices(0));

            Assert.That(skinned.X, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(skinned.Y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(skinned.Z, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void Animator_LoopsClampsAndPausesDeterministically()
        {
            Skeleton skeleton = CreateTwoJointSkeleton();
            AnimationClip clip = new AnimationClip
            {
                Name = "Move",
                DurationSeconds = 1f,
                Channels = new[]
                {
                    new AnimationChannel
                    {
                        TargetJointIndex = 0,
                        Path = AnimationChannelPath.Translation,
                        Sampler = new AnimationSampler
                        {
                            InputTimes = new[] { 0f, 1f },
                            OutputValues = new[] { new Vector4(0f, 0f, 0f, 0f), new Vector4(10f, 0f, 0f, 0f) }
                        }
                    }
                }
            };

            var animator = new Animator(skeleton, Array.Empty<Skin>(), new[] { clip });
            animator.Play(clip, loop: true);
            animator.Update(1.25f);
            Assert.That(animator.TimeSeconds, Is.EqualTo(0.25f).Within(0.0001f));

            animator.Pause();
            animator.Update(0.5f);
            Assert.That(animator.TimeSeconds, Is.EqualTo(0.25f).Within(0.0001f));

            animator.Play(clip, loop: false);
            animator.Update(2f);
            Assert.That(animator.TimeSeconds, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(animator.IsPlaying, Is.False);
        }

        [Test]
        public void ModelCreateInstance_CreatesIndependentAnimatorStateForSkinnedObjects()
        {
            Skeleton skeleton = CreateTwoJointSkeleton();
            AnimationClip clip = new AnimationClip
            {
                Name = "Move",
                DurationSeconds = 1f,
                Channels = Array.Empty<AnimationChannel>()
            };
            var skin = new Skin
            {
                Name = "Skin",
                Skeleton = skeleton,
                JointIndices = new[] { 0, 1 },
                InverseBindMatrices = new[] { Matrix4x4.Identity, Matrix4x4.Identity }
            };
            var model = new Model { Name = "AnimatedModel" };
            model.AddSkeletons(new[] { skeleton });
            model.AddSkins(new[] { skin });
            model.AddAnimationClips(new[] { clip });
            model.Add(new SkinnedRenderObject("mesh", "material")
            {
                SkinIndex = 0,
                Animator = new Animator(skeleton, new[] { skin }, new[] { clip }),
                SkinningBindTransform = Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f))
            });

            Model first = model.CreateInstance();
            Model second = model.CreateInstance();
            var firstObject = (SkinnedRenderObject)first.RenderObjects[0];
            var secondObject = (SkinnedRenderObject)second.RenderObjects[0];

            firstObject.Animator!.Play(clip);
            firstObject.Animator.Update(0.25f);

            Assert.Multiple(() =>
            {
                Assert.That(firstObject.Animator, Is.Not.SameAs(secondObject.Animator));
                Assert.That(firstObject.Animator!.TimeSeconds, Is.EqualTo(0.25f).Within(0.0001f));
                Assert.That(secondObject.Animator!.TimeSeconds, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(secondObject.SkinningBindTransform.M41, Is.EqualTo(10f).Within(0.0001f));
            });
        }

        private static Skeleton CreateTwoJointSkeleton()
        {
            var root = new SkeletonJoint
            {
                Name = "Root",
                ParentIndex = -1,
                LocalBindPose = AnimationTransform.Identity,
                LocalBindTransform = Matrix4x4.Identity,
                InverseBindMatrix = Matrix4x4.Identity
            };
            var childBind = new AnimationTransform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One);
            var child = new SkeletonJoint
            {
                Name = "Child",
                ParentIndex = 0,
                LocalBindPose = childBind,
                LocalBindTransform = childBind.ToMatrix(),
                InverseBindMatrix = Matrix4x4.CreateTranslation(new Vector3(-1f, 0f, 0f))
            };

            return new Skeleton
            {
                Name = "TestSkeleton",
                Joints = new[] { root, child },
                RootJointIndex = 0
            };
        }
    }
}
