using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using Silk.NET.Assimp;
using Silk.NET.Core.Native;
using File = System.IO.File;
using CoreSkeleton = Njulf.Core.Animation.Skeleton;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector4 = System.Numerics.Vector4;

namespace Njulf.Assets
{
    public class ModelImporter : IDisposable
    {
        private readonly Assimp _assimp;
        private bool _disposed;

        public ModelImporter()
        {
            _assimp = Assimp.GetApi();
        }

        public ModelMesh Import(string path, ImporterOptions? options = null)
        {
            options ??= ImporterOptions.Default!;

            if (!File.Exists(path))
                throw new FileNotFoundException($"Model file was not found: {Path.GetFullPath(path)}", Path.GetFullPath(path));

            string fullPath = Path.GetFullPath(path);
            GltfAssetManifest? gltfManifest = LoadAndValidateGltfManifest(fullPath);

            var postProcess = PostProcessSteps.Triangulate;
            if (options.GenerateNormals)
                postProcess |= PostProcessSteps.GenerateNormals;
            if (options.GenerateTangents)
                postProcess |= PostProcessSteps.CalculateTangentSpace;
            if (options.JoinIdenticalVertices && gltfManifest == null)
                postProcess |= PostProcessSteps.JoinIdenticalVertices;
            if (options.FlipUVs)
                postProcess |= PostProcessSteps.FlipUVs;
            if (options.SortByPrimitiveType)
                postProcess |= PostProcessSteps.SortByPrimitiveType;

            unsafe
            {
                var scene = _assimp.ImportFile(fullPath, (uint)postProcess);
                if (scene == null)
                {
                    string error = SilkMarshal.PtrToString((nint)_assimp.GetErrorString()) ?? "Unknown error";
                    throw new Exception($"Failed to import model '{fullPath}': {error}");
                }

                try
                {
                    if (((SceneFlags)scene->MFlags & SceneFlags.Incomplete) != 0)
                        throw new Exception("Scene import incomplete");

                    if (scene->MRootNode == null)
                        throw new Exception("No root node in scene");

                    var mesh = ProcessScene(scene, fullPath, options, gltfManifest);
                    return mesh;
                }
                finally
                {
                    _assimp.ReleaseImport(scene);
                }
            }
        }

        private unsafe ModelMesh ProcessScene(Scene* scene, string path, ImporterOptions options, GltfAssetManifest? gltfManifest)
        {
            var mesh = new ModelMesh
            {
                Name = Path.GetFileNameWithoutExtension(path)
            };
            AssimpAnimationManifest animationManifest = BuildAssimpAnimationManifest(scene);
            mesh.Skeletons.AddRange(animationManifest.Skeletons);
            mesh.Skins.AddRange(animationManifest.Skins);
            mesh.AnimationClips.AddRange(animationManifest.AnimationClips);
            mesh.AnimationDiagnostics = animationManifest.Diagnostics;
            if (gltfManifest != null)
                mesh.ImportDiagnostics = gltfManifest.Diagnostics;

            AccumulateNodeMeshTotals(scene, scene->MRootNode, out int vertexCapacity, out int indexCapacity);
            var vertices = new List<Vector3>(vertexCapacity);
            var normals = new List<Vector3>(vertexCapacity);
            var tangents = new List<Vector3>(vertexCapacity);
            var bitangents = new List<Vector3>(vertexCapacity);
            var texCoords = new List<Vector2>(vertexCapacity);
            var texCoords1 = new List<Vector2>(vertexCapacity);
            var vertexColors = new List<Vector4>(vertexCapacity);
            var jointIndices = new List<VertexJointIndices>(vertexCapacity);
            var jointWeights = new List<VertexJointWeights>(vertexCapacity);
            var indices = new List<uint>(indexCapacity);
            mesh.Materials.AddRange(ProcessMaterials(scene, path, gltfManifest));

            ProcessNode(
                scene,
                scene->MRootNode,
                NumericsMatrix4x4.Identity,
                options,
                mesh,
                vertices,
                normals,
                tangents,
                bitangents,
                texCoords,
                texCoords1,
                vertexColors,
                jointIndices,
                jointWeights,
                indices,
                animationManifest);

            mesh.Vertices = vertices.ToArray();
            mesh.Normals = normals.ToArray();
            mesh.Tangents = tangents.ToArray();
            mesh.Bitangents = bitangents.ToArray();
            mesh.TexCoords = texCoords.ToArray();
            mesh.TexCoords1 = texCoords1.ToArray();
            mesh.VertexColors = vertexColors.ToArray();
            if (jointIndices.Count == vertices.Count && jointWeights.Any(w => w.Sum > 0f))
            {
                mesh.JointIndices0 = jointIndices.ToArray();
                mesh.JointWeights0 = jointWeights.ToArray();
            }
            mesh.Indices = indices.ToArray();

            ComputeBoundingVolume(vertices, out var bbox, out var bsphere);
            mesh.BoundingBox = bbox;
            mesh.BoundingSphere = bsphere;

            return mesh;
        }

        private static unsafe AssimpAnimationManifest BuildAssimpAnimationManifest(Scene* scene)
        {
            var boneOffsets = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            var boneNames = new List<string>();
            for (uint meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
            {
                Mesh* mesh = scene->MMeshes[meshIndex];
                for (uint boneIndex = 0; boneIndex < mesh->MNumBones; boneIndex++)
                {
                    Bone* bone = mesh->MBones[boneIndex];
                    string boneName = bone->MName.AsString;
                    if (string.IsNullOrWhiteSpace(boneName) || boneOffsets.ContainsKey(boneName))
                        continue;

                    boneOffsets[boneName] = ToCoreMatrixTransposed(bone->MOffsetMatrix);
                    boneNames.Add(boneName);
                }
            }

            if (boneNames.Count == 0)
                return AssimpAnimationManifest.Empty;

            var nodeGlobals = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            var nodesByName = new Dictionary<string, nint>(StringComparer.Ordinal);
            BuildAssimpNodeMaps(scene->MRootNode, Matrix4x4.Identity, nodeGlobals, nodesByName);

            var nodeToJoint = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < boneNames.Count; i++)
                nodeToJoint[boneNames[i]] = i;

            var joints = new SkeletonJoint[boneNames.Count];
            int rootJoint = -1;
            for (int jointIndex = 0; jointIndex < boneNames.Count; jointIndex++)
            {
                string boneName = boneNames[jointIndex];
                if (!nodesByName.TryGetValue(boneName, out nint boneNodeAddress))
                    throw new InvalidDataException($"Assimp bone '{boneName}' does not have a matching scene node.");

                Node* boneNode = (Node*)boneNodeAddress;
                int parentJoint = FindNearestAssimpAncestorJoint(boneNode->MParent, nodeToJoint);
                Matrix4x4 globalBind = nodeGlobals[boneName];
                Matrix4x4 localBind = parentJoint >= 0
                    ? globalBind * nodeGlobals[boneNames[parentJoint]].Invert()
                    : globalBind;
                AnimationTransform bindPose = ToAnimationTransform(localBind);
                joints[jointIndex] = new SkeletonJoint
                {
                    Name = boneName,
                    ParentIndex = parentJoint,
                    LocalBindPose = bindPose,
                    LocalBindTransform = localBind,
                    InverseBindMatrix = boneOffsets[boneName]
                };

                if (parentJoint < 0 && rootJoint < 0)
                    rootJoint = jointIndex;
            }

            var skeleton = new CoreSkeleton
            {
                Name = "AssimpSkeleton",
                Joints = joints,
                RootJointIndex = rootJoint >= 0 ? rootJoint : 0
            };
            var skin = new Skin
            {
                Name = "AssimpSkin",
                Skeleton = skeleton,
                JointIndices = Enumerable.Range(0, joints.Length).ToArray(),
                InverseBindMatrices = boneNames.Select(name => boneOffsets[name]).ToArray()
            };

            List<AnimationClip> clips = ReadAssimpAnimations(scene, nodeToJoint, boneNames, nodeGlobals, nodesByName);
            var diagnostics = new ModelAnimationImportDiagnostics(
                skeletonCount: 1,
                jointCount: joints.Length,
                skinCount: 1,
                skinnedSubMeshCount: CountAssimpSkinnedMeshes(scene),
                animationClipCount: clips.Count,
                animationChannelCount: clips.Sum(clip => clip.Channels.Count),
                unsupportedInterpolationCount: 0,
                maxInfluencesPerVertex: 4);

            return new AssimpAnimationManifest(
                new[] { skeleton },
                new[] { skin },
                clips,
                nodeToJoint,
                rootJoint >= 0 ? nodesByName[boneNames[rootJoint]] : 0,
                diagnostics);
        }

        private static unsafe int CountAssimpSkinnedMeshes(Scene* scene)
        {
            int count = 0;
            for (uint meshIndex = 0; meshIndex < scene->MNumMeshes; meshIndex++)
            {
                if (scene->MMeshes[meshIndex]->MNumBones > 0)
                    count++;
            }

            return count;
        }

        private static unsafe void BuildAssimpNodeMaps(
            Node* node,
            Matrix4x4 parentGlobal,
            Dictionary<string, Matrix4x4> globals,
            Dictionary<string, nint> nodes)
        {
            string name = node->MName.AsString;
            Matrix4x4 local = ToCoreMatrixTransposed(node->MTransformation);
            Matrix4x4 global = local * parentGlobal;
            if (!string.IsNullOrWhiteSpace(name))
            {
                globals[name] = global;
                nodes[name] = (nint)node;
            }

            for (uint i = 0; i < node->MNumChildren; i++)
                BuildAssimpNodeMaps(node->MChildren[i], global, globals, nodes);
        }

        private static unsafe int FindNearestAssimpAncestorJoint(Node* parent, IReadOnlyDictionary<string, int> nodeToJoint)
        {
            Node* current = parent;
            while (current != null)
            {
                if (nodeToJoint.TryGetValue(current->MName.AsString, out int jointIndex))
                    return jointIndex;

                current = current->MParent;
            }

            return -1;
        }

        private static unsafe List<AnimationClip> ReadAssimpAnimations(
            Scene* scene,
            IReadOnlyDictionary<string, int> nodeToJoint,
            IReadOnlyList<string> jointNames,
            IReadOnlyDictionary<string, Matrix4x4> nodeGlobals,
            IReadOnlyDictionary<string, nint> nodesByName)
        {
            var clips = new List<AnimationClip>();
            for (uint animationIndex = 0; animationIndex < scene->MNumAnimations; animationIndex++)
            {
                Animation* animation = scene->MAnimations[animationIndex];
                double ticksPerSecond = animation->MTicksPerSecond > 0.0 ? animation->MTicksPerSecond : 25.0;
                double startTick = FindAssimpAnimationStartTick(animation);
                var channels = new List<AnimationChannel>();

                for (uint channelIndex = 0; channelIndex < animation->MNumChannels; channelIndex++)
                {
                    NodeAnim* nodeChannel = animation->MChannels[channelIndex];
                    string nodeName = nodeChannel->MNodeName.AsString;
                    if (!nodeToJoint.TryGetValue(nodeName, out int targetJoint) ||
                        !nodesByName.TryGetValue(nodeName, out nint targetNodeAddress))
                    {
                        continue;
                    }

                    Node* targetNode = (Node*)targetNodeAddress;
                    Matrix4x4 ancestorConversion = ResolveAssimpAnimationAncestorConversion(
                        targetNode,
                        nodeToJoint,
                        jointNames,
                        nodeGlobals);
                    AddAssimpNodeChannels(nodeChannel, targetJoint, ticksPerSecond, startTick, ancestorConversion, channels);
                }

                float duration = channels
                    .SelectMany(channel => channel.Sampler.InputTimes)
                    .DefaultIfEmpty(0f)
                    .Max();
                if (duration <= 0f && animation->MDuration > 0.0)
                    duration = (float)(animation->MDuration / ticksPerSecond);

                clips.Add(new AnimationClip
                {
                    Name = string.IsNullOrWhiteSpace(animation->MName.AsString) ? $"Animation_{animationIndex}" : animation->MName.AsString,
                    DurationSeconds = duration,
                    Channels = channels
                });
            }

            return clips;
        }

        private static unsafe double FindAssimpAnimationStartTick(Animation* animation)
        {
            double start = double.PositiveInfinity;
            for (uint channelIndex = 0; channelIndex < animation->MNumChannels; channelIndex++)
            {
                NodeAnim* channel = animation->MChannels[channelIndex];
                if (channel->MNumPositionKeys > 0)
                    start = Math.Min(start, channel->MPositionKeys[0].MTime);
                if (channel->MNumRotationKeys > 0)
                    start = Math.Min(start, channel->MRotationKeys[0].MTime);
                if (channel->MNumScalingKeys > 0)
                    start = Math.Min(start, channel->MScalingKeys[0].MTime);
            }

            return double.IsFinite(start) ? start : 0.0;
        }

        private static unsafe Matrix4x4 ResolveAssimpAnimationAncestorConversion(
            Node* targetNode,
            IReadOnlyDictionary<string, int> nodeToJoint,
            IReadOnlyList<string> jointNames,
            IReadOnlyDictionary<string, Matrix4x4> nodeGlobals)
        {
            Node* parentNode = targetNode->MParent;
            if (parentNode == null)
                return Matrix4x4.Identity;

            string parentName = parentNode->MName.AsString;
            if (!nodeGlobals.TryGetValue(parentName, out Matrix4x4 parentGlobal))
                return Matrix4x4.Identity;

            int parentJoint = FindNearestAssimpAncestorJoint(parentNode, nodeToJoint);
            if (parentJoint < 0)
                return parentGlobal;

            return parentGlobal * nodeGlobals[jointNames[parentJoint]].Invert();
        }

        private static unsafe void AddAssimpNodeChannels(
            NodeAnim* nodeChannel,
            int targetJoint,
            double ticksPerSecond,
            double startTick,
            Matrix4x4 ancestorConversion,
            List<AnimationChannel> channels)
        {
            if (nodeChannel->MNumPositionKeys > 0)
            {
                float[] times = new float[nodeChannel->MNumPositionKeys];
                Vector4[] values = new Vector4[nodeChannel->MNumPositionKeys];
                for (uint i = 0; i < nodeChannel->MNumPositionKeys; i++)
                {
                    VectorKey key = nodeChannel->MPositionKeys[i];
                    times[i] = ToAnimationSeconds(key.MTime, startTick, ticksPerSecond);
                    Vector3 position = new(key.MValue.X, key.MValue.Y, key.MValue.Z);
                    Vector3 converted = position * ancestorConversion;
                    values[i] = new Vector4(converted.X, converted.Y, converted.Z, 0f);
                }

                channels.Add(CreateAssimpChannel(targetJoint, AnimationChannelPath.Translation, times, values));
            }

            if (nodeChannel->MNumRotationKeys > 0)
            {
                float[] times = new float[nodeChannel->MNumRotationKeys];
                Vector4[] values = new Vector4[nodeChannel->MNumRotationKeys];
                Matrix4x4 ancestorRotation = ExtractRotationMatrix(ancestorConversion);
                for (uint i = 0; i < nodeChannel->MNumRotationKeys; i++)
                {
                    QuatKey key = nodeChannel->MRotationKeys[i];
                    times[i] = ToAnimationSeconds(key.MTime, startTick, ticksPerSecond);
                    var source = new Quaternion(-key.MValue.X, -key.MValue.Y, -key.MValue.Z, key.MValue.W);
                    Matrix4x4 convertedMatrix = source.Normalized().ToMatrix4x4() * ancestorRotation;
                    Quaternion converted = Quaternion.FromMatrix4x4(convertedMatrix).Normalized();
                    values[i] = new Vector4(converted.X, converted.Y, converted.Z, converted.W);
                }

                channels.Add(CreateAssimpChannel(targetJoint, AnimationChannelPath.Rotation, times, values));
            }

            if (nodeChannel->MNumScalingKeys > 0)
            {
                float[] times = new float[nodeChannel->MNumScalingKeys];
                Vector4[] values = new Vector4[nodeChannel->MNumScalingKeys];
                Vector3 ancestorScale = ancestorConversion.Scale;
                for (uint i = 0; i < nodeChannel->MNumScalingKeys; i++)
                {
                    VectorKey key = nodeChannel->MScalingKeys[i];
                    times[i] = ToAnimationSeconds(key.MTime, startTick, ticksPerSecond);
                    values[i] = new Vector4(
                        key.MValue.X * ancestorScale.X,
                        key.MValue.Y * ancestorScale.Y,
                        key.MValue.Z * ancestorScale.Z,
                        0f);
                }

                channels.Add(CreateAssimpChannel(targetJoint, AnimationChannelPath.Scale, times, values));
            }
        }

        private static AnimationChannel CreateAssimpChannel(
            int targetJoint,
            AnimationChannelPath path,
            float[] times,
            Vector4[] values)
        {
            return new AnimationChannel
            {
                TargetNodeIndex = targetJoint,
                TargetJointIndex = targetJoint,
                Path = path,
                Sampler = new AnimationSampler
                {
                    InputTimes = times,
                    OutputValues = values,
                    Interpolation = AnimationInterpolation.Linear
                }
            };
        }

        private static float ToAnimationSeconds(double tick, double startTick, double ticksPerSecond)
        {
            return (float)Math.Max(0.0, (tick - startTick) / ticksPerSecond);
        }

        private static unsafe void BuildAssimpSkinningStreams(
            Mesh* mesh,
            AssimpAnimationManifest manifest,
            string subMeshName,
            out VertexJointIndices[] jointIndices,
            out VertexJointWeights[] jointWeights)
        {
            jointIndices = Array.Empty<VertexJointIndices>();
            jointWeights = Array.Empty<VertexJointWeights>();
            if (mesh->MNumBones == 0)
                return;

            var influences = new List<VertexInfluence>[(int)mesh->MNumVertices];
            for (int i = 0; i < influences.Length; i++)
                influences[i] = new List<VertexInfluence>(4);

            for (uint boneIndex = 0; boneIndex < mesh->MNumBones; boneIndex++)
            {
                Bone* bone = mesh->MBones[boneIndex];
                string boneName = bone->MName.AsString;
                if (!manifest.NodeNameToJoint.TryGetValue(boneName, out int jointIndex))
                    throw new InvalidDataException($"Skinned submesh '{subMeshName}' references Assimp bone '{boneName}', but no matching joint was imported.");

                for (uint weightIndex = 0; weightIndex < bone->MNumWeights; weightIndex++)
                {
                    VertexWeight weight = bone->MWeights[weightIndex];
                    if (weight.MVertexId >= mesh->MNumVertices || weight.MWeight <= 0f)
                        continue;

                    influences[(int)weight.MVertexId].Add(new VertexInfluence((ushort)jointIndex, weight.MWeight));
                }
            }

            jointIndices = new VertexJointIndices[mesh->MNumVertices];
            jointWeights = new VertexJointWeights[mesh->MNumVertices];
            for (int vertex = 0; vertex < influences.Length; vertex++)
            {
                List<VertexInfluence> vertexInfluences = influences[vertex];
                vertexInfluences.Sort((left, right) => right.Weight.CompareTo(left.Weight));
                while (vertexInfluences.Count < 4)
                    vertexInfluences.Add(default);

                jointIndices[vertex] = new VertexJointIndices(
                    vertexInfluences[0].Joint,
                    vertexInfluences[1].Joint,
                    vertexInfluences[2].Joint,
                    vertexInfluences[3].Joint);
                jointWeights[vertex] = new VertexJointWeights(
                    vertexInfluences[0].Weight,
                    vertexInfluences[1].Weight,
                    vertexInfluences[2].Weight,
                    vertexInfluences[3].Weight).Normalized();
            }
        }

        private static Matrix4x4 ToCoreMatrixTransposed(NumericsMatrix4x4 matrix)
        {
            return ToCoreMatrix(NumericsMatrix4x4.Transpose(matrix));
        }

        private static Matrix4x4 ToCoreMatrix(NumericsMatrix4x4 matrix)
        {
            return new Matrix4x4(
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44);
        }

        private static Matrix4x4 ToCoreMatrixWithScaledTranslation(NumericsMatrix4x4 matrix, float globalScale)
        {
            Matrix4x4 result = ToCoreMatrix(matrix);
            result.M41 *= globalScale;
            result.M42 *= globalScale;
            result.M43 *= globalScale;
            return result;
        }

        private static unsafe Matrix4x4 BuildSkinningBindTransform(
            Node* meshNode,
            NumericsMatrix4x4 meshGlobalTransform,
            AssimpAnimationManifest animationManifest,
            float globalScale)
        {
            NumericsMatrix4x4 bindTransform = meshGlobalTransform;
            if (animationManifest.SkeletonRootNodeAddress != 0)
            {
                Node* skeletonRoot = (Node*)animationManifest.SkeletonRootNodeAddress;
                Node* commonAncestor = FindNearestCommonAncestor(meshNode, skeletonRoot);
                if (commonAncestor != null &&
                    NumericsMatrix4x4.Invert(GetAssimpNodeGlobalTransform(commonAncestor), out NumericsMatrix4x4 inverseCommonAncestor))
                {
                    bindTransform = meshGlobalTransform * inverseCommonAncestor;
                }
            }

            return ToCoreMatrixWithScaledTranslation(bindTransform, globalScale);
        }

        private static unsafe Node* FindNearestCommonAncestor(Node* first, Node* second)
        {
            var ancestors = new HashSet<nint>();
            for (Node* current = first; current != null; current = current->MParent)
                ancestors.Add((nint)current);

            for (Node* current = second; current != null; current = current->MParent)
            {
                if (ancestors.Contains((nint)current))
                    return current;
            }

            return null;
        }

        private static unsafe NumericsMatrix4x4 GetAssimpNodeGlobalTransform(Node* node)
        {
            NumericsMatrix4x4 global = NumericsMatrix4x4.Identity;
            for (Node* current = node; current != null; current = current->MParent)
                global *= ToEngineTransform(current->MTransformation);

            return global;
        }

        private static unsafe void AccumulateNodeMeshTotals(Scene* scene, Node* node, out int vertexCount, out int indexCount)
        {
            vertexCount = 0;
            indexCount = 0;
            AccumulateNodeMeshTotalsRecursive(scene, node, ref vertexCount, ref indexCount);
        }

        private static unsafe void AccumulateNodeMeshTotalsRecursive(Scene* scene, Node* node, ref int vertexCount, ref int indexCount)
        {
            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                Mesh* mesh = scene->MMeshes[node->MMeshes[i]];
                if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0)
                    continue;
                if ((PrimitiveType)mesh->MPrimitiveTypes != PrimitiveType.Triangle)
                    continue;

                vertexCount = checked(vertexCount + (int)mesh->MNumVertices);
                indexCount = checked(indexCount + (int)mesh->MNumFaces * 3);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
                AccumulateNodeMeshTotalsRecursive(scene, node->MChildren[i], ref vertexCount, ref indexCount);
        }

        private unsafe void ProcessNode(
            Scene* scene,
            Node* node,
            NumericsMatrix4x4 parentTransform,
            ImporterOptions options,
            ModelMesh model,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector3> tangents,
            List<Vector3> bitangents,
            List<Vector2> texCoords,
            List<Vector2> texCoords1,
            List<Vector4> vertexColors,
            List<VertexJointIndices> jointIndices,
            List<VertexJointWeights> jointWeights,
            List<uint> indices,
            AssimpAnimationManifest animationManifest)
        {
            NumericsMatrix4x4 nodeTransform = ToEngineTransform(node->MTransformation) * parentTransform;

            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                uint meshIndex = node->MMeshes[i];
                AppendMeshInstance(
                    scene->MMeshes[meshIndex],
                    node,
                    meshIndex,
                    nodeTransform,
                    options,
                    model,
                    vertices,
                    normals,
                    tangents,
                    bitangents,
                    texCoords,
                    texCoords1,
                    vertexColors,
                    jointIndices,
                    jointWeights,
                    indices,
                    animationManifest);
            }

            for (uint i = 0; i < node->MNumChildren; i++)
            {
                ProcessNode(
                    scene,
                    node->MChildren[i],
                    nodeTransform,
                    options,
                    model,
                    vertices,
                    normals,
                    tangents,
                    bitangents,
                    texCoords,
                    texCoords1,
                    vertexColors,
                    jointIndices,
                    jointWeights,
                    indices,
                    animationManifest);
            }
        }

        private unsafe void AppendMeshInstance(
            Mesh* aiMesh,
            Node* node,
            uint meshIndex,
            NumericsMatrix4x4 transform,
            ImporterOptions options,
            ModelMesh model,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector3> tangents,
            List<Vector3> bitangents,
            List<Vector2> texCoords,
            List<Vector2> texCoords1,
            List<Vector4> vertexColors,
            List<VertexJointIndices> jointIndices,
            List<VertexJointWeights> jointWeights,
            List<uint> indices,
            AssimpAnimationManifest animationManifest)
        {
            if ((PrimitiveType)aiMesh->MPrimitiveTypes != PrimitiveType.Triangle)
                throw new Exception("Mesh is not triangulated. Enable Triangulate post-process step.");

            if (aiMesh->MNumVertices == 0 || aiMesh->MNumFaces == 0)
                return;

            string nodeName = node->MName.AsString;
            string meshName = aiMesh->MName.AsString;
            bool isSkinned = aiMesh->MNumBones > 0 && animationManifest.Skins.Count > 0;
            NumericsMatrix4x4 vertexTransform = isSkinned ? NumericsMatrix4x4.Identity : transform;
            var subMesh = new ModelSubMesh
            {
                Name = CreateSubMeshName(model.Name, nodeName, meshName, meshIndex),
                MaterialIndex = aiMesh->MMaterialIndex < model.Materials.Count
                    ? (int)aiMesh->MMaterialIndex
                    : 0,
                NodeIndex = -1,
                SkinIndex = isSkinned ? 0 : -1,
                SkinningBindTransform = isSkinned
                    ? BuildSkinningBindTransform(node, transform, animationManifest, options.GlobalScale)
                    : Matrix4x4.Identity
            };

            int subVertexCapacity = checked((int)aiMesh->MNumVertices);
            int subIndexCapacity = checked((int)aiMesh->MNumFaces * 3);
            var subVertices = new List<Vector3>(subVertexCapacity);
            var subNormals = new List<Vector3>(subVertexCapacity);
            var subTangents = new List<Vector3>(subVertexCapacity);
            var subBitangents = new List<Vector3>(subVertexCapacity);
            var subTexCoords = new List<Vector2>(subVertexCapacity);
            var subTexCoords1 = new List<Vector2>(subVertexCapacity);
            var subVertexColors = new List<Vector4>(subVertexCapacity);
            var subIndices = new List<uint>(subIndexCapacity);

            BuildAssimpSkinningStreams(aiMesh, animationManifest, subMesh.Name, out VertexJointIndices[] meshJointIndices, out VertexJointWeights[] meshJointWeights);

            NumericsMatrix4x4 normalTransform = NumericsMatrix4x4.Invert(vertexTransform, out NumericsMatrix4x4 inverseTransform)
                ? NumericsMatrix4x4.Transpose(inverseTransform)
                : vertexTransform;

            for (uint v = 0; v < aiMesh->MNumVertices; v++)
            {
                var pos = aiMesh->MVertices[(int)v];
                var position = TransformPosition(new Vector3(pos.X, pos.Y, pos.Z), vertexTransform, options.GlobalScale);
                vertices.Add(position);
                subVertices.Add(position);

                var normal = aiMesh->MNormals != null ? aiMesh->MNormals[(int)v] : default;
                var normalValue = NormalizeOrDefault(TransformDirection(new Vector3(normal.X, normal.Y, normal.Z), normalTransform));
                normals.Add(normalValue);
                subNormals.Add(normalValue);

                var tangent = aiMesh->MTangents != null ? aiMesh->MTangents[(int)v] : default;
                var tangentValue = NormalizeOrDefault(TransformDirection(new Vector3(tangent.X, tangent.Y, tangent.Z), vertexTransform));
                tangents.Add(tangentValue);
                subTangents.Add(tangentValue);

                var bitangent = aiMesh->MBitangents != null ? aiMesh->MBitangents[(int)v] : default;
                var bitangentValue = NormalizeOrDefault(TransformDirection(new Vector3(bitangent.X, bitangent.Y, bitangent.Z), vertexTransform));
                bitangents.Add(bitangentValue);
                subBitangents.Add(bitangentValue);

                Vector3 tc = default;
                if (aiMesh->MTextureCoords[0] != null)
                {
                    var tcSrc = aiMesh->MTextureCoords[0][(int)v];
                    tc = new Vector3(tcSrc.X, tcSrc.Y, tcSrc.Z);
                }

                var texCoord = new Vector2(tc.X, tc.Y);
                texCoords.Add(texCoord);
                subTexCoords.Add(texCoord);

                Vector3 tc1 = default;
                if (aiMesh->MTextureCoords[1] != null)
                {
                    var tcSrc1 = aiMesh->MTextureCoords[1][(int)v];
                    tc1 = new Vector3(tcSrc1.X, tcSrc1.Y, tcSrc1.Z);
                }

                var texCoord1 = new Vector2(tc1.X, tc1.Y);
                texCoords1.Add(texCoord1);
                subTexCoords1.Add(texCoord1);

                Vector4 color = new(1f, 1f, 1f, 1f);
                if (aiMesh->MColors[0] != null)
                {
                    var colorSrc = aiMesh->MColors[0][(int)v];
                    color = new Vector4(colorSrc.X, colorSrc.Y, colorSrc.Z, colorSrc.W);
                }

                vertexColors.Add(color);
                subVertexColors.Add(color);

                VertexJointIndices vertexJoints = default;
                VertexJointWeights vertexWeights = default;
                if (isSkinned)
                {
                    vertexJoints = meshJointIndices[(int)v];
                    vertexWeights = meshJointWeights[(int)v];
                    ValidateSkinnedVertex(model.Name, subMesh.Name, subMesh.SkinIndex, model.Skins, (int)v, vertexJoints, vertexWeights);
                }

                jointIndices.Add(vertexJoints);
                jointWeights.Add(vertexWeights);
                subMesh.JointIndices0 = isSkinned ? meshJointIndices : Array.Empty<VertexJointIndices>();
                subMesh.JointWeights0 = isSkinned ? meshJointWeights : Array.Empty<VertexJointWeights>();
            }

            int baseVertex = vertices.Count - subVertices.Count;
            for (uint f = 0; f < aiMesh->MNumFaces; f++)
            {
                var face = aiMesh->MFaces[f];
                if (face.MNumIndices != 3)
                    throw new Exception("Face is not a triangle. Enable Triangulate post-process step.");

                if (options.FlipWindingOrder)
                {
                    indices.Add((uint)(baseVertex + face.MIndices[2]));
                    indices.Add((uint)(baseVertex + face.MIndices[1]));
                    indices.Add((uint)(baseVertex + face.MIndices[0]));
                    subIndices.Add(face.MIndices[2]);
                    subIndices.Add(face.MIndices[1]);
                    subIndices.Add(face.MIndices[0]);
                }
                else
                {
                    indices.Add((uint)(baseVertex + face.MIndices[0]));
                    indices.Add((uint)(baseVertex + face.MIndices[1]));
                    indices.Add((uint)(baseVertex + face.MIndices[2]));
                    subIndices.Add(face.MIndices[0]);
                    subIndices.Add(face.MIndices[1]);
                    subIndices.Add(face.MIndices[2]);
                }
            }

            subMesh.Vertices = subVertices.ToArray();
            subMesh.Normals = subNormals.ToArray();
            subMesh.Tangents = subTangents.ToArray();
            subMesh.Bitangents = subBitangents.ToArray();
            subMesh.TexCoords = subTexCoords.ToArray();
            subMesh.TexCoords1 = subTexCoords1.ToArray();
            subMesh.VertexColors = subVertexColors.ToArray();
            subMesh.Indices = subIndices.ToArray();
            ComputeBoundingVolume(subVertices, out var subBox, out var subSphere);
            subMesh.BoundingBox = subBox;
            subMesh.BoundingSphere = subSphere;
            model.SubMeshes.Add(subMesh);
        }

        private static string CreateSubMeshName(string modelName, string nodeName, string meshName, uint meshIndex)
        {
            if (!string.IsNullOrWhiteSpace(nodeName))
                return nodeName;
            if (!string.IsNullOrWhiteSpace(meshName))
                return meshName;

            return $"{modelName}_mesh_{meshIndex}";
        }

        private static NumericsMatrix4x4 ToEngineTransform(NumericsMatrix4x4 assimpTransform)
        {
            return NumericsMatrix4x4.Transpose(assimpTransform);
        }

        private unsafe List<ModelMaterial> ProcessMaterials(Scene* scene, string modelPath, GltfAssetManifest? gltfManifest)
        {
            var materials = new List<ModelMaterial>();
            string modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? AppContext.BaseDirectory;

            for (uint i = 0; i < scene->MNumMaterials; i++)
            {
                Material* material = scene->MMaterials[i];
                var imported = new ModelMaterial
                {
                    Name = GetMaterialString(material, Assimp.MaterialName, $"Material_{i}"),
                    Albedo = ToCoreVector(GetMaterialColor(material, Assimp.MaterialColorDiffuse, new NumericsVector4(1f, 1f, 1f, 1f))),
                    Emissive = ToCoreVector(GetMaterialColor(material, Assimp.MaterialColorEmissive, NumericsVector4.Zero)),
                    Metallic = GetMaterialFloat(material, 0f, "$mat.metallicFactor", "$mat.gltf.pbrMetallicRoughness.metallicFactor"),
                    Roughness = GetMaterialFloat(material, 1f, "$mat.roughnessFactor", "$mat.gltf.pbrMetallicRoughness.roughnessFactor"),
                    AmbientOcclusion = 1f,
                    NormalScale = GetMaterialFloat(material, 1f, "$mat.normalScale"),
                    AlbedoTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.BaseColor, TextureType.Diffuse), "base-color"),
                    NormalTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.NormalCamera, TextureType.Normals, TextureType.Height), "normal"),
                    MetallicRoughnessTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.Metalness, TextureType.DiffuseRoughness, TextureType.AmbientOcclusion, TextureType.Unknown), "metallic-roughness/occlusion"),
                    EmissiveTexturePath = ResolveTexturePath(modelDirectory, GetFirstTexturePath(material, TextureType.EmissionColor, TextureType.Emissive), "emissive")
                };

                if (gltfManifest != null && i < gltfManifest.Materials.Count)
                    ApplyGltfMaterial(imported, gltfManifest.Materials[(int)i]);

                materials.Add(imported);
            }

            if (materials.Count == 0)
                materials.Add(ModelMaterial.Default);

            return materials;
        }

        private unsafe string GetMaterialString(Material* material, string key, string fallback)
        {
            AssimpString value = default;
            return _assimp.GetMaterialString(material, key, 0, 0, &value) == Return.Success &&
                   !string.IsNullOrWhiteSpace(value.AsString)
                ? value.AsString
                : fallback;
        }

        private unsafe NumericsVector4 GetMaterialColor(Material* material, string key, NumericsVector4 fallback)
        {
            NumericsVector4 value = fallback;
            return _assimp.GetMaterialColor(material, key, 0, 0, &value) == Return.Success
                ? value
                : fallback;
        }

        private unsafe float GetMaterialFloat(Material* material, float fallback, params string[] keys)
        {
            foreach (string key in keys)
            {
                float value = fallback;
                uint count = 1;
                if (_assimp.GetMaterialFloatArray(material, key, 0, 0, &value, &count) == Return.Success && count > 0)
                    return value;
            }

            return fallback;
        }

        private unsafe string? GetFirstTexturePath(Material* material, params TextureType[] textureTypes)
        {
            foreach (TextureType textureType in textureTypes.Distinct())
            {
                if (_assimp.GetMaterialTextureCount(material, textureType) == 0)
                    continue;

                AssimpString path = default;
                Return result = _assimp.GetMaterialTexture(
                    material,
                    textureType,
                    0,
                    &path,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);

                if (result == Return.Success && !string.IsNullOrWhiteSpace(path.AsString))
                    return path.AsString;
            }

            return default;
        }

        private static string? ResolveTexturePath(string modelDirectory, string? texturePath, string textureRole)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return default;

            if (texturePath.StartsWith("*", StringComparison.Ordinal))
                return default;

            if (texturePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return default;

            string decodedPath = Uri.UnescapeDataString(texturePath.Replace('\\', Path.DirectorySeparatorChar));
            return Path.IsPathRooted(decodedPath)
                ? Path.GetFullPath(decodedPath)
                : Path.GetFullPath(Path.Combine(modelDirectory, decodedPath));
        }

        private static void ApplyGltfMaterial(ModelMaterial target, GltfMaterial material)
        {
            target.Name = string.IsNullOrWhiteSpace(material.Name) ? target.Name : material.Name;
            target.Albedo = material.BaseColorFactor ?? target.Albedo;
            target.Emissive = material.EmissiveFactor ?? target.Emissive;
            target.Metallic = material.MetallicFactor ?? target.Metallic;
            target.Roughness = material.RoughnessFactor ?? target.Roughness;
            target.AmbientOcclusion = material.OcclusionStrength ?? target.AmbientOcclusion;
            target.NormalScale = material.NormalScale ?? target.NormalScale;
            target.AlphaMode = material.AlphaMode;
            target.AlphaCutoff = material.AlphaCutoff ?? target.AlphaCutoff;
            target.DoubleSided = material.DoubleSided;
            target.AlbedoTexturePath = material.BaseColorTexturePath ?? target.AlbedoTexturePath;
            target.NormalTexturePath = material.NormalTexturePath ?? target.NormalTexturePath;
            target.MetallicRoughnessTexturePath = material.MetallicRoughnessTexturePath ?? target.MetallicRoughnessTexturePath;
            target.OcclusionTexturePath = material.OcclusionTexturePath ?? target.OcclusionTexturePath;
            target.EmissiveTexturePath = material.EmissiveTexturePath ?? target.EmissiveTexturePath;
            target.BaseColorTexture = material.BaseColorTexture ?? target.BaseColorTexture;
            target.NormalTexture = material.NormalTexture ?? target.NormalTexture;
            target.MetallicRoughnessTexture = material.MetallicRoughnessTexture ?? target.MetallicRoughnessTexture;
            target.OcclusionTexture = material.OcclusionTexture ?? target.OcclusionTexture;
            target.EmissiveTexture = material.EmissiveTexture ?? target.EmissiveTexture;
        }

        private static GltfAssetManifest? LoadAndValidateGltfManifest(string modelPath)
        {
            string extension = Path.GetExtension(modelPath);
            if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                using FileStream stream = File.OpenRead(modelPath);
                using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                string modelDirectory = Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory;
                return BuildGltfManifest(document.RootElement, modelPath, modelDirectory, binaryChunk: null);
            }

            if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
            {
                GlbPayload payload = ReadGlb(modelPath);
                using JsonDocument document = JsonDocument.Parse(payload.Json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                string modelDirectory = Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory;
                return BuildGltfManifest(document.RootElement, modelPath, modelDirectory, payload.BinaryChunk);
            }

            return default;
        }

        private static GltfAssetManifest BuildGltfManifest(
            JsonElement root,
            string modelPath,
            string modelDirectory,
            byte[]? binaryChunk)
        {
            var diagnostics = new AssetImportDiagnostics();
            if (binaryChunk == null)
                diagnostics.ImportedGltfCount = 1;
            else
                diagnostics.ImportedGlbCount = 1;

            ValidateGltfExtensions(root, modelPath, diagnostics);
            List<GltfBufferData> buffers = LoadGltfBuffers(root, modelPath, modelDirectory, binaryChunk, diagnostics);
            List<GltfBufferViewData> bufferViews = ReadGltfBufferViews(root);
            List<ModelTextureSource?> imageSources = ReadGltfImages(root, modelPath, modelDirectory, buffers, bufferViews, diagnostics);
            List<GltfTexture> textureSources = ReadGltfTextures(root);
            List<TextureSamplerDescription> samplers = ReadGltfSamplers(root, diagnostics);

            var materials = new List<GltfMaterial>();
            if (root.TryGetProperty("materials", out JsonElement materialsElement) &&
                materialsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement materialElement in materialsElement.EnumerateArray())
                    materials.Add(ReadGltfMaterial(materialElement, textureSources, imageSources, samplers));
            }

            return new GltfAssetManifest(materials, diagnostics);
        }

        private static void ValidateGltfExtensions(JsonElement root, string modelPath, AssetImportDiagnostics diagnostics)
        {
            var supported = new HashSet<string>(StringComparer.Ordinal)
            {
                "KHR_texture_transform",
                "KHR_materials_emissive_strength",
                "KHR_texture_basisu"
            };
            var optionalWarn = new HashSet<string>(StringComparer.Ordinal)
            {
                "KHR_materials_clearcoat",
                "KHR_materials_sheen",
                "KHR_materials_transmission",
                "KHR_materials_ior",
                "KHR_materials_volume",
                "KHR_materials_specular",
                "KHR_materials_iridescence",
                "KHR_materials_anisotropy",
                "KHR_materials_unlit",
                "KHR_materials_pbrSpecularGlossiness",
                "EXT_meshopt_compression",
                "KHR_draco_mesh_compression",
                "MSFT_texture_dds"
            };

            if (root.TryGetProperty("extensionsRequired", out JsonElement required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement extensionElement in required.EnumerateArray())
                {
                    string? extension = extensionElement.GetString();
                    if (string.IsNullOrWhiteSpace(extension) || supported.Contains(extension))
                        continue;

                    diagnostics.UnsupportedRequiredExtensionCount++;
                    diagnostics.Add(
                        AssetImportSeverity.Error,
                        AssetImportMessageCode.UnsupportedRequiredExtension,
                        modelPath,
                        "/extensionsRequired",
                        $"glTF asset requires unsupported extension '{extension}'.");
                    throw new NotSupportedException($"glTF asset '{modelPath}' requires unsupported extension '{extension}'.");
                }
            }

            if (root.TryGetProperty("extensionsUsed", out JsonElement used) &&
                used.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement extensionElement in used.EnumerateArray())
                {
                    string? extension = extensionElement.GetString();
                    if (string.IsNullOrWhiteSpace(extension) || supported.Contains(extension) || !optionalWarn.Contains(extension))
                        continue;

                    diagnostics.UnsupportedOptionalExtensionCount++;
                    diagnostics.Add(
                        AssetImportSeverity.Warning,
                        AssetImportMessageCode.UnsupportedOptionalExtension,
                        modelPath,
                        "/extensionsUsed",
                        $"glTF asset uses optional extension '{extension}' that is not rendered by this phase.");
                }
            }
        }

        private static List<GltfBufferData> LoadGltfBuffers(
            JsonElement root,
            string modelPath,
            string modelDirectory,
            byte[]? binaryChunk,
            AssetImportDiagnostics diagnostics)
        {
            var buffers = new List<GltfBufferData>();
            if (!root.TryGetProperty("buffers", out JsonElement buffersElement) ||
                buffersElement.ValueKind != JsonValueKind.Array)
                return buffers;

            int index = 0;
            foreach (JsonElement bufferElement in buffersElement.EnumerateArray())
            {
                int declaredLength = ReadRequiredInt(bufferElement, "byteLength", $"buffer {index}");
                byte[] data;
                if (bufferElement.TryGetProperty("uri", out JsonElement uriElement) &&
                    uriElement.ValueKind == JsonValueKind.String)
                {
                    string uri = uriElement.GetString() ?? string.Empty;
                    if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        data = DecodeDataUri(uri, out _);
                        diagnostics.DataUriBufferCount++;
                    }
                    else
                    {
                        string absolutePath = ResolveExternalGltfPath(modelDirectory, uri);
                        if (!File.Exists(absolutePath))
                        {
                            diagnostics.Add(
                                AssetImportSeverity.Error,
                                AssetImportMessageCode.MissingExternalBuffer,
                                modelPath,
                                $"/buffers/{index}/uri",
                                $"Required external glTF buffer was not found: {absolutePath}");
                            throw new FileNotFoundException($"Required external glTF buffer was not found: {absolutePath}", absolutePath);
                        }

                        data = File.ReadAllBytes(absolutePath);
                        diagnostics.ExternalBufferCount++;
                    }
                }
                else
                {
                    if (binaryChunk == null)
                        throw new InvalidDataException($"glTF buffer {index} in '{modelPath}' has no URI and no GLB BIN chunk is available.");

                    data = binaryChunk;
                    diagnostics.EmbeddedBufferCount++;
                }

                if (data.Length < declaredLength)
                    throw new InvalidDataException($"glTF buffer {index} in '{modelPath}' declares {declaredLength} bytes, but only {data.Length} bytes are available.");

                buffers.Add(new GltfBufferData(data, declaredLength));
                index++;
            }

            return buffers;
        }

        private static GlbPayload ReadGlb(string modelPath)
        {
            byte[] data = File.ReadAllBytes(modelPath);
            if (data.Length < 20)
                throw new InvalidDataException($"glB asset '{modelPath}' is too small to contain a valid header.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
            uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
            if (magic != 0x46546C67u)
                throw new InvalidDataException($"glB asset '{modelPath}' has an invalid magic value.");
            if (version != 2u)
                throw new NotSupportedException($"glB asset '{modelPath}' uses version {version}; only glB 2.0 is supported.");
            if (declaredLength > data.Length)
                throw new InvalidDataException($"glB asset '{modelPath}' declares {declaredLength} bytes but the file contains {data.Length} bytes.");

            byte[] jsonBytes = Array.Empty<byte>();
            byte[]? binaryChunk = null;
            int offset = 12;
            while (offset + 8 <= declaredLength)
            {
                int chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
                uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
                offset += 8;
                if (chunkLength < 0 || offset + chunkLength > declaredLength)
                    throw new InvalidDataException($"glB asset '{modelPath}' contains a chunk that extends past the declared file length.");

                byte[] chunk = data.AsSpan(offset, chunkLength).ToArray();
                if (chunkType == 0x4E4F534Au)
                    jsonBytes = TrimJsonPadding(chunk);
                else if (chunkType == 0x004E4942u)
                    binaryChunk = chunk;

                offset += chunkLength;
            }

            if (jsonBytes.Length == 0)
                throw new InvalidDataException($"glB asset '{modelPath}' does not contain a JSON chunk.");

            return new GlbPayload(jsonBytes, binaryChunk);
        }

        private static byte[] TrimJsonPadding(byte[] jsonChunk)
        {
            int length = jsonChunk.Length;
            while (length > 0 && (jsonChunk[length - 1] == 0x20 || jsonChunk[length - 1] == 0x00))
                length--;

            if (length == jsonChunk.Length)
                return jsonChunk;

            byte[] trimmed = new byte[length];
            Array.Copy(jsonChunk, trimmed, length);
            return trimmed;
        }

        private static List<GltfBufferViewData> ReadGltfBufferViews(JsonElement root)
        {
            var bufferViews = new List<GltfBufferViewData>();
            if (!root.TryGetProperty("bufferViews", out JsonElement viewsElement) ||
                viewsElement.ValueKind != JsonValueKind.Array)
                return bufferViews;

            foreach (JsonElement viewElement in viewsElement.EnumerateArray())
            {
                int buffer = ReadRequiredInt(viewElement, "buffer", "bufferView");
                int byteOffset = ReadInt(viewElement, "byteOffset") ?? 0;
                int byteLength = ReadRequiredInt(viewElement, "byteLength", "bufferView");
                bufferViews.Add(new GltfBufferViewData(buffer, byteOffset, byteLength));
            }

            return bufferViews;
        }

        private static List<ModelTextureSource?> ReadGltfImages(
            JsonElement root,
            string modelPath,
            string modelDirectory,
            IReadOnlyList<GltfBufferData> buffers,
            IReadOnlyList<GltfBufferViewData> bufferViews,
            AssetImportDiagnostics diagnostics)
        {
            var imageSources = new List<ModelTextureSource?>();
            if (!root.TryGetProperty("images", out JsonElement imagesElement) ||
                imagesElement.ValueKind != JsonValueKind.Array)
                return imageSources;

            int index = 0;
            foreach (JsonElement imageElement in imagesElement.EnumerateArray())
            {
                if (imageElement.TryGetProperty("bufferView", out _))
                {
                    int bufferViewIndex = ReadRequiredInt(imageElement, "bufferView", $"image {index}");
                    if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count)
                        throw new InvalidDataException($"glTF image {index} in '{modelPath}' references invalid bufferView {bufferViewIndex}.");

                    GltfBufferViewData view = bufferViews[bufferViewIndex];
                    byte[] bytes = SliceBufferView(buffers, view, modelPath, $"/images/{index}/bufferView");
                    string? mimeType = ReadString(imageElement, "mimeType");
                    imageSources.Add(new ModelTextureSource
                    {
                        DebugName = ReadString(imageElement, "name") ?? $"image_{index}",
                        Bytes = bytes,
                        MimeType = mimeType,
                        CacheIdentity = $"{Path.GetFullPath(modelPath)}#image:{index}:bufferView:{bufferViewIndex}"
                    });
                    diagnostics.EmbeddedImageCount++;
                    diagnostics.BufferViewImageCount++;
                    index++;
                    continue;
                }

                if (!imageElement.TryGetProperty("uri", out JsonElement uriElement) ||
                    uriElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"glTF image {index} in '{modelPath}' is missing both uri and bufferView.");
                }

                string uri = uriElement.GetString() ?? string.Empty;
                string debugName = ReadString(imageElement, "name") ?? $"image_{index}";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] bytes = DecodeDataUri(uri, out string? mimeType);
                    imageSources.Add(new ModelTextureSource
                    {
                        DebugName = debugName,
                        Bytes = bytes,
                        MimeType = mimeType,
                        CacheIdentity = $"{Path.GetFullPath(modelPath)}#image:{index}:data"
                    });
                    diagnostics.EmbeddedImageCount++;
                    diagnostics.DataUriImageCount++;
                    index++;
                    continue;
                }

                string absolutePath = ResolveExternalGltfPath(modelDirectory, uri);
                if (!File.Exists(absolutePath))
                {
                    diagnostics.Add(
                        AssetImportSeverity.Error,
                        AssetImportMessageCode.MissingExternalImage,
                        modelPath,
                        $"/images/{index}/uri",
                        $"Required external glTF image was not found: {absolutePath}");
                    throw new FileNotFoundException($"Required external glTF image was not found: {absolutePath}", absolutePath);
                }

                imageSources.Add(new ModelTextureSource
                {
                    DebugName = debugName,
                    FilePath = absolutePath,
                    CacheIdentity = Path.GetFullPath(absolutePath)
                });
                diagnostics.ExternalImageCount++;
                index++;
            }

            return imageSources;
        }

        private static List<GltfTexture> ReadGltfTextures(JsonElement root)
        {
            var textures = new List<GltfTexture>();
            if (!root.TryGetProperty("textures", out JsonElement texturesElement) ||
                texturesElement.ValueKind != JsonValueKind.Array)
                return textures;

            foreach (JsonElement textureElement in texturesElement.EnumerateArray())
            {
                textures.Add(new GltfTexture(
                    ReadInt(textureElement, "source"),
                    ReadInt(textureElement, "sampler")));
            }

            return textures;
        }

        private static List<TextureSamplerDescription> ReadGltfSamplers(JsonElement root, AssetImportDiagnostics diagnostics)
        {
            var samplers = new List<TextureSamplerDescription>();
            if (!root.TryGetProperty("samplers", out JsonElement samplersElement) ||
                samplersElement.ValueKind != JsonValueKind.Array)
                return samplers;

            foreach (JsonElement samplerElement in samplersElement.EnumerateArray())
            {
                TextureWrapMode wrapS = MapWrapMode(ReadInt(samplerElement, "wrapS") ?? 10497);
                TextureWrapMode wrapT = MapWrapMode(ReadInt(samplerElement, "wrapT") ?? 10497);
                int? minFilterValue = ReadInt(samplerElement, "minFilter");
                int? magFilterValue = ReadInt(samplerElement, "magFilter");
                samplers.Add(new TextureSamplerDescription(
                    wrapS,
                    wrapT,
                    MapMinFilter(minFilterValue),
                    MapMagFilter(magFilterValue),
                    MapMipFilter(minFilterValue),
                    16f));
                diagnostics.ImportedSamplerCount++;
            }

            return samplers;
        }

        private static TextureWrapMode MapWrapMode(int value)
        {
            return value switch
            {
                33071 => TextureWrapMode.ClampToEdge,
                33648 => TextureWrapMode.MirroredRepeat,
                _ => TextureWrapMode.Repeat
            };
        }

        private static TextureFilterMode MapMinFilter(int? value)
        {
            return value switch
            {
                9728 or 9984 or 9986 => TextureFilterMode.Nearest,
                _ => TextureFilterMode.Linear
            };
        }

        private static TextureFilterMode MapMagFilter(int? value)
        {
            return value == 9728 ? TextureFilterMode.Nearest : TextureFilterMode.Linear;
        }

        private static TextureMipFilterMode MapMipFilter(int? value)
        {
            return value switch
            {
                9984 or 9985 => TextureMipFilterMode.Nearest,
                _ => TextureMipFilterMode.Linear
            };
        }

        private static byte[] SliceBufferView(
            IReadOnlyList<GltfBufferData> buffers,
            GltfBufferViewData view,
            string modelPath,
            string source)
        {
            if (view.Buffer < 0 || view.Buffer >= buffers.Count)
                throw new InvalidDataException($"glTF asset '{modelPath}' has invalid buffer index {view.Buffer} at {source}.");

            byte[] buffer = buffers[view.Buffer].Bytes;
            if (view.ByteOffset < 0 || view.ByteLength < 0 || view.ByteOffset + view.ByteLength > buffer.Length)
                throw new InvalidDataException($"glTF asset '{modelPath}' has an out-of-bounds bufferView at {source}.");

            byte[] result = new byte[view.ByteLength];
            Array.Copy(buffer, view.ByteOffset, result, 0, view.ByteLength);
            return result;
        }

        private static byte[] DecodeDataUri(string uri, out string? mimeType)
        {
            int comma = uri.IndexOf(',');
            if (comma < 0)
                throw new InvalidDataException("Malformed glTF data URI.");

            string header = uri[..comma];
            string payload = uri[(comma + 1)..];
            mimeType = null;
            if (header.Length > "data:".Length)
            {
                string metadata = header["data:".Length..];
                int semicolon = metadata.IndexOf(';');
                mimeType = semicolon >= 0 ? metadata[..semicolon] : metadata;
            }

            if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(payload);

            return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

        private static int ReadRequiredInt(JsonElement element, string propertyName, string source)
        {
            return ReadInt(element, propertyName)
                   ?? throw new InvalidDataException($"glTF {source} is missing required integer property '{propertyName}'.");
        }

        private static int? ReadInt(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int result)
                ? result
                : null;
        }

        private static Matrix4x4 ExtractRotationMatrix(Matrix4x4 matrix)
        {
            Vector3 x = NormalizeOrDefault(new Vector3(matrix.M11, matrix.M12, matrix.M13));
            Vector3 y = NormalizeOrDefault(new Vector3(matrix.M21, matrix.M22, matrix.M23));
            Vector3 z = NormalizeOrDefault(new Vector3(matrix.M31, matrix.M32, matrix.M33));

            return new Matrix4x4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        private static AnimationTransform ToAnimationTransform(Matrix4x4 matrix)
        {
            return new AnimationTransform(matrix.Translation, Quaternion.FromMatrix4x4(matrix), matrix.Scale);
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static GltfMaterial ReadGltfMaterial(
            JsonElement materialElement,
            IReadOnlyList<GltfTexture> textures,
            IReadOnlyList<ModelTextureSource?> imageSources,
            IReadOnlyList<TextureSamplerDescription> samplers)
        {
            string? name = materialElement.TryGetProperty("name", out JsonElement nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            var material = new GltfMaterial
            {
                Name = name,
                AlphaMode = ReadAlphaMode(materialElement),
                AlphaCutoff = ReadFloat(materialElement, "alphaCutoff"),
                DoubleSided = ReadBool(materialElement, "doubleSided")
            };

            if (materialElement.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) &&
                pbr.ValueKind == JsonValueKind.Object)
            {
                material.BaseColorFactor = ReadVector4(pbr, "baseColorFactor");
                material.MetallicFactor = ReadFloat(pbr, "metallicFactor");
                material.RoughnessFactor = ReadFloat(pbr, "roughnessFactor");
                material.BaseColorTexture = ReadTextureSlot(pbr, "baseColorTexture", textures, imageSources, samplers, TextureColorSpace.Srgb);
                material.MetallicRoughnessTexture = ReadTextureSlot(pbr, "metallicRoughnessTexture", textures, imageSources, samplers, TextureColorSpace.Linear);
                material.BaseColorTexturePath = material.BaseColorTexture?.Source?.FilePath;
                material.MetallicRoughnessTexturePath = material.MetallicRoughnessTexture?.Source?.FilePath;
            }

            material.NormalTexture = ReadTextureSlot(materialElement, "normalTexture", textures, imageSources, samplers, TextureColorSpace.Linear);
            material.EmissiveTexture = ReadTextureSlot(materialElement, "emissiveTexture", textures, imageSources, samplers, TextureColorSpace.Srgb);
            material.OcclusionTexture = ReadTextureSlot(materialElement, "occlusionTexture", textures, imageSources, samplers, TextureColorSpace.Linear);
            material.NormalTexturePath = material.NormalTexture?.Source?.FilePath;
            material.EmissiveTexturePath = material.EmissiveTexture?.Source?.FilePath;
            material.OcclusionTexturePath = material.OcclusionTexture?.Source?.FilePath;
            material.NormalScale = ReadNestedFloat(materialElement, "normalTexture", "scale");
            material.OcclusionStrength = ReadNestedFloat(materialElement, "occlusionTexture", "strength");
            material.EmissiveFactor = ReadVector3AsColor(materialElement, "emissiveFactor");

            return material;
        }

        private static ModelTextureSlot? ReadTextureSlot(
            JsonElement owner,
            string propertyName,
            IReadOnlyList<GltfTexture> textures,
            IReadOnlyList<ModelTextureSource?> imageSources,
            IReadOnlyList<TextureSamplerDescription> samplers,
            TextureColorSpace colorSpace)
        {
            if (!owner.TryGetProperty(propertyName, out JsonElement textureInfo) ||
                textureInfo.ValueKind != JsonValueKind.Object ||
                !textureInfo.TryGetProperty("index", out JsonElement indexElement) ||
                !indexElement.TryGetInt32(out int textureIndex) ||
                textureIndex < 0 ||
                textureIndex >= textures.Count)
                return default;

            GltfTexture texture = textures[textureIndex];
            if (!texture.Source.HasValue || texture.Source.Value < 0 || texture.Source.Value >= imageSources.Count)
                return default;

            ModelTextureSource? source = imageSources[texture.Source.Value];
            if (source == null)
                return default;

            TextureSamplerDescription sampler = TextureSamplerDescription.Default;
            if (texture.Sampler.HasValue && texture.Sampler.Value >= 0 && texture.Sampler.Value < samplers.Count)
                sampler = samplers[texture.Sampler.Value];

            int texCoord = ReadInt(textureInfo, "texCoord") ?? 0;
            Vector2 offset = Vector2.Zero;
            Vector2 scale = new(1f, 1f);
            float rotation = 0f;
            if (textureInfo.TryGetProperty("extensions", out JsonElement extensions) &&
                extensions.ValueKind == JsonValueKind.Object &&
                extensions.TryGetProperty("KHR_texture_transform", out JsonElement transform) &&
                transform.ValueKind == JsonValueKind.Object)
            {
                offset = ReadVector2(transform, "offset") ?? offset;
                scale = ReadVector2(transform, "scale") ?? scale;
                rotation = ReadFloat(transform, "rotation") ?? rotation;
                texCoord = ReadInt(transform, "texCoord") ?? texCoord;
            }

            return new ModelTextureSlot
            {
                Source = source,
                Sampler = sampler,
                ColorSpace = colorSpace,
                TexCoordSet = Math.Clamp(texCoord, 0, 1),
                Offset = offset,
                Scale = scale,
                RotationRadians = rotation
            };
        }

        private static ModelAlphaMode ReadAlphaMode(JsonElement materialElement)
        {
            if (!materialElement.TryGetProperty("alphaMode", out JsonElement alphaModeElement) ||
                alphaModeElement.ValueKind != JsonValueKind.String)
                return ModelAlphaMode.Opaque;

            return alphaModeElement.GetString() switch
            {
                "MASK" => ModelAlphaMode.Mask,
                "BLEND" => ModelAlphaMode.Blend,
                _ => ModelAlphaMode.Opaque
            };
        }

        private static bool ReadBool(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.True;
        }

        private static float? ReadFloat(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetSingle(out float result)
                ? result
                : null;
        }

        private static float? ReadNestedFloat(JsonElement owner, string objectName, string propertyName)
        {
            return owner.TryGetProperty(objectName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                ? ReadFloat(nested, propertyName)
                : null;
        }

        private static Vector4? ReadVector4(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() != 4)
                return default;

            float[] values = array.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        private static Vector2? ReadVector2(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() != 2)
                return default;

            float[] values = array.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Vector2(values[0], values[1]);
        }

        private static Vector4? ReadVector3AsColor(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() != 3)
                return default;

            float[] values = array.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Vector4(values[0], values[1], values[2], 1f);
        }

        private static string ResolveExternalGltfPath(string modelDirectory, string uri)
        {
            string decodedPath = Uri.UnescapeDataString(uri).Replace('\\', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(decodedPath)
                ? Path.GetFullPath(decodedPath)
                : Path.GetFullPath(Path.Combine(modelDirectory, decodedPath));
        }

        private static Vector4 ToCoreVector(NumericsVector4 value)
        {
            return new Vector4(value.X, value.Y, value.Z, value.W);
        }

        private static Vector3 TransformPosition(Vector3 position, NumericsMatrix4x4 transform, float globalScale)
        {
            return new Vector3(
                (position.X * transform.M11 + position.Y * transform.M21 + position.Z * transform.M31 + transform.M41) * globalScale,
                (position.X * transform.M12 + position.Y * transform.M22 + position.Z * transform.M32 + transform.M42) * globalScale,
                (position.X * transform.M13 + position.Y * transform.M23 + position.Z * transform.M33 + transform.M43) * globalScale);
        }

        private static Vector3 TransformDirection(Vector3 direction, NumericsMatrix4x4 transform)
        {
            return new Vector3(
                direction.X * transform.M11 + direction.Y * transform.M21 + direction.Z * transform.M31,
                direction.X * transform.M12 + direction.Y * transform.M22 + direction.Z * transform.M32,
                direction.X * transform.M13 + direction.Y * transform.M23 + direction.Z * transform.M33);
        }

        private static Vector3 TransformDirection(Vector3 direction, Matrix4x4 transform)
        {
            return new Vector3(
                direction.X * transform.M11 + direction.Y * transform.M21 + direction.Z * transform.M31,
                direction.X * transform.M12 + direction.Y * transform.M22 + direction.Z * transform.M32,
                direction.X * transform.M13 + direction.Y * transform.M23 + direction.Z * transform.M33);
        }

        private static Vector3 NormalizeOrDefault(Vector3 value)
        {
            float lengthSquared = value.LengthSquared();
            if (lengthSquared <= float.Epsilon)
                return Vector3.Zero;

            return value / (float)System.Math.Sqrt(lengthSquared);
        }

        private static void ValidateSkinnedVertex(
            string modelName,
            string subMeshName,
            int skinIndex,
            IReadOnlyList<Skin> skins,
            int vertexIndex,
            VertexJointIndices jointIndices,
            VertexJointWeights jointWeights)
        {
            if (skinIndex < 0 || skinIndex >= skins.Count)
            {
                throw new InvalidDataException(
                    $"Skinned submesh '{subMeshName}' in model '{modelName}' references skin {skinIndex}, but the model has {skins.Count} skins.");
            }

            if (jointWeights.X < 0f || jointWeights.Y < 0f || jointWeights.Z < 0f || jointWeights.W < 0f)
            {
                throw new InvalidDataException(
                    $"Skinned submesh '{subMeshName}' in model '{modelName}' has negative weights at vertex {vertexIndex}.");
            }

            if (jointWeights.Sum <= float.Epsilon)
            {
                throw new InvalidDataException(
                    $"Skinned submesh '{subMeshName}' in model '{modelName}' has zero total skin weight at vertex {vertexIndex}.");
            }

            int jointCount = skins[skinIndex].JointIndices.Count;
            for (int i = 0; i < 4; i++)
            {
                if (jointWeights[i] > 0f && jointIndices[i] >= jointCount)
                {
                    throw new InvalidDataException(
                        $"Skinned submesh '{subMeshName}' in model '{modelName}' vertex {vertexIndex} references joint {jointIndices[i]}, " +
                        $"but skin {skinIndex} has {jointCount} joints.");
                }
            }
        }

        private static void ComputeBoundingVolume(List<Vector3> vertices, out BoundingBox bbox, out BoundingSphere bsphere)
        {
            if (vertices.Count == 0)
            {
                bbox = new BoundingBox();
                bsphere = new BoundingSphere();
                return;
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = System.Math.Min(min.X, v.X);
                min.Y = System.Math.Min(min.Y, v.Y);
                min.Z = System.Math.Min(min.Z, v.Z);
                max.X = System.Math.Max(max.X, v.X);
                max.Y = System.Math.Max(max.Y, v.Y);
                max.Z = System.Math.Max(max.Z, v.Z);
            }

            bbox = new BoundingBox(min, max);
            bsphere = BoundingSphere.FromBox(bbox);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }
    }

    public class ModelMesh
    {
        public string Name { get; set; } = "ModelMesh";
        public Vector3[] Vertices { get; set; }
        public Vector3[] Normals { get; set; }
        public Vector3[] Tangents { get; set; }
        public Vector3[] Bitangents { get; set; }
        public Vector2[] TexCoords { get; set; }
        public Vector2[] TexCoords1 { get; set; }
        public Vector4[] VertexColors { get; set; }
        public VertexJointIndices[] JointIndices0 { get; set; }
        public VertexJointWeights[] JointWeights0 { get; set; }
        public uint[] Indices { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        public List<ModelSubMesh> SubMeshes { get; } = new();
        public List<ModelMaterial> Materials { get; } = new();
        public List<CoreSkeleton> Skeletons { get; } = new();
        public List<Skin> Skins { get; } = new();
        public List<AnimationClip> AnimationClips { get; } = new();
        public ModelAnimationImportDiagnostics AnimationDiagnostics { get; set; } = ModelAnimationImportDiagnostics.Empty;
        public AssetImportDiagnostics ImportDiagnostics { get; set; } = new();

        public ModelMesh()
        {
            Vertices = Array.Empty<Vector3>();
            Normals = Array.Empty<Vector3>();
            Tangents = Array.Empty<Vector3>();
            Bitangents = Array.Empty<Vector3>();
            TexCoords = Array.Empty<Vector2>();
            TexCoords1 = Array.Empty<Vector2>();
            VertexColors = Array.Empty<Vector4>();
            JointIndices0 = Array.Empty<VertexJointIndices>();
            JointWeights0 = Array.Empty<VertexJointWeights>();
            Indices = Array.Empty<uint>();
        }
    }

    public sealed class ModelSubMesh
    {
        public string Name { get; set; } = "SubMesh";
        public int MaterialIndex { get; set; }
        public int NodeIndex { get; set; } = -1;
        public int SkinIndex { get; set; } = -1;
        public Matrix4x4 SkinningBindTransform { get; set; } = Matrix4x4.Identity;
        public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Tangents { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Bitangents { get; set; } = Array.Empty<Vector3>();
        public Vector2[] TexCoords { get; set; } = Array.Empty<Vector2>();
        public Vector2[] TexCoords1 { get; set; } = Array.Empty<Vector2>();
        public Vector4[] VertexColors { get; set; } = Array.Empty<Vector4>();
        public VertexJointIndices[] JointIndices0 { get; set; } = Array.Empty<VertexJointIndices>();
        public VertexJointWeights[] JointWeights0 { get; set; } = Array.Empty<VertexJointWeights>();
        public uint[] Indices { get; set; } = Array.Empty<uint>();
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
    }

    public sealed class ModelMaterial
    {
        public static ModelMaterial Default => new ModelMaterial();

        public string Name { get; set; } = "DefaultMaterial";
        public Vector4 Albedo { get; set; } = new Vector4(1f, 1f, 1f, 1f);
        public Vector4 Emissive { get; set; } = Vector4.Zero;
        public float Metallic { get; set; } = 0f;
        public float Roughness { get; set; } = 1f;
        public float AmbientOcclusion { get; set; } = 1f;
        public float NormalScale { get; set; } = 1f;
        public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
        public float AlphaCutoff { get; set; } = 0.5f;
        public bool DoubleSided { get; set; }
        public bool IsGeometryDecal { get; set; }
        public int DecalLayer { get; set; }
        public float DecalDepthBias { get; set; }
        public string? AlbedoTexturePath { get; set; }
        public string? NormalTexturePath { get; set; }
        public string? MetallicRoughnessTexturePath { get; set; }
        public string? OcclusionTexturePath { get; set; }
        public string? EmissiveTexturePath { get; set; }
        public ModelTextureSlot? BaseColorTexture { get; set; }
        public ModelTextureSlot? NormalTexture { get; set; }
        public ModelTextureSlot? MetallicRoughnessTexture { get; set; }
        public ModelTextureSlot? OcclusionTexture { get; set; }
        public ModelTextureSlot? EmissiveTexture { get; set; }
    }

    public enum ModelAlphaMode
    {
        Opaque,
        Mask,
        Blend
    }

    public sealed class ModelAnimationImportDiagnostics
    {
        internal static ModelAnimationImportDiagnostics Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

        internal ModelAnimationImportDiagnostics(
            int skeletonCount,
            int jointCount,
            int skinCount,
            int skinnedSubMeshCount,
            int animationClipCount,
            int animationChannelCount,
            int unsupportedInterpolationCount,
            int maxInfluencesPerVertex)
        {
            SkeletonCount = skeletonCount;
            JointCount = jointCount;
            SkinCount = skinCount;
            SkinnedSubMeshCount = skinnedSubMeshCount;
            AnimationClipCount = animationClipCount;
            AnimationChannelCount = animationChannelCount;
            UnsupportedInterpolationCount = unsupportedInterpolationCount;
            MaxInfluencesPerVertex = maxInfluencesPerVertex;
        }

        public int SkeletonCount { get; }
        public int JointCount { get; }
        public int SkinCount { get; }
        public int SkinnedSubMeshCount { get; }
        public int AnimationClipCount { get; }
        public int AnimationChannelCount { get; }
        public int UnsupportedInterpolationCount { get; }
        public int MaxInfluencesPerVertex { get; }
        public bool HasAnimationData => SkeletonCount > 0 || SkinCount > 0 || AnimationClipCount > 0;
    }

    internal sealed class GltfAssetManifest
    {
        public GltfAssetManifest(
            IReadOnlyList<GltfMaterial> materials,
            AssetImportDiagnostics diagnostics)
        {
            Materials = materials;
            Diagnostics = diagnostics;
        }

        public IReadOnlyList<GltfMaterial> Materials { get; }
        public AssetImportDiagnostics Diagnostics { get; }
    }

    internal sealed class AssimpAnimationManifest
    {
        public static AssimpAnimationManifest Empty { get; } = new(
            Array.Empty<CoreSkeleton>(),
            Array.Empty<Skin>(),
            Array.Empty<AnimationClip>(),
            new Dictionary<string, int>(StringComparer.Ordinal),
            0,
            ModelAnimationImportDiagnostics.Empty);

        public AssimpAnimationManifest(
            IReadOnlyList<CoreSkeleton> skeletons,
            IReadOnlyList<Skin> skins,
            IReadOnlyList<AnimationClip> animationClips,
            IReadOnlyDictionary<string, int> nodeNameToJoint,
            nint skeletonRootNodeAddress,
            ModelAnimationImportDiagnostics diagnostics)
        {
            Skeletons = skeletons;
            Skins = skins;
            AnimationClips = animationClips;
            NodeNameToJoint = nodeNameToJoint;
            SkeletonRootNodeAddress = skeletonRootNodeAddress;
            Diagnostics = diagnostics;
        }

        public IReadOnlyList<CoreSkeleton> Skeletons { get; }
        public IReadOnlyList<Skin> Skins { get; }
        public IReadOnlyList<AnimationClip> AnimationClips { get; }
        public IReadOnlyDictionary<string, int> NodeNameToJoint { get; }
        public nint SkeletonRootNodeAddress { get; }
        public ModelAnimationImportDiagnostics Diagnostics { get; }
    }

    internal readonly record struct VertexInfluence(ushort Joint, float Weight);

    internal sealed class GltfMaterial
    {
        public string? Name { get; set; }
        public Vector4? BaseColorFactor { get; set; }
        public Vector4? EmissiveFactor { get; set; }
        public float? MetallicFactor { get; set; }
        public float? RoughnessFactor { get; set; }
        public float? OcclusionStrength { get; set; }
        public float? NormalScale { get; set; }
        public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
        public float? AlphaCutoff { get; set; }
        public bool DoubleSided { get; set; }
        public string? BaseColorTexturePath { get; set; }
        public string? NormalTexturePath { get; set; }
        public string? MetallicRoughnessTexturePath { get; set; }
        public string? OcclusionTexturePath { get; set; }
        public string? EmissiveTexturePath { get; set; }
        public ModelTextureSlot? BaseColorTexture { get; set; }
        public ModelTextureSlot? NormalTexture { get; set; }
        public ModelTextureSlot? MetallicRoughnessTexture { get; set; }
        public ModelTextureSlot? OcclusionTexture { get; set; }
        public ModelTextureSlot? EmissiveTexture { get; set; }
    }

    internal readonly record struct GlbPayload(byte[] Json, byte[]? BinaryChunk);
    internal readonly record struct GltfBufferData(byte[] Bytes, int DeclaredLength);
    internal readonly record struct GltfBufferViewData(int Buffer, int ByteOffset, int ByteLength);
    internal readonly record struct GltfTexture(int? Source, int? Sampler);
}
