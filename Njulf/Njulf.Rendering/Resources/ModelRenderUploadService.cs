using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Njulf.Assets;
using Njulf.Assets.Validation;
using Njulf.Assets.Cooked;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Resources
{
    public sealed class ModelRenderUploadService :
        IModelRenderUploadService,
        IDisposable
    {
        private readonly IModelRenderUploadBackend _backend;
        private readonly RuntimePrimitiveTransportProfileBuilder _runtimePrimitiveProfiles;
        private readonly OpacityMicromapRuntimeRegistrationStore
            _opacityMicromapRegistrations;
        private readonly Action<MeshHandle> _releaseMeshHandle;
        private readonly Action<object> _retainMeshResource;
        private readonly Action<object> _releaseMeshResource;
        private readonly Action<object> _retainMaterialResource;
        private readonly Action<object> _releaseMaterialResource;
        private readonly object _lifecycleLock = new();
        private readonly object _diagnosticsLock = new object();
        private ModelUploadOwnershipLedger?
            _pendingMaterialOwnershipRollback;
        private ModelUploadRollbackLedger?
            _pendingModelUploadRollback;
        private bool _uploadInProgress;
        private int _uploadThreadId;
        private bool _disposeStarted;
        private bool _disposeCompleted;
        private ModelRenderUploadDiagnostics _lastUploadDiagnostics =
            new ModelRenderUploadDiagnostics(string.Empty, 0, 0, 0, 0, 0, 0, 0, 0);

        public ModelRenderUploadService(
            MeshManager meshManager,
            TextureManager textureManager,
            MaterialManager materialManager)
            : this(
                meshManager,
                textureManager,
                materialManager,
                new OpacityMicromapRuntimeRegistrationStore())
        {
        }

        public ModelRenderUploadService(
            MeshManager meshManager,
            TextureManager textureManager,
            MaterialManager materialManager,
            OpacityMicromapRuntimeRegistrationStore
                opacityMicromapRegistrations)
            : this(new ModelRenderUploadBackend(
                meshManager,
                textureManager,
                materialManager),
                opacityMicromapRegistrations)
        {
        }

        internal ModelRenderUploadService(IModelRenderUploadBackend backend)
            : this(backend, new OpacityMicromapRuntimeRegistrationStore())
        {
        }

        internal ModelRenderUploadService(
            IModelRenderUploadBackend backend,
            OpacityMicromapRuntimeRegistrationStore
                opacityMicromapRegistrations)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _opacityMicromapRegistrations =
                opacityMicromapRegistrations ??
                throw new ArgumentNullException(
                    nameof(opacityMicromapRegistrations));
            _runtimePrimitiveProfiles = new RuntimePrimitiveTransportProfileBuilder();
            _releaseMeshHandle = ReleaseMeshHandle;
            _retainMeshResource = resource =>
                RetainMeshHandle(RequireMeshHandle(resource));
            _releaseMeshResource = resource =>
                ReleaseMeshHandle(RequireMeshHandle(resource));
            _retainMaterialResource = resource =>
                _backend.RetainMaterial(
                    RequireMaterialHandle(resource));
            _releaseMaterialResource = resource =>
                _backend.ReleaseMaterial(
                    RequireMaterialHandle(resource));
        }

        public ModelRenderUploadDiagnostics LastUploadDiagnostics
        {
            get
            {
                lock (_diagnosticsLock)
                    return _lastUploadDiagnostics;
            }
        }

        internal OpacityMicromapRuntimeRegistrationStore
            OpacityMicromapRegistrations =>
            _opacityMicromapRegistrations;

        internal int PendingMaterialRollbackResourceCount
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return checked(
                        (_pendingMaterialOwnershipRollback
                            ?.PendingResourceCount ??
                         0) +
                        (_pendingModelUploadRollback
                            ?.PendingResourceCount ??
                         0));
                }
            }
        }

        internal Action<ModelUploadPublicationStage>?
            UploadPublicationFaultInjector
        { get; set; }

        public Model UploadModel(ModelMesh modelMesh)
        {
            lock (_lifecycleLock)
            {
                BeginUploadLocked();
                try
                {
                    PrepareForUploadLocked();
                    return UploadModelCore(modelMesh);
                }
                finally
                {
                    EndUploadLocked();
                }
            }
        }

        private Model UploadModelCore(ModelMesh modelMesh)
        {
            if (modelMesh == null)
                throw new ArgumentNullException(nameof(modelMesh));

            ValidateModelMesh(modelMesh);

            var model = new Model
            {
                Name = string.IsNullOrWhiteSpace(modelMesh.Name) ? "Model" : modelMesh.Name,
                BoundingBox = modelMesh.BoundingBox,
                BoundingSphere = modelMesh.BoundingSphere
            };
            model.AddSkeletons(modelMesh.Skeletons);
            model.AddSkins(modelMesh.Skins);
            model.AddAnimationClips(modelMesh.AnimationClips);
            model.AddLights(modelMesh.Lights);

            IReadOnlyList<ModelMaterial> importedMaterials = modelMesh.Materials.Count > 0
                ? modelMesh.Materials
                : new[] { ModelMaterial.Default };
            IReadOnlyList<ModelSubMesh> subMeshes = modelMesh.SubMeshes.Count > 0
                ? modelMesh.SubMeshes
                : new[]
                {
                    new ModelSubMesh
                    {
                        Name = string.IsNullOrWhiteSpace(modelMesh.Name) ? "Mesh" : modelMesh.Name,
                        MaterialIndex = 0,
                        Vertices = modelMesh.Vertices,
                        Normals = modelMesh.Normals,
                        Tangents = modelMesh.Tangents,
                        Bitangents = modelMesh.Bitangents,
                        TexCoords = modelMesh.TexCoords,
                        TexCoords1 = modelMesh.TexCoords1,
                        VertexColors = modelMesh.VertexColors,
                        JointIndices0 = modelMesh.JointIndices0,
                        JointWeights0 = modelMesh.JointWeights0,
                        Indices = modelMesh.Indices,
                        BoundingBox = modelMesh.BoundingBox,
                        BoundingSphere = modelMesh.BoundingSphere
                    }
                };

            foreach (ModelSubMesh subMesh in subMeshes)
                ValidateSubMesh(subMesh, nameof(modelMesh));
            RuntimePrimitiveTransportProfileBuildResult profileBuild =
                _runtimePrimitiveProfiles.Build(subMeshes, importedMaterials);
            var rollback =
                new ModelUploadRollbackLedger(
                    model,
                    checked(
                        importedMaterials.Count +
                        subMeshes.Count),
                    subMeshes.Count,
                    _releaseMeshHandle,
                    _backend.ReleaseMaterial,
                    _backend.ReleaseTexture);

            try
            {
                _backend.InitializeDefaultTextures();
                MaterialUploadResult materialUpload = RegisterImportedMaterials(importedMaterials);
                MaterialHandle[] materials = materialUpload.Materials;
                rollback.TrackBaseMaterials(materials);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterMaterialRegistration);
                var profileAuthenticationDiagnostics = new List<string>();
                var meshRegistrations = new MeshManager.MeshRegistrationData[subMeshes.Count];
                var subMeshMaterials = new MaterialHandle[subMeshes.Count];
                var subMeshNames = new string[subMeshes.Count];
                for (int i = 0; i < subMeshes.Count; i++)
                {
                    ModelSubMesh subMesh = subMeshes[i];
                    GPUVertex[] vertices = BuildGpuVertices(subMesh);
                    GPUVertexSkinningData[] skinningData = BuildGpuSkinningData(subMesh, model);
                    int materialIndex = ResolveSubMeshMaterialIndex(subMesh, materials.Length);
                    ModelGiCausticHeroTopologyEvidence causticTopologyEvidence =
                        default;
                    ModelMaterial sourceMaterial = importedMaterials.Count == 0
                        ? ModelMaterial.Default
                        : importedMaterials[materialIndex];
                    if (sourceMaterial.GiCausticParticipation !=
                        ModelGiCausticParticipationMode.None)
                    {
                        if (!ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
                                subMesh.Vertices,
                                subMesh.Indices,
                                isSkinned: subMesh.SkinIndex >= 0,
                                out causticTopologyEvidence,
                                out string causticTopologyReason))
                        {
                            if (profileAuthenticationDiagnostics.Count < 16)
                            {
                                profileAuthenticationDiagnostics.Add(
                                    $"Submesh {i} ('{subMesh.Name}') C4 hero rejected: " +
                                    causticTopologyReason);
                            }
                        }
                        else
                        {
                            ModelGiCausticHeroValidation causticValidation =
                                ModelGiCausticHeroValidator.Validate(
                                    sourceMaterial.GiCausticParticipation,
                                    sourceMaterial.AlphaMode,
                                    sourceMaterial.GiTransmissionPolicy,
                                    sourceMaterial.Roughness,
                                    sourceMaterial.Ior,
                                    sourceMaterial.ThicknessFactor,
                                    sourceMaterial.AttenuationDistance,
                                    new Vector4(
                                        sourceMaterial.AttenuationColor.X,
                                        sourceMaterial.AttenuationColor.Y,
                                        sourceMaterial.AttenuationColor.Z,
                                        sourceMaterial.AttenuationColor.W),
                                    causticTopologyEvidence);
                            if (!causticValidation.IsEligible &&
                                profileAuthenticationDiagnostics.Count < 16)
                            {
                                profileAuthenticationDiagnostics.Add(
                                    $"Submesh {i} ('{subMesh.Name}') C4 hero rejected: " +
                                    causticValidation.Detail);
                            }
                        }
                    }
                    GiPrimitiveTransportProfile primitiveProfile = profileBuild.Profiles[i];
                    if (!TryAuthenticatePrimitiveTextureHashes(
                            materials[materialIndex],
                            primitiveProfile,
                            out string? authenticationFailure))
                    {
                        primitiveProfile =
                            RuntimePrimitiveTransportProfileBuilder.InvalidateProfile(
                                primitiveProfile,
                                $"Runtime primitive profile was invalidated after texture upload: " +
                                authenticationFailure);
                        profileBuild.Profiles[i] = primitiveProfile;
                        if (profileAuthenticationDiagnostics.Count < 16)
                        {
                            profileAuthenticationDiagnostics.Add(
                                $"Submesh {i} ('{subMesh.Name}'): {authenticationFailure}");
                        }
                    }
                    meshRegistrations[i] = new MeshManager.MeshRegistrationData(
                        vertices,
                        subMesh.Indices,
                        generateMeshlets: true,
                        skinningData: skinningData.Length == 0 ? null : skinningData,
                        primitiveTransportProfile: primitiveProfile,
                        causticTopologyEvidence: causticTopologyEvidence);
                    subMeshMaterials[i] = RegisterPrimitiveProfileMaterial(
                        materials[materialIndex],
                        primitiveProfile,
                        rollback);
                    UploadPublicationFaultInjector?.Invoke(
                        ModelUploadPublicationStage
                            .AfterPrimitiveMaterialRegistration);
                    subMeshNames[i] = string.IsNullOrWhiteSpace(subMesh.Name) ? model.Name : subMesh.Name;
                }

                MeshHandle[] lifetimeMeshes =
                    _backend.RegisterMeshes(meshRegistrations);
                rollback.TrackMeshes(lifetimeMeshes);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterMeshRegistration);
                for (int i = 0; i < lifetimeMeshes.Length; i++)
                {
                    RenderObject renderObject = subMeshes[i].SkinIndex >= 0 && subMeshes[i].SkinIndex < model.Skins.Count
                        ? new SkinnedRenderObject(lifetimeMeshes[i], subMeshMaterials[i])
                        {
                            SkinIndex = subMeshes[i].SkinIndex,
                            Animator = CreateAnimator(model, subMeshes[i].SkinIndex),
                            SkinningBindTransform = subMeshes[i].SkinningBindTransform
                        }
                        : new RenderObject(lifetimeMeshes[i], subMeshMaterials[i]);

                    renderObject.Name = subMeshNames[i];
                    renderObject.LocalMeshBounds = subMeshes[i].BoundingBox;
                    model.Add(renderObject);
                    AttachRenderObjectResourceLifetime(
                        renderObject);
                    rollback.MarkRenderObjectAttached();
                    UploadPublicationFaultInjector?.Invoke(
                        ModelUploadPublicationStage
                            .AfterRenderObjectAttachment);
                }

                ModelRenderUploadDiagnostics diagnostics =
                    new ModelRenderUploadDiagnostics(
                    model.Name,
                    model.RenderObjects.Count,
                    subMeshes.Count,
                    rollback.TrackedMaterialCount,
                    materialUpload.DynamicTextureIndices.Count,
                    materialUpload.DefaultWhiteSubstitutions,
                    materialUpload.DefaultNormalSubstitutions,
                    materialUpload.DefaultBlackSubstitutions,
                    materialUpload.BlendMaterialCount,
                    profileBuild.Profiles.Count(
                        static profile =>
                            profile.IsComplete &&
                            profile.Quality != GiPrimitiveTransportProfileQuality.Invalid),
                    profileBuild.Profiles.Count(
                        static profile =>
                            !profile.IsComplete ||
                            profile.Quality == GiPrimitiveTransportProfileQuality.Invalid),
                    profileBuild.Diagnostics.ProfileCacheHitCount,
                    profileBuild.Diagnostics.ProfileCacheMissCount,
                    profileBuild.Diagnostics.TextureAnalysisFailureCount,
                    profileBuild.Diagnostics.PackageOmittedEmissiveRecordCount,
                    string.Join(
                        " | ",
                        new[] { profileBuild.Diagnostics.Summary }
                            .Concat(profileAuthenticationDiagnostics)
                            .Where(static message => !string.IsNullOrWhiteSpace(message))));

                RegisterModelMaterialLifetime(
                    model,
                    materials);
                rollback.TransferBaseMaterialsToModel();
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterBaseMaterialTransfer);
                rollback.Commit();
                SetLastUploadDiagnostics(diagnostics);
                return model;
            }
            catch (Exception uploadFailure)
            {
                Exception? rollbackFailure =
                    rollback.TryRollback();
                if (rollbackFailure != null)
                {
                    PublishPendingModelUploadRollbackLocked(
                        rollback);
                    throw new AggregateException(
                        "Model upload failed and resource ownership rollback also failed.",
                        uploadFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        public Model UploadCookedModel(CookedModelAsset cooked)
        {
            lock (_lifecycleLock)
            {
                BeginUploadLocked();
                try
                {
                    PrepareForUploadLocked();
                    return UploadCookedModelCore(cooked);
                }
                finally
                {
                    EndUploadLocked();
                }
            }
        }

        private Model UploadCookedModelCore(
            CookedModelAsset cooked)
        {
            if (cooked == null)
                throw new ArgumentNullException(nameof(cooked));
            CookedMeshPayload payload = cooked.Mesh;
            if (payload.SubMeshes.Count == 0)
                throw new CookedAssetFormatException(cooked.PackagePath, "mesh payload contains no submeshes");

            var model = new Model
            {
                Name = string.IsNullOrWhiteSpace(cooked.Manifest.Name) ? "Model" : cooked.Manifest.Name,
                BoundingBox = cooked.Manifest.BoundingBox,
                BoundingSphere = cooked.Manifest.BoundingSphere
            };
            model.AddSkeletons(cooked.Animation.Skeletons);
            model.AddSkins(cooked.Animation.Skins);
            model.AddAnimationClips(cooked.Animation.AnimationClips);
            model.AddLights(cooked.Manifest.Lights);

            var rollback =
                new ModelUploadRollbackLedger(
                    model,
                    checked(
                        Math.Max(
                            1,
                            cooked.Materials.Materials.Count) +
                        payload.SubMeshes.Count),
                    payload.SubMeshes.Count,
                    _releaseMeshHandle,
                    _backend.ReleaseMaterial,
                    _backend.ReleaseTexture);
            int opacityMicromapPayloadAcceptedCount =
                cooked.OpacityMicromapLoadStatus.Accepted &&
                cooked.OpacityMicromapPayload is not null
                    ? 1
                    : 0;
            int opacityMicromapRuntimeRegistrationCount = 0;
            string opacityMicromapRuntimeDetail =
                cooked.OpacityMicromapLoadStatus.Detail;
            try
            {
                _backend.InitializeDefaultTextures();
                MaterialUploadResult materialUpload = RegisterImportedMaterials(
                    cooked.Materials.Materials,
                    cooked.Materials.Pipelines);
                MaterialHandle[] materials = materialUpload.Materials;
                rollback.TrackBaseMaterials(materials);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterMaterialRegistration);
                MaterialHandle[] subMeshMaterials = ResolveCookedPrimitiveMaterials(
                    cooked.Materials,
                    payload.SubMeshes,
                    materials,
                    rollback,
                    out GiPrimitiveTransportProfile?[] primitiveProfiles,
                    out IReadOnlyList<string> profileAuthenticationDiagnostics);
                var registrations = new MeshManager.MeshRegistrationData[payload.SubMeshes.Count];
                for (int i = 0; i < payload.SubMeshes.Count; i++)
                {
                    CookedSubMeshRecord subMesh = payload.SubMeshes[i];
                    GPUVertexPositionStream[] vertexPositions = BuildCookedPositionStream(payload, subMesh);
                    GPUVertexNormalTangentStream[] vertexNormalTangents = BuildCookedNormalTangentStream(payload, subMesh);
                    GPUVertexUvColorStream[] vertexUvColors = BuildCookedUvColorStream(payload, subMesh);
                    uint[] indices = payload.Indices.AsSpan(subMesh.IndexOffset, subMesh.IndexCount).ToArray();
                    Meshlet[] meshlets = BuildCookedMeshletRanges(
                        payload,
                        subMesh,
                        out int lod0Count,
                        out int lod1Count,
                        out int lod2Count);
                    uint[] meshletVertices = payload.MeshletVertices.AsSpan(subMesh.MeshletVertexOffset, subMesh.MeshletVertexCount).ToArray();
                    uint[] meshletTriangles = payload.MeshletTriangles.AsSpan(subMesh.MeshletTriangleOffset, subMesh.MeshletTriangleCount).ToArray();
                    GPUVertexSkinningData[] skinning = BuildCookedSkinning(payload, subMesh);
                    registrations[i] = new MeshManager.MeshRegistrationData(
                        vertexPositions,
                        vertexNormalTangents,
                        vertexUvColors,
                        indices,
                        meshlets,
                        meshletVertices,
                        meshletTriangles,
                        lod0Count,
                        lod1Count,
                        lod2Count,
                        skinning.Length == 0 ? null : skinning,
                        primitiveProfiles[i],
                        subMesh.CausticTopologyEvidence);
                }

                MeshHandle[] lifetimeMeshes =
                    _backend.RegisterMeshes(registrations);
                rollback.TrackMeshes(lifetimeMeshes);
                opacityMicromapRuntimeRegistrationCount =
                    RegisterCookedOpacityMicromaps(
                        cooked,
                        registrations,
                        lifetimeMeshes,
                        subMeshMaterials,
                        out opacityMicromapRuntimeDetail);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterMeshRegistration);
                for (int i = 0; i < lifetimeMeshes.Length; i++)
                {
                    CookedSubMeshRecord subMesh = payload.SubMeshes[i];
                    RenderObject renderObject = subMesh.SkinIndex >= 0 && subMesh.SkinIndex < model.Skins.Count
                        ? new SkinnedRenderObject(lifetimeMeshes[i], subMeshMaterials[i])
                        {
                            SkinIndex = subMesh.SkinIndex,
                            Animator = CreateAnimator(model, subMesh.SkinIndex),
                            SkinningBindTransform = subMesh.SkinningBindTransform
                        }
                        : new RenderObject(lifetimeMeshes[i], subMeshMaterials[i]);
                    renderObject.Name = string.IsNullOrWhiteSpace(subMesh.Name) ? model.Name : subMesh.Name;
                    renderObject.LocalMeshBounds = subMesh.BoundingBox;
                    model.Add(renderObject);
                    AttachRenderObjectResourceLifetime(
                        renderObject);
                    rollback.MarkRenderObjectAttached();
                    UploadPublicationFaultInjector?.Invoke(
                        ModelUploadPublicationStage
                            .AfterRenderObjectAttachment);
                }

                ModelRenderUploadDiagnostics diagnostics =
                    new ModelRenderUploadDiagnostics(
                    model.Name,
                    model.RenderObjects.Count,
                    payload.SubMeshes.Count,
                    rollback.TrackedMaterialCount,
                    materialUpload.DynamicTextureIndices.Count,
                    materialUpload.DefaultWhiteSubstitutions,
                    materialUpload.DefaultNormalSubstitutions,
                    materialUpload.DefaultBlackSubstitutions,
                    materialUpload.BlendMaterialCount,
                    primitiveProfiles.Count(
                        static profile =>
                            profile != null &&
                            profile.IsComplete &&
                            profile.Quality != GiPrimitiveTransportProfileQuality.Invalid),
                    primitiveProfiles.Count(
                        static profile =>
                            profile != null &&
                            !profile.IsComplete ||
                            profile != null &&
                            profile.Quality == GiPrimitiveTransportProfileQuality.Invalid),
                    0,
                    0,
                    0,
                    cooked.Materials.PrimitiveTransportProfiles.Sum(
                        static profile => Math.Max(
                            profile.EmissiveCandidateTriangleCount -
                            profile.EmissiveTriangles.Length,
                            0)),
                    string.Join(
                        " | ",
                        profileAuthenticationDiagnostics),
                    opacityMicromapPayloadAcceptedCount,
                    opacityMicromapRuntimeRegistrationCount,
                    opacityMicromapRuntimeDetail);
                RegisterModelMaterialLifetime(
                    model,
                    materials);
                rollback.TransferBaseMaterialsToModel();
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterBaseMaterialTransfer);
                rollback.Commit();
                SetLastUploadDiagnostics(diagnostics);
                return model;
            }
            catch (Exception uploadFailure)
            {
                Exception? rollbackFailure =
                    rollback.TryRollback();
                if (rollbackFailure != null)
                {
                    PublishPendingModelUploadRollbackLocked(
                        rollback);
                    throw new AggregateException(
                        "Cooked model upload failed and resource ownership rollback also failed.",
                        uploadFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        private MaterialHandle[] ResolveCookedPrimitiveMaterials(
            CookedMaterialTable materialTable,
            IReadOnlyList<CookedSubMeshRecord> subMeshes,
            IReadOnlyList<MaterialHandle> baseMaterials,
            ModelUploadRollbackLedger rollback,
            out GiPrimitiveTransportProfile?[] primitiveProfiles,
            out IReadOnlyList<string> authenticationDiagnostics)
        {
            if (baseMaterials.Count == 0)
                throw new InvalidDataException("Cooked model has no runtime material handles.");

            var profilesBySubMesh = new Dictionary<int, GiPrimitiveTransportProfile>();
            foreach (GiPrimitiveTransportProfile profile in materialTable.PrimitiveTransportProfiles)
            {
                if ((uint)profile.SubMeshIndex >= (uint)subMeshes.Count)
                {
                    throw new InvalidDataException(
                        $"Primitive transport profile references submesh {profile.SubMeshIndex}, " +
                        $"but the cooked mesh has {subMeshes.Count} submeshes.");
                }
                if (!profilesBySubMesh.TryAdd(profile.SubMeshIndex, profile))
                {
                    throw new InvalidDataException(
                        $"Cooked material data contains duplicate primitive transport profiles for submesh {profile.SubMeshIndex}.");
                }
            }

            var resolved = new MaterialHandle[subMeshes.Count];
            primitiveProfiles = new GiPrimitiveTransportProfile?[subMeshes.Count];
            var diagnostics = new List<string>();
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                CookedSubMeshRecord subMesh = subMeshes[subMeshIndex];
                if ((uint)subMesh.MaterialSlot >= (uint)baseMaterials.Count)
                {
                    throw new InvalidDataException(
                        $"Cooked submesh {subMeshIndex} references material slot {subMesh.MaterialSlot}, " +
                        $"but only {baseMaterials.Count} materials exist.");
                }

                MaterialHandle baseHandle = baseMaterials[subMesh.MaterialSlot];
                if (!profilesBySubMesh.TryGetValue(
                        subMeshIndex,
                        out GiPrimitiveTransportProfile? cookedProfile) ||
                    cookedProfile is null)
                {
                    resolved[subMeshIndex] =
                        RetainPrimitiveBaseMaterial(
                            baseHandle,
                            rollback);
                    UploadPublicationFaultInjector?.Invoke(
                        ModelUploadPublicationStage
                            .AfterPrimitiveMaterialRegistration);
                    continue;
                }
                if (cookedProfile.MaterialSlot != subMesh.MaterialSlot)
                {
                    throw new InvalidDataException(
                        $"Primitive transport profile {subMeshIndex} is paired with material slot " +
                        $"{cookedProfile.MaterialSlot}, expected {subMesh.MaterialSlot}.");
                }
                if (!TryAuthenticatePrimitiveTextureHashes(
                        baseHandle,
                        cookedProfile,
                        out string? authenticationFailure))
                {
                    cookedProfile =
                        RuntimePrimitiveTransportProfileBuilder.InvalidateProfile(
                            cookedProfile,
                            $"Cooked primitive profile was invalidated after authenticated texture upload: " +
                            authenticationFailure);
                    if (diagnostics.Count < 16)
                    {
                        diagnostics.Add(
                            $"Submesh {subMeshIndex} ('{subMesh.Name}'): " +
                            authenticationFailure);
                    }
                }

                primitiveProfiles[subMeshIndex] = cookedProfile;
                resolved[subMeshIndex] = RegisterPrimitiveProfileMaterial(
                    baseHandle,
                    cookedProfile,
                    rollback);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterPrimitiveMaterialRegistration);
            }

            authenticationDiagnostics = diagnostics;
            return resolved;
        }

        private MaterialHandle RegisterPrimitiveProfileMaterial(
            MaterialHandle baseHandle,
            GiPrimitiveTransportProfile primitiveProfile,
            ModelUploadRollbackLedger rollback)
        {
            ArgumentNullException.ThrowIfNull(rollback);
            GiMaterialTransportProfile runtimeProfile =
                ConvertPrimitiveTransportProfile(primitiveProfile);
            if (runtimeProfile.Quality != GiTransportProfileQuality.PrimitiveSurfaceSampling)
                return RetainPrimitiveBaseMaterial(
                    baseHandle,
                    rollback);

            MaterialDefinition definition = _backend.GetMaterialDefinition(baseHandle);
            IReadOnlyList<TextureHandle> textures =
                _backend.GetMaterialTextures(baseHandle);
            foreach (TextureHandle texture in textures)
            {
                if (!texture.IsValid)
                {
                    throw new InvalidOperationException(
                        "A live material returned an invalid texture dependency.");
                }
            }

            // Collection capacity and the material occurrence slot are
            // reserved before the first retain. Every successful retain is
            // therefore durably recorded without a subsequent allocation.
            rollback.BeginPrimitiveMaterialAcquisition(
                textures.Count);
            try
            {
                foreach (TextureHandle texture in textures)
                {
                    _backend.RetainTexture(texture);
                    rollback.TrackRetainedPrimitiveTexture(
                        texture);
                }

                MaterialHandle primitiveHandle =
                    _backend.RegisterMaterialDefinition(definition, runtimeProfile);
                rollback.CommitPrimitiveMaterialAcquisition(
                    primitiveHandle);
                return primitiveHandle;
            }
            catch
            {
                rollback.AbortPrimitiveMaterialAcquisition();
                throw;
            }
        }

        private MaterialHandle RetainPrimitiveBaseMaterial(
            MaterialHandle baseHandle,
            ModelUploadRollbackLedger rollback)
        {
            ArgumentNullException.ThrowIfNull(rollback);
            rollback.BeginPrimitiveMaterialAcquisition(
                expectedTextureCount: 0);
            try
            {
                _backend.RetainMaterial(baseHandle);
                rollback.CommitPrimitiveMaterialAcquisition(
                    baseHandle);
                return baseHandle;
            }
            catch
            {
                rollback.AbortPrimitiveMaterialAcquisition();
                throw;
            }
        }

        private bool TryAuthenticatePrimitiveTextureHashes(
            MaterialHandle materialHandle,
            GiPrimitiveTransportProfile profile,
            out string? failure)
        {
            if (profile.TextureSourceHashes.Length !=
                GiPrimitiveTransportProfile.TextureSourceHashCount)
            {
                failure =
                    $"profile contains {profile.TextureSourceHashes.Length} texture hashes; expected " +
                    $"{GiPrimitiveTransportProfile.TextureSourceHashCount}.";
                return false;
            }

            MaterialDefinition definition =
                _backend.GetMaterialDefinition(materialHandle);
            MaterialTextureBinding[] bindings =
            [
                definition.BaseColor,
                definition.MetallicRoughness,
                definition.Occlusion,
                definition.Emissive,
                definition.Normal,
                definition.Extensions.Clearcoat,
                definition.Extensions.SheenColor,
                definition.Extensions.Transmission,
                definition.Extensions.Specular,
                definition.Extensions.SpecularColor
            ];
            for (int index = 0; index < bindings.Length; index++)
            {
                MaterialTextureBinding binding = bindings[index];
                ulong expected = profile.TextureSourceHashes[index];
                if (expected == 0)
                {
                    if (binding.IsBound)
                    {
                        failure =
                            $"texture slot {index} is bound after upload but the primitive " +
                            "profile reports no source-content hash.";
                        return false;
                    }
                    continue;
                }
                if (!binding.IsBound)
                {
                    failure =
                        $"texture slot {index} is unbound after upload but the source profile reports " +
                        $"hash 0x{expected:x16}.";
                    return false;
                }
                if (!_backend.TryGetTextureTransportStatistics(
                        binding.Texture,
                        out TextureTransportStatistics statistics) ||
                    !statistics.IsValid ||
                    !statistics.Validity.HasFlag(
                        TextureTransportStatisticsValidity.SourceContentHash) ||
                    statistics.SourceContentHash == 0)
                {
                    failure =
                        $"texture slot {index} has no valid authenticated runtime " +
                        "source-resolution statistics.";
                    return false;
                }
                if (statistics.SourceContentHash != expected)
                {
                    failure =
                        $"texture slot {index} was uploaded from hash " +
                        $"0x{statistics.SourceContentHash:x16}, but primitive integration used " +
                        $"0x{expected:x16}.";
                    return false;
                }
            }

            failure = null;
            return true;
        }

        internal static GiMaterialTransportProfile ConvertPrimitiveTransportProfile(
            GiPrimitiveTransportProfile profile)
        {
            GiPrimitiveTransportProfileValidity finite =
                GiPrimitiveTransportProfileValidity.Geometry |
                GiPrimitiveTransportProfileValidity.Finite;
            if ((profile.Validity & finite) != finite ||
                !profile.IsComplete ||
                profile.Quality == GiPrimitiveTransportProfileQuality.Invalid)
            {
                return GiMaterialTransportProfile.Invalid;
            }

            GiMaterialTransportFlags flags = GiMaterialTransportFlags.None;
            if (profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.Diffuse))
                flags |= GiMaterialTransportFlags.DiffuseProfileValid;
            if (profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.Diffuse) &&
                profile.GiTransmissionPolicy == ModelGiTransmissionPolicy.ThinSurface)
            {
                flags |= GiMaterialTransportFlags.ThinSurfaceTransmission |
                         GiMaterialTransportFlags.TransmissionProfileValid;
            }
            if (profile.GiTransmissionPolicy == ModelGiTransmissionPolicy.Volume)
            {
                flags |= GiMaterialTransportFlags.VolumeTransmission |
                         GiMaterialTransportFlags.TransmissionRemovesOpaqueDiffuse;
            }
            if (profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.Emission))
                flags |= GiMaterialTransportFlags.EmissionProfileValid;
            if (profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.AlphaCoverage))
                flags |= GiMaterialTransportFlags.AlphaProfileValid;
            if (profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.NormalVariance))
                flags |= GiMaterialTransportFlags.NormalProfileValid;
            GiPrimitiveTransportProfileValidity baseValidity =
                GiPrimitiveTransportProfileValidity.AmbientOcclusion |
                GiPrimitiveTransportProfileValidity.MetallicRoughness;
            if ((profile.Validity & baseValidity) == baseValidity)
                flags |= GiMaterialTransportFlags.BaseStatisticsValid;

            var converted = new GiMaterialTransportProfile
            {
                AlgorithmVersion = profile.AlgorithmVersion,
                SourceContentHash = CombineTextureSourceHashes(profile.TextureSourceHashes),
                PrimitiveContentHash = profile.InputHash,
                Flags = flags,
                Quality = GiTransportProfileQuality.PrimitiveSurfaceSampling,
                MeanDiffuseReflectance = ToFiniteUnitVector3(
                    profile.MeanDiffuseReflectance,
                    nameof(profile.MeanDiffuseReflectance)),
                MeanTransmittedDiffuseReflectance = ToFiniteUnitVector3(
                    profile.MeanTransmittedDiffuseReflectance,
                    nameof(profile.MeanTransmittedDiffuseReflectance)),
                MeanEmissiveRadiance = ToFiniteNonNegativeVector3(
                    profile.MeanEmission,
                    nameof(profile.MeanEmission)),
                EmissiveImportance = ToFiniteNonNegative(
                    profile.MeanEmission.X * 0.2126 +
                    profile.MeanEmission.Y * 0.7152 +
                    profile.MeanEmission.Z * 0.0722,
                    nameof(profile.MeanEmission)),
                MeanMaterialOcclusion = ToFiniteUnit(
                    profile.MeanAmbientOcclusion,
                    nameof(profile.MeanAmbientOcclusion)),
                AlphaCoverage = ToFiniteUnit(profile.AlphaCoverage, nameof(profile.AlphaCoverage)),
                MeanMetallic = ToFiniteUnit(profile.MeanMetallic, nameof(profile.MeanMetallic)),
                MeanRoughness = ToFiniteUnit(profile.MeanRoughness, nameof(profile.MeanRoughness)),
                NormalVariance = ToFiniteUnit(profile.NormalVariance, nameof(profile.NormalVariance))
            };
            return converted;
        }

        private static CoreVector3 ToFiniteUnitVector3(
            TextureTransportVector4 value,
            string name) => new(
            ToFiniteUnit(value.X, name),
            ToFiniteUnit(value.Y, name),
            ToFiniteUnit(value.Z, name));

        private static CoreVector3 ToFiniteNonNegativeVector3(
            TextureTransportVector4 value,
            string name) => new(
            ToFiniteNonNegative(value.X, name),
            ToFiniteNonNegative(value.Y, name),
            ToFiniteNonNegative(value.Z, name));

        private static float ToFiniteUnit(double value, string name)
        {
            if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
                throw new InvalidDataException($"Cooked primitive field {name} contains out-of-range value {value}.");
            return (float)value;
        }

        private static float ToFiniteNonNegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0.0 || value > 65504.0)
                throw new InvalidDataException($"Cooked primitive field {name} contains invalid HDR value {value}.");
            return (float)value;
        }

        private static ulong CombineTextureSourceHashes(IReadOnlyList<ulong> hashes)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong combined = offset;
            foreach (ulong hash in hashes)
            {
                combined ^= hash;
                combined *= prime;
            }
            return combined;
        }

        private static GPUVertexPositionStream[] BuildCookedPositionStream(CookedMeshPayload payload, CookedSubMeshRecord subMesh)
        {
            var result = new GPUVertexPositionStream[subMesh.VertexCount];
            for (int i = 0; i < result.Length; i++)
            {
                CoreVector4 position = payload.VertexPositions[subMesh.VertexOffset + i].Position;
                result[i] = new GPUVertexPositionStream
                {
                    Position = position
                };
            }
            return result;
        }

        private static GPUVertexNormalTangentStream[] BuildCookedNormalTangentStream(CookedMeshPayload payload, CookedSubMeshRecord subMesh)
        {
            var result = new GPUVertexNormalTangentStream[subMesh.VertexCount];
            for (int i = 0; i < result.Length; i++)
            {
                CookedVertexNormalTangentStream source = payload.VertexNormalTangents[subMesh.VertexOffset + i];
                result[i] = new GPUVertexNormalTangentStream { Normal = source.Normal, Tangent = source.Tangent };
            }
            return result;
        }

        private static GPUVertexUvColorStream[] BuildCookedUvColorStream(CookedMeshPayload payload, CookedSubMeshRecord subMesh)
        {
            var result = new GPUVertexUvColorStream[subMesh.VertexCount];
            for (int i = 0; i < result.Length; i++)
            {
                CookedVertexUvColorStream source = payload.VertexUvColors[subMesh.VertexOffset + i];
                result[i] = new GPUVertexUvColorStream
                {
                    TexCoord = source.TexCoord,
                    TexCoord2 = source.TexCoord2,
                    Color = source.Color
                };
            }
            return result;
        }

        private static GPUVertexSkinningData[] BuildCookedSkinning(CookedMeshPayload payload, CookedSubMeshRecord subMesh)
        {
            if (subMesh.SkinningCount == 0)
                return Array.Empty<GPUVertexSkinningData>();
            var result = new GPUVertexSkinningData[subMesh.SkinningCount];
            for (int i = 0; i < result.Length; i++)
            {
                CookedVertexSkinningData source = payload.VertexSkinning[subMesh.SkinningOffset + i];
                result[i] = new GPUVertexSkinningData
                {
                    Joint0 = source.Joint0,
                    Joint1 = source.Joint1,
                    Joint2 = source.Joint2,
                    Joint3 = source.Joint3,
                    Weight0 = source.Weight0,
                    Weight1 = source.Weight1,
                    Weight2 = source.Weight2,
                    Weight3 = source.Weight3
                };
            }
            return result;
        }

        /// <summary>
        /// Materializes the three independent cooked LOD ranges directly into
        /// the one contiguous registration buffer required by MeshManager.
        /// This avoids the former three temporary LOD allocations before the
        /// final contiguous upload copy.
        /// </summary>
        private static Meshlet[] BuildCookedMeshletRanges(
            CookedMeshPayload payload,
            CookedSubMeshRecord subMesh,
            out int lod0Count,
            out int lod1Count,
            out int lod2Count)
        {
            lod0Count = subMesh.MeshletCount;
            lod1Count = subMesh.MeshletLod1Count;
            lod2Count = subMesh.MeshletLod2Count;
            var combined = new Meshlet[checked(
                lod0Count + lod1Count + lod2Count)];
            payload.MeshletsLod0.AsSpan(
                subMesh.MeshletOffset,
                lod0Count).CopyTo(combined);
            payload.MeshletsLod1.AsSpan(
                subMesh.MeshletLod1Offset,
                lod1Count).CopyTo(combined.AsSpan(lod0Count));
            payload.MeshletsLod2.AsSpan(
                subMesh.MeshletLod2Offset,
                lod2Count).CopyTo(combined.AsSpan(
                    checked(lod0Count + lod1Count)));
            return combined;
        }

        private void SetLastUploadDiagnostics(ModelRenderUploadDiagnostics diagnostics)
        {
            lock (_diagnosticsLock)
                _lastUploadDiagnostics = diagnostics;
        }

        private static void ValidateModelMesh(ModelMesh modelMesh)
        {
            if (modelMesh.Vertices.Length == 0)
                throw new ArgumentException("Imported model contains no vertices.", nameof(modelMesh));
            if (modelMesh.Indices.Length == 0)
                throw new ArgumentException("Imported model contains no indices.", nameof(modelMesh));
            if (modelMesh.Indices.Length % 3 != 0)
                throw new ArgumentException("Imported model index count must be divisible by 3.", nameof(modelMesh));

            ValidateOptionalStream(modelMesh.Normals, modelMesh.Vertices.Length, nameof(modelMesh.Normals));
            ValidateOptionalStream(modelMesh.Tangents, modelMesh.Vertices.Length, nameof(modelMesh.Tangents));
            ValidateOptionalStream(modelMesh.Bitangents, modelMesh.Vertices.Length, nameof(modelMesh.Bitangents));
            ValidateOptionalStream(modelMesh.TexCoords, modelMesh.Vertices.Length, nameof(modelMesh.TexCoords));
            ValidateOptionalStream(modelMesh.TexCoords1, modelMesh.Vertices.Length, nameof(modelMesh.TexCoords1));
            ValidateOptionalStream(modelMesh.VertexColors, modelMesh.Vertices.Length, nameof(modelMesh.VertexColors));

            for (int i = 0; i < modelMesh.Indices.Length; i++)
            {
                if (modelMesh.Indices[i] >= modelMesh.Vertices.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(modelMesh),
                        $"Imported model index {i} references vertex {modelMesh.Indices[i]}, but vertex count is {modelMesh.Vertices.Length}.");
                }
            }
        }

        private static void ValidateSubMesh(ModelSubMesh subMesh, string argumentName)
        {
            if (subMesh.Vertices.Length == 0)
                throw new ArgumentException("Imported submesh contains no vertices.", argumentName);
            if (subMesh.Indices.Length == 0)
                throw new ArgumentException("Imported submesh contains no indices.", argumentName);
            if (subMesh.Indices.Length % 3 != 0)
                throw new ArgumentException("Imported submesh index count must be divisible by 3.", argumentName);

            ValidateOptionalStream(subMesh.Normals, subMesh.Vertices.Length, nameof(subMesh.Normals));
            ValidateOptionalStream(subMesh.Tangents, subMesh.Vertices.Length, nameof(subMesh.Tangents));
            ValidateOptionalStream(subMesh.Bitangents, subMesh.Vertices.Length, nameof(subMesh.Bitangents));
            ValidateOptionalStream(subMesh.TexCoords, subMesh.Vertices.Length, nameof(subMesh.TexCoords));
            ValidateOptionalStream(subMesh.TexCoords1, subMesh.Vertices.Length, nameof(subMesh.TexCoords1));
            ValidateOptionalStream(subMesh.VertexColors, subMesh.Vertices.Length, nameof(subMesh.VertexColors));
            ValidateOptionalStream(subMesh.JointIndices0, subMesh.Vertices.Length, nameof(subMesh.JointIndices0));
            ValidateOptionalStream(subMesh.JointWeights0, subMesh.Vertices.Length, nameof(subMesh.JointWeights0));

            for (int i = 0; i < subMesh.Indices.Length; i++)
            {
                if (subMesh.Indices[i] >= subMesh.Vertices.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        argumentName,
                        $"Imported submesh index {i} references vertex {subMesh.Indices[i]}, but vertex count is {subMesh.Vertices.Length}.");
                }
            }
        }

        private static void ValidateOptionalStream<T>(T[] stream, int vertexCount, string streamName)
        {
            if (stream.Length != 0 && stream.Length != vertexCount)
                throw new ArgumentException($"Imported {streamName} stream length must be either 0 or match vertex count.", streamName);
        }

        private static GPUVertex[] BuildGpuVertices(ModelMesh modelMesh)
        {
            Vector3[] fallbackNormals = modelMesh.Normals.Length == modelMesh.Vertices.Length
                ? Array.Empty<Vector3>()
                : ComputeNormals(modelMesh.Vertices, modelMesh.Indices);

            var vertices = new GPUVertex[modelMesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector3 normal = modelMesh.Normals.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Normals[i], new CoreVector3(0f, 0f, 1f))
                    : ToCoreVector(fallbackNormals[i]);

                CoreVector3 tangent = modelMesh.Tangents.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Tangents[i], new CoreVector3(1f, 0f, 0f))
                    : new CoreVector3(1f, 0f, 0f);
                CoreVector3 bitangent = modelMesh.Bitangents.Length == modelMesh.Vertices.Length
                    ? NormalizeOrDefault(modelMesh.Bitangents[i], CoreVector3.Zero)
                    : CoreVector3.Zero;
                float tangentHandedness = CalculateTangentHandedness(normal, tangent, bitangent);

                CoreVector2 texCoord = modelMesh.TexCoords.Length == modelMesh.Vertices.Length
                    ? modelMesh.TexCoords[i]
                    : CoreVector2.Zero;
                CoreVector2 texCoord1 = modelMesh.TexCoords1.Length == modelMesh.Vertices.Length
                    ? modelMesh.TexCoords1[i]
                    : CoreVector2.Zero;
                CoreVector4 color = modelMesh.VertexColors.Length == modelMesh.Vertices.Length
                    ? modelMesh.VertexColors[i]
                    : GPUVertex.DefaultColor;

                vertices[i] = new GPUVertex
                {
                    Position = modelMesh.Vertices[i],
                    Padding0 = 0f,
                    Normal = normal,
                    Padding1 = 0f,
                    TexCoord = texCoord,
                    TexCoord2 = texCoord1,
                    Tangent = new CoreVector4(tangent.X, tangent.Y, tangent.Z, tangentHandedness),
                    Color = color
                };
            }

            return vertices;
        }

        private static GPUVertex[] BuildGpuVertices(ModelSubMesh subMesh)
        {
            Vector3[] fallbackNormals = subMesh.Normals.Length == subMesh.Vertices.Length
                ? Array.Empty<Vector3>()
                : ComputeNormals(subMesh.Vertices, subMesh.Indices);

            var vertices = new GPUVertex[subMesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector3 normal = subMesh.Normals.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Normals[i], new CoreVector3(0f, 0f, 1f))
                    : ToCoreVector(fallbackNormals[i]);

                CoreVector3 tangent = subMesh.Tangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Tangents[i], new CoreVector3(1f, 0f, 0f))
                    : new CoreVector3(1f, 0f, 0f);
                CoreVector3 bitangent = subMesh.Bitangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Bitangents[i], CoreVector3.Zero)
                    : CoreVector3.Zero;
                float tangentHandedness = CalculateTangentHandedness(normal, tangent, bitangent);

                CoreVector2 texCoord = subMesh.TexCoords.Length == subMesh.Vertices.Length
                    ? subMesh.TexCoords[i]
                    : CoreVector2.Zero;
                CoreVector2 texCoord1 = subMesh.TexCoords1.Length == subMesh.Vertices.Length
                    ? subMesh.TexCoords1[i]
                    : CoreVector2.Zero;
                CoreVector4 color = subMesh.VertexColors.Length == subMesh.Vertices.Length
                    ? subMesh.VertexColors[i]
                    : GPUVertex.DefaultColor;

                vertices[i] = new GPUVertex
                {
                    Position = subMesh.Vertices[i],
                    Padding0 = 0f,
                    Normal = normal,
                    Padding1 = 0f,
                    TexCoord = texCoord,
                    TexCoord2 = texCoord1,
                    Tangent = new CoreVector4(tangent.X, tangent.Y, tangent.Z, tangentHandedness),
                    Color = color
                };
            }

            return vertices;
        }

        private static GPUVertexSkinningData[] BuildGpuSkinningData(ModelSubMesh subMesh, Model model)
        {
            if (subMesh.SkinIndex < 0)
                return Array.Empty<GPUVertexSkinningData>();
            if (subMesh.SkinIndex >= model.Skins.Count)
                throw new InvalidOperationException(
                    $"Imported submesh '{subMesh.Name}' references skin index {subMesh.SkinIndex}, but the model only has {model.Skins.Count} skins.");
            if (subMesh.JointIndices0.Length != subMesh.Vertices.Length || subMesh.JointWeights0.Length != subMesh.Vertices.Length)
                throw new InvalidOperationException(
                    $"Skinned submesh '{subMesh.Name}' must provide JOINTS_0 and WEIGHTS_0 streams for every vertex.");

            int jointCount = model.Skins[subMesh.SkinIndex].JointIndices.Count;
            var skinningData = new GPUVertexSkinningData[subMesh.Vertices.Length];
            for (int i = 0; i < skinningData.Length; i++)
            {
                VertexJointIndices joints = subMesh.JointIndices0[i];
                VertexJointWeights weights = subMesh.JointWeights0[i].Normalized();

                ValidateJointIndex(subMesh.Name, i, joints.X, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.Y, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.Z, jointCount);
                ValidateJointIndex(subMesh.Name, i, joints.W, jointCount);

                skinningData[i] = new GPUVertexSkinningData
                {
                    Joint0 = joints.X,
                    Joint1 = joints.Y,
                    Joint2 = joints.Z,
                    Joint3 = joints.W,
                    Weight0 = weights.X,
                    Weight1 = weights.Y,
                    Weight2 = weights.Z,
                    Weight3 = weights.W
                };
            }

            return skinningData;
        }

        private static void ValidateJointIndex(string subMeshName, int vertexIndex, ushort jointIndex, int jointCount)
        {
            if (jointIndex >= jointCount)
            {
                throw new InvalidOperationException(
                    $"Skinned submesh '{subMeshName}' vertex {vertexIndex} references joint {jointIndex}, but the skin only has {jointCount} joints.");
            }
        }

        private static int ResolveSubMeshMaterialIndex(ModelSubMesh subMesh, int materialCount)
        {
            if (materialCount <= 0)
                throw new InvalidOperationException("No GPU materials were built for the imported model.");

            if (subMesh.MaterialIndex < 0 || subMesh.MaterialIndex >= materialCount)
            {
                throw new InvalidOperationException(
                    $"Imported submesh '{subMesh.Name}' references material index {subMesh.MaterialIndex}, " +
                    $"but the imported material count is {materialCount}.");
            }

            return subMesh.MaterialIndex;
        }

        private static Animator? CreateAnimator(Model model, int skinIndex)
        {
            if (skinIndex < 0 || skinIndex >= model.Skins.Count)
                return null;

            Skin skin = model.Skins[skinIndex];
            return new Animator(skin.Skeleton, model.Skins, model.AnimationClips);
        }

        private MaterialUploadResult RegisterImportedMaterials(
            IReadOnlyList<ModelMaterial> importedMaterials,
            IReadOnlyList<CookedMaterialPipeline>? cookedPipelines = null)
        {
            if (importedMaterials.Count == 0)
                importedMaterials = new[] { ModelMaterial.Default };
            if (cookedPipelines is { Count: > 0 } &&
                cookedPipelines.Count != importedMaterials.Count)
            {
                throw new InvalidDataException(
                    $"Cooked material pipeline count {cookedPipelines.Count} does not match material count {importedMaterials.Count}.");
            }

            var materials = new MaterialHandle[importedMaterials.Count];
            var dynamicTextureIndices = new HashSet<int>();
            var ownership = new ModelUploadOwnershipLedger(
                importedMaterials.Count,
                pendingTextureCapacity: 18,
                material => _backend.ReleaseMaterial(material),
                texture => _backend.ReleaseTexture(texture));
            int defaultWhiteSubstitutions = 0;
            int defaultNormalSubstitutions = 0;
            int defaultBlackSubstitutions = 0;
            int blendMaterialCount = 0;

            try
            {
                for (int i = 0; i < importedMaterials.Count; i++)
                {
                    ModelMaterial material = importedMaterials[i];
                    if (material.AlphaMode == ModelAlphaMode.Blend)
                        blendMaterialCount++;

                    MaterialTextureBindings textureBindings = ResolveMaterialTextureBindings(
                        material,
                        ref defaultWhiteSubstitutions,
                        ref defaultNormalSubstitutions,
                        ref defaultBlackSubstitutions,
                        ownership.PendingTextures);

                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.AlbedoTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.NormalTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.MetallicRoughnessTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.OcclusionTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.TextureIndices.EmissiveTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatRoughnessTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ClearcoatNormalTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SheenColorTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SheenRoughnessTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.AnisotropyTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.TransmissionTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.ThicknessTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SubsurfaceTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SpecularTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.SpecularColorTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.IridescenceTextureIndex);
                    AddDynamicTextureIndex(dynamicTextureIndices, textureBindings.ExtensionTextureIndices.IridescenceThicknessTextureIndex);

                    CookedMaterialPipeline? cookedPipeline =
                        cookedPipelines is { Count: > 0 } ? cookedPipelines[i] : null;
                    MaterialDefinition definition = BuildMaterialDefinition(
                        material,
                        textureBindings,
                        cookedPipeline);
                    materials[i] = _backend.RegisterMaterialDefinition(definition);
                    // RegisterMaterialDefinition transfers every pending texture
                    // occurrence to this logical material reference on success.
                    ownership.CommitPendingTexturesTo(materials[i]);
                }

                return new MaterialUploadResult(
                    materials,
                    dynamicTextureIndices,
                    defaultWhiteSubstitutions,
                    defaultNormalSubstitutions,
                    defaultBlackSubstitutions,
                    blendMaterialCount);
            }
            catch (Exception uploadFailure)
            {
                Exception? rollbackFailure = ownership.TryRollback();
                if (rollbackFailure != null)
                {
                    PublishPendingMaterialRollbackLocked(
                        ownership);
                    throw new AggregateException(
                        "Imported material registration failed and ownership rollback was incomplete.",
                        uploadFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        private void BeginUploadLocked()
        {
            EnsureLifecycleLockHeld();
            if (_uploadInProgress)
            {
                string owner =
                    _uploadThreadId ==
                    Environment.CurrentManagedThreadId
                        ? "reentrant"
                        : "concurrent";
                throw new InvalidOperationException(
                    $"A {owner} model upload cannot start while another upload operation is active.");
            }

            _uploadInProgress = true;
            _uploadThreadId =
                Environment.CurrentManagedThreadId;
        }

        private void EndUploadLocked()
        {
            EnsureLifecycleLockHeld();
            _uploadInProgress = false;
            _uploadThreadId = 0;
        }

        private void PrepareForUploadLocked()
        {
            EnsureLifecycleLockHeld();
            ObjectDisposedException.ThrowIf(
                _disposeStarted,
                this);

            Exception? rollbackFailure =
                TryDrainPendingRollbacksLocked();
            if (rollbackFailure != null)
            {
                throw new AggregateException(
                    "A model upload cannot start while ownership rollback from a previous upload remains incomplete.",
                    rollbackFailure);
            }
        }

        private void PublishPendingMaterialRollbackLocked(
            ModelUploadOwnershipLedger ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);
            EnsureLifecycleLockHeld();
            if (_pendingMaterialOwnershipRollback != null)
            {
                throw new InvalidOperationException(
                    "A pending model material rollback is already published.");
            }
            if (ownership.RollbackCompleted)
            {
                throw new InvalidOperationException(
                    "A completed model material rollback cannot be published as pending.");
            }

            // Upload entry is serialized and drains a previous ledger before
            // acquiring new ownership, so one durable slot is sufficient and
            // publication cannot allocate or lose the failed occurrences.
            _pendingMaterialOwnershipRollback =
                ownership;
        }

        private void PublishPendingModelUploadRollbackLocked(
            ModelUploadRollbackLedger rollback)
        {
            ArgumentNullException.ThrowIfNull(rollback);
            EnsureLifecycleLockHeld();
            if (_pendingModelUploadRollback != null)
            {
                throw new InvalidOperationException(
                    "A pending full model-upload rollback is already published.");
            }
            if (rollback.RollbackCompleted)
            {
                throw new InvalidOperationException(
                    "A completed full model-upload rollback cannot be published as pending.");
            }

            _pendingModelUploadRollback =
                rollback;
        }

        private Exception? TryDrainPendingRollbacksLocked()
        {
            EnsureLifecycleLockHeld();
            List<Exception>? failures = null;

            Exception? materialFailure =
                TryDrainPendingMaterialRollbackLocked();
            if (materialFailure != null)
            {
                (failures ??= new List<Exception>())
                    .Add(materialFailure);
            }

            Exception? modelFailure =
                TryDrainPendingModelUploadRollbackLocked();
            if (modelFailure != null)
            {
                (failures ??= new List<Exception>())
                    .Add(modelFailure);
            }

            if (failures == null)
                return null;
            if (failures.Count == 1)
                return failures[0];
            return new AggregateException(
                "Multiple pending model-upload ownership ledgers remain incomplete.",
                failures);
        }

        private Exception? TryDrainPendingMaterialRollbackLocked()
        {
            EnsureLifecycleLockHeld();
            ModelUploadOwnershipLedger? pending =
                _pendingMaterialOwnershipRollback;
            if (pending == null)
                return null;

            Exception? rollbackFailure =
                pending.TryRollback();
            if (rollbackFailure != null)
                return rollbackFailure;
            if (!pending.RollbackCompleted ||
                pending.PendingResourceCount != 0)
            {
                return new InvalidOperationException(
                    "A model material ownership rollback returned without a failure but still owns pending resources.");
            }

            _pendingMaterialOwnershipRollback = null;
            return null;
        }

        private Exception? TryDrainPendingModelUploadRollbackLocked()
        {
            EnsureLifecycleLockHeld();
            ModelUploadRollbackLedger? pending =
                _pendingModelUploadRollback;
            if (pending == null)
                return null;

            Exception? rollbackFailure =
                pending.TryRollback();
            if (rollbackFailure != null)
                return rollbackFailure;
            if (!pending.RollbackCompleted ||
                pending.PendingResourceCount != 0)
            {
                return new InvalidOperationException(
                    "A full model-upload rollback returned without a failure but still owns pending resources.");
            }

            _pendingModelUploadRollback = null;
            return null;
        }

        private void EnsureLifecycleLockHeld()
        {
            if (!Monitor.IsEntered(_lifecycleLock))
            {
                throw new SynchronizationLockException(
                    "The model upload service lifecycle lock must be held.");
            }
        }

        private MaterialTextureBindings ResolveMaterialTextureBindings(
            ModelMaterial material,
            ref int defaultWhiteSubstitutions,
            ref int defaultNormalSubstitutions,
            ref int defaultBlackSubstitutions,
            ICollection<TextureHandle> pendingTextureOwnership)
        {
            TextureHandle ResolveTextureHandle(
                ModelTextureSlot? textureSlot,
                string? texturePath,
                TextureHandle fallback,
                ref int defaultSubstitutions,
                bool generateMipmaps,
                bool srgb,
                TextureSemantic semantic,
                RuntimeTextureMipPolicy mipPolicy = default)
            {
                TextureHandle handle = this.ResolveTextureHandle(
                    textureSlot,
                    texturePath,
                    fallback,
                    ref defaultSubstitutions,
                    generateMipmaps,
                    srgb,
                    semantic,
                    mipPolicy);
                pendingTextureOwnership.Add(handle);
                return handle;
            }

            TextureHandle albedoTexture = ResolveTextureHandle(
                material.BaseColorTexture,
                material.AlbedoTexturePath,
                _backend.DefaultWhiteTexture,
                ref defaultWhiteSubstitutions,
                generateMipmaps: ShouldGenerateAlbedoMipmaps(material),
                srgb: true,
                semantic: TextureSemantic.Color,
                mipPolicy: ResolveAlbedoRuntimeMipPolicy(material));
            TextureHandle normalTexture = ResolveTextureHandle(
                material.NormalTexture,
                material.NormalTexturePath,
                _backend.DefaultNormalTexture,
                ref defaultNormalSubstitutions,
                generateMipmaps: true,
                srgb: false,
                semantic: TextureSemantic.Normal);

            TextureHandle metallicRoughnessTexture = ResolveTextureHandle(
                material.MetallicRoughnessTexture,
                material.MetallicRoughnessTexturePath,
                _backend.DefaultBlackTexture,
                ref defaultBlackSubstitutions,
                generateMipmaps: true,
                srgb: false,
                semantic: TextureSemantic.Data);
            TextureHandle occlusionTexture = ResolveTextureHandle(
                material.OcclusionTexture,
                material.OcclusionTexturePath,
                _backend.DefaultWhiteTexture,
                ref defaultWhiteSubstitutions,
                generateMipmaps: true,
                srgb: false,
                semantic: TextureSemantic.Scalar);
            TextureHandle emissiveTexture = ResolveTextureHandle(
                material.EmissiveTexture,
                material.EmissiveTexturePath,
                // Emissive texture is multiplicative with ModelMaterial.Emissive. Missing
                // texture data must consequently resolve to white, not black.
                _backend.DefaultWhiteTexture,
                ref defaultWhiteSubstitutions,
                generateMipmaps: true,
                srgb: true,
                semantic: TextureSemantic.Color);

            TextureHandle clearcoatTexture = _backend.DefaultWhiteTexture;
            TextureHandle clearcoatRoughnessTexture = _backend.DefaultWhiteTexture;
            TextureHandle clearcoatNormalTexture = _backend.DefaultNormalTexture;
            TextureHandle sheenColorTexture = _backend.DefaultWhiteTexture;
            TextureHandle sheenRoughnessTexture = _backend.DefaultWhiteTexture;
            TextureHandle anisotropyTexture = _backend.DefaultWhiteTexture;
            TextureHandle transmissionTexture = _backend.DefaultWhiteTexture;
            TextureHandle thicknessTexture = _backend.DefaultWhiteTexture;
            TextureHandle subsurfaceTexture = _backend.DefaultWhiteTexture;
            TextureHandle specularTexture = _backend.DefaultWhiteTexture;
            TextureHandle specularColorTexture = _backend.DefaultWhiteTexture;
            TextureHandle iridescenceTexture = _backend.DefaultWhiteTexture;
            TextureHandle iridescenceThicknessTexture = _backend.DefaultWhiteTexture;

            MaterialFeatureFlags featureFlags = (MaterialFeatureFlags)material.FeatureFlags;
            if (featureFlags.RequiresExtensionData())
            {
                clearcoatTexture = ResolveTextureHandle(
                    material.ClearcoatTexture,
                    material.ClearcoatTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                clearcoatRoughnessTexture = ResolveTextureHandle(
                    material.ClearcoatRoughnessTexture,
                    material.ClearcoatRoughnessTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                clearcoatNormalTexture = ResolveTextureHandle(
                    material.ClearcoatNormalTexture,
                    material.ClearcoatNormalTexturePath,
                    _backend.DefaultNormalTexture,
                    ref defaultNormalSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Normal);
                sheenColorTexture = ResolveTextureHandle(
                    material.SheenColorTexture,
                    material.SheenColorTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true,
                    semantic: TextureSemantic.Color);
                sheenRoughnessTexture = ResolveTextureHandle(
                    material.SheenRoughnessTexture,
                    material.SheenRoughnessTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                anisotropyTexture = ResolveTextureHandle(
                    material.AnisotropyTexture,
                    material.AnisotropyTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Data);
                transmissionTexture = ResolveTextureHandle(
                    material.TransmissionTexture,
                    material.TransmissionTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                thicknessTexture = ResolveTextureHandle(
                    material.ThicknessTexture,
                    material.ThicknessTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                subsurfaceTexture = ResolveTextureHandle(
                    material.SubsurfaceTexture,
                    material.SubsurfaceTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true,
                    semantic: TextureSemantic.Color);
                specularTexture = ResolveTextureHandle(
                    material.SpecularTexture,
                    material.SpecularTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                specularColorTexture = ResolveTextureHandle(
                    material.SpecularColorTexture,
                    material.SpecularColorTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: true,
                    semantic: TextureSemantic.Color);
                iridescenceTexture = ResolveTextureHandle(
                    material.IridescenceTexture,
                    material.IridescenceTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
                iridescenceThicknessTexture = ResolveTextureHandle(
                    material.IridescenceThicknessTexture,
                    material.IridescenceThicknessTexturePath,
                    _backend.DefaultWhiteTexture,
                    ref defaultWhiteSubstitutions,
                    generateMipmaps: true,
                    srgb: false,
                    semantic: TextureSemantic.Scalar);
            }

            TextureHandle[] textureHandles = !featureFlags.RequiresExtensionData()
                ? new[]
                {
                    albedoTexture,
                    normalTexture,
                    metallicRoughnessTexture,
                    emissiveTexture,
                    occlusionTexture
                }
                : new[]
                {
                    albedoTexture,
                    normalTexture,
                    metallicRoughnessTexture,
                    emissiveTexture,
                    clearcoatTexture,
                    clearcoatRoughnessTexture,
                    clearcoatNormalTexture,
                    sheenColorTexture,
                    sheenRoughnessTexture,
                    anisotropyTexture,
                    transmissionTexture,
                    thicknessTexture,
                    subsurfaceTexture,
                    specularTexture,
                    specularColorTexture,
                    iridescenceTexture,
                    iridescenceThicknessTexture,
                    occlusionTexture
                };

            return new MaterialTextureBindings(
                new MaterialTextureIndices(
                    _backend.GetBindlessTextureIndex(albedoTexture),
                    _backend.GetBindlessTextureIndex(normalTexture),
                    _backend.GetBindlessTextureIndex(metallicRoughnessTexture),
                    _backend.GetBindlessTextureIndex(emissiveTexture),
                    _backend.GetBindlessTextureIndex(occlusionTexture)),
                new MaterialExtensionTextureIndices(
                    _backend.GetBindlessTextureIndex(clearcoatTexture),
                    _backend.GetBindlessTextureIndex(clearcoatRoughnessTexture),
                    _backend.GetBindlessTextureIndex(clearcoatNormalTexture),
                    _backend.GetBindlessTextureIndex(sheenColorTexture),
                    _backend.GetBindlessTextureIndex(sheenRoughnessTexture),
                    _backend.GetBindlessTextureIndex(anisotropyTexture),
                    _backend.GetBindlessTextureIndex(transmissionTexture),
                    _backend.GetBindlessTextureIndex(thicknessTexture),
                    _backend.GetBindlessTextureIndex(subsurfaceTexture),
                    _backend.GetBindlessTextureIndex(specularTexture),
                    _backend.GetBindlessTextureIndex(specularColorTexture),
                    _backend.GetBindlessTextureIndex(iridescenceTexture),
                    _backend.GetBindlessTextureIndex(iridescenceThicknessTexture)),
                textureHandles);
        }

        private MaterialDefinition BuildMaterialDefinition(
            ModelMaterial material,
            MaterialTextureBindings bindings,
            CookedMaterialPipeline? cookedPipeline = null)
        {
            MaterialFeatureFlags flags = (MaterialFeatureFlags)material.FeatureFlags;
            MaterialShadingModel shadingModel = cookedPipeline switch
            {
                CookedMaterialPipeline.Unlit => MaterialShadingModel.Unlit,
                CookedMaterialPipeline.Foliage => MaterialShadingModel.Foliage,
                CookedMaterialPipeline.Decal => MaterialShadingModel.Decal,
                _ when material.Unlit => MaterialShadingModel.Unlit,
                _ when (flags & MaterialFeatureFlags.Foliage) != MaterialFeatureFlags.None =>
                    MaterialShadingModel.Foliage,
                _ when material.IsGeometryDecal => MaterialShadingModel.Decal,
                _ when material.SubsurfaceStrength > 0f =>
                    MaterialShadingModel.SubsurfaceApproximation,
                _ => MaterialShadingModel.Pbr
            };
            MaterialAlphaMode alphaMode = cookedPipeline switch
            {
                CookedMaterialPipeline.Masked => MaterialAlphaMode.Mask,
                CookedMaterialPipeline.Blended => MaterialAlphaMode.Blend,
                CookedMaterialPipeline.Opaque => MaterialAlphaMode.Opaque,
                _ => material.AlphaMode switch
                {
                    ModelAlphaMode.Mask => MaterialAlphaMode.Mask,
                    ModelAlphaMode.Blend => MaterialAlphaMode.Blend,
                    _ => MaterialAlphaMode.Opaque
                }
            };

            var extensions = new MaterialExtensionDefinition
            {
                ClearcoatFactor = material.ClearcoatFactor,
                ClearcoatRoughness = material.ClearcoatRoughness,
                ClearcoatNormalScale = material.ClearcoatNormalScale,
                Clearcoat = CreateBinding(
                    material.ClearcoatTexture,
                    material.ClearcoatTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.ClearcoatTextureIndex)),
                ClearcoatRoughnessTexture = CreateBinding(
                    material.ClearcoatRoughnessTexture,
                    material.ClearcoatRoughnessTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.ClearcoatRoughnessTextureIndex)),
                ClearcoatNormal = CreateBinding(
                    material.ClearcoatNormalTexture,
                    material.ClearcoatNormalTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.ClearcoatNormalTextureIndex)),
                SheenColorFactor = new CoreVector3(
                    material.SheenColor.X,
                    material.SheenColor.Y,
                    material.SheenColor.Z),
                SheenRoughness = material.SheenRoughness,
                SheenColor = CreateBinding(
                    material.SheenColorTexture,
                    material.SheenColorTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.SheenColorTextureIndex)),
                SheenRoughnessTexture = CreateBinding(
                    material.SheenRoughnessTexture,
                    material.SheenRoughnessTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.SheenRoughnessTextureIndex)),
                AnisotropyStrength = material.AnisotropyStrength,
                AnisotropyRotation = material.AnisotropyRotation,
                Anisotropy = CreateBinding(
                    material.AnisotropyTexture,
                    material.AnisotropyTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.AnisotropyTextureIndex)),
                TransmissionFactor = material.TransmissionFactor,
                ThinTransmissionTint = new CoreVector3(
                    material.ThinTransmissionTint.X,
                    material.ThinTransmissionTint.Y,
                    material.ThinTransmissionTint.Z),
                Ior = material.Ior,
                ThicknessFactor = material.ThicknessFactor,
                AttenuationDistance = material.AttenuationDistance,
                AttenuationColor = new CoreVector3(
                    material.AttenuationColor.X,
                    material.AttenuationColor.Y,
                    material.AttenuationColor.Z),
                Transmission = CreateBinding(
                    material.TransmissionTexture,
                    material.TransmissionTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.TransmissionTextureIndex)),
                Thickness = CreateBinding(
                    material.ThicknessTexture,
                    material.ThicknessTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.ThicknessTextureIndex)),
                TransmissionPolicy = material.TransmissionFactor <= 0f
                    ? GiTransmissionPolicy.None
                    : material.GiTransmissionPolicy switch
                    {
                        ModelGiTransmissionPolicy.ThinSurface => GiTransmissionPolicy.ThinSurface,
                        ModelGiTransmissionPolicy.Volume => GiTransmissionPolicy.Volume,
                        ModelGiTransmissionPolicy.Unsupported => GiTransmissionPolicy.Unsupported,
                        // KHR_materials_transmission is not enough evidence to
                        // classify a zero-thickness cloth sheet.
                        _ => GiTransmissionPolicy.Unsupported
                    },
                OpticalBoundary = material.OpticalBoundaryKind switch
                {
                    ModelOpticalBoundaryKind.WaterSurface =>
                        OpticalBoundaryKind.WaterSurface,
                    _ => OpticalBoundaryKind.ClosedVolume
                },
                CausticCasterPolicy = material.GiCausticCasterPolicy switch
                {
                    ModelGiCausticCasterPolicy.Disabled =>
                        GiCausticCasterPolicy.Disabled,
                    ModelGiCausticCasterPolicy.Mirror =>
                        GiCausticCasterPolicy.Mirror,
                    ModelGiCausticCasterPolicy.RoughSpecular =>
                        GiCausticCasterPolicy.RoughSpecular,
                    ModelGiCausticCasterPolicy.DielectricPriority =>
                        GiCausticCasterPolicy.DielectricPriority,
                    _ => GiCausticCasterPolicy.Default
                },
                WaterNormalVelocity0 = new CoreVector2(
                    material.WaterNormalVelocity0.X,
                    material.WaterNormalVelocity0.Y),
                WaterNormalVelocity1 = new CoreVector2(
                    material.WaterNormalVelocity1.X,
                    material.WaterNormalVelocity1.Y),
                WaterNormalUvScale0 = material.WaterNormalUvScale0,
                WaterNormalUvScale1 = material.WaterNormalUvScale1,
                CausticParticipation = material.GiCausticParticipation switch
                {
                    ModelGiCausticParticipationMode.MirrorHero =>
                        GiCausticParticipationMode.MirrorHero,
                    ModelGiCausticParticipationMode.ClosedDielectricHero =>
                        GiCausticParticipationMode.ClosedDielectricHero,
                    ModelGiCausticParticipationMode.RoughSpecularReference =>
                        GiCausticParticipationMode.RoughSpecularReference,
                    _ => GiCausticParticipationMode.None
                },
                SpecularFactor = material.SpecularFactor,
                SpecularColorFactor = new CoreVector3(
                    material.SpecularColor.X,
                    material.SpecularColor.Y,
                    material.SpecularColor.Z),
                Specular = CreateBinding(
                    material.SpecularTexture,
                    material.SpecularTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.SpecularTextureIndex)),
                SpecularColor = CreateBinding(
                    material.SpecularColorTexture,
                    material.SpecularColorTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.SpecularColorTextureIndex)),
                IridescenceFactor = material.IridescenceFactor,
                IridescenceIor = material.IridescenceIor,
                IridescenceThicknessMinimum = material.IridescenceThicknessMinimum,
                IridescenceThicknessMaximum = material.IridescenceThicknessMaximum,
                Iridescence = CreateBinding(
                    material.IridescenceTexture,
                    material.IridescenceTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.IridescenceTextureIndex)),
                IridescenceThickness = CreateBinding(
                    material.IridescenceThicknessTexture,
                    material.IridescenceThicknessTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.IridescenceThicknessTextureIndex)),
                Dispersion = material.Dispersion,
                SubsurfaceColor = new CoreVector3(
                    material.SubsurfaceColor.X,
                    material.SubsurfaceColor.Y,
                    material.SubsurfaceColor.Z),
                SubsurfaceStrength = material.SubsurfaceStrength,
                Subsurface = CreateBinding(
                    material.SubsurfaceTexture,
                    material.SubsurfaceTexturePath,
                    ResolveTextureHandle(bindings, bindings.ExtensionTextureIndices.SubsurfaceTextureIndex))
            };

            return new MaterialDefinition
            {
                Name = material.Name,
                BaseColorFactor = material.Albedo,
                EmissiveFactor = new CoreVector3(
                    material.Emissive.X,
                    material.Emissive.Y,
                    material.Emissive.Z),
                EmissiveStrength = material.EmissiveStrength,
                MetallicFactor = material.Metallic,
                RoughnessFactor = material.Roughness,
                OcclusionStrength = material.AmbientOcclusion,
                NormalScale = material.NormalScale,
                BaseColor = CreateBinding(
                    material.BaseColorTexture,
                    material.AlbedoTexturePath,
                    ResolveTextureHandle(bindings, bindings.TextureIndices.AlbedoTextureIndex)),
                Normal = CreateBinding(
                    material.NormalTexture,
                    material.NormalTexturePath,
                    ResolveTextureHandle(bindings, bindings.TextureIndices.NormalTextureIndex)),
                MetallicRoughness = CreateBinding(
                    material.MetallicRoughnessTexture,
                    material.MetallicRoughnessTexturePath,
                    ResolveTextureHandle(bindings, bindings.TextureIndices.MetallicRoughnessTextureIndex)),
                Occlusion = CreateBinding(
                    material.OcclusionTexture,
                    material.OcclusionTexturePath,
                    ResolveTextureHandle(bindings, bindings.TextureIndices.OcclusionTextureIndex)),
                Emissive = CreateBinding(
                    material.EmissiveTexture,
                    material.EmissiveTexturePath,
                    ResolveTextureHandle(bindings, bindings.TextureIndices.EmissiveTextureIndex)),
                AlphaMode = alphaMode,
                AlphaCutoff = material.AlphaCutoff,
                DoubleSided = material.DoubleSided,
                ShadingModel = shadingModel,
                FeatureFlags = flags,
                Extensions = extensions,
                IsGeometryDecal = material.IsGeometryDecal,
                DecalLayer = material.DecalLayer,
                DecalDepthBias = material.DecalDepthBias
            };
        }

        private TextureHandle ResolveTextureHandle(
            MaterialTextureBindings bindings,
            int bindlessIndex)
        {
            foreach (TextureHandle handle in bindings.TextureHandles)
            {
                if (handle.IsValid &&
                    _backend.GetBindlessTextureIndex(handle) == bindlessIndex)
                {
                    return handle;
                }
            }
            return TextureHandle.Invalid;
        }

        private static MaterialTextureBinding CreateBinding(
            ModelTextureSlot? slot,
            string? legacyPath,
            TextureHandle handle)
        {
            bool authored = slot?.Source != null || !string.IsNullOrWhiteSpace(legacyPath);
            if (!authored || !handle.IsValid)
                return MaterialTextureBinding.Missing;

            return new MaterialTextureBinding
            {
                Texture = handle,
                Sampler = slot?.Sampler ?? TextureSamplerDescription.Default,
                TexCoordSet = Math.Clamp(slot?.TexCoordSet ?? 0, 0, 1),
                Offset = slot?.Offset ?? CoreVector2.Zero,
                Scale = slot?.Scale ?? CoreVector2.One,
                RotationRadians = slot?.RotationRadians ?? 0f
            };
        }

        internal static GPUMaterialData BuildGpuMaterialData(
            ModelMaterial material,
            MaterialTextureIndices textureIndices,
            CoreVector4? runtimeBaseColorTextureAverageLinear = null)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));
            float alphaCutoff = ValidateAlphaCutoff(material.AlphaCutoff);

            bool hasBaseColorTexture =
                material.BaseColorTexture != null ||
                !string.IsNullOrWhiteSpace(material.AlbedoTexturePath);
            bool compactAlbedoValid =
                !hasBaseColorTexture ||
                material.DdgiBaseColorTextureAverageLinear.HasValue ||
                runtimeBaseColorTextureAverageLinear.HasValue;
            return new GPUMaterialData
            {
                Albedo = material.Albedo,
                Emissive = material.Emissive,
                NormalScaleBias = new CoreVector4(
                    material.NormalScale,
                    ToGpuAlphaModeCode(material.AlphaMode),
                    alphaCutoff,
                    material.DoubleSided ? 1f : 0f),
                MetallicRoughnessAO = new CoreVector4(
                    Math.Clamp(material.Metallic, 0f, 1f),
                    Math.Clamp(material.Roughness, 0.04f, 1f),
                    Math.Clamp(material.AmbientOcclusion, 0f, 1f),
                    ShouldSampleOcclusionFromMetallicRoughnessTexture(material) ? 1f : 0f),
                BaseColorOffsetScale = ToOffsetScale(material.BaseColorTexture),
                NormalOffsetScale = ToOffsetScale(material.NormalTexture),
                MetallicRoughnessOffsetScale = ToOffsetScale(material.MetallicRoughnessTexture),
                OcclusionOffsetScale = ToOffsetScale(material.OcclusionTexture),
                EmissiveOffsetScale = ToOffsetScale(material.EmissiveTexture),
                TextureRotations = new CoreVector4(
                    material.BaseColorTexture?.RotationRadians ?? 0f,
                    material.NormalTexture?.RotationRadians ?? 0f,
                    material.MetallicRoughnessTexture?.RotationRadians ?? 0f,
                    material.EmissiveTexture?.RotationRadians ?? 0f),
                TextureTexCoordSets = new CoreVector4(
                    Math.Clamp(material.BaseColorTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.NormalTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.MetallicRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.EmissiveTexture?.TexCoordSet ?? 0, 0, 1)),
                OcclusionBinding = new CoreVector4(
                    material.OcclusionTexture?.RotationRadians ?? 0f,
                    Math.Clamp(material.OcclusionTexture?.TexCoordSet ?? 0, 0, 1),
                    0f,
                    0f),
                AlbedoTextureIndex = textureIndices.AlbedoTextureIndex,
                NormalTextureIndex = textureIndices.NormalTextureIndex,
                MetallicRoughnessTextureIndex = textureIndices.MetallicRoughnessTextureIndex,
                OcclusionTextureIndex = textureIndices.OcclusionTextureIndex,
                EmissiveTextureIndex = textureIndices.EmissiveTextureIndex,
                FeatureFlags = material.FeatureFlags,
                ExtensionDataIndex = -1,
                TransportFlags = BuildLegacyTransportFlags(material, compactAlbedoValid),
                TransportProfileRevision = 0u,
                PackedMeanMetallicRoughness = 0u,
                TransportProfileQuality = 0u,
                MaterialRevision = 0u,
                DdgiAverageAlbedo = BuildDdgiAverageAlbedo(material, runtimeBaseColorTextureAverageLinear),
                DdgiAverageEmissive = BuildDdgiAverageEmissive(material),
                DdgiMaterialPolicy = BuildDdgiMaterialPolicy(material, compactAlbedoValid)
            };
        }

        internal static CoreVector4 BuildDdgiAverageAlbedo(
            ModelMaterial material,
            CoreVector4? runtimeBaseColorTextureAverageLinear = null)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            CoreVector4 textureAverage =
                material.DdgiBaseColorTextureAverageLinear ??
                runtimeBaseColorTextureAverageLinear ??
                new CoreVector4(1f, 1f, 1f, 1f);
            return new CoreVector4(
                Math.Max(material.Albedo.X, 0f) * Math.Max(textureAverage.X, 0f),
                Math.Max(material.Albedo.Y, 0f) * Math.Max(textureAverage.Y, 0f),
                Math.Max(material.Albedo.Z, 0f) * Math.Max(textureAverage.Z, 0f),
                Math.Clamp(material.Albedo.W, 0f, 1f) * Math.Clamp(textureAverage.W, 0f, 1f));
        }

        internal static CoreVector4 BuildDdgiAverageEmissive(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            float strength = Math.Clamp(material.EmissiveStrength, 0f, 128f);
            float x = Math.Max(material.Emissive.X, 0f) * strength;
            float y = Math.Max(material.Emissive.Y, 0f) * strength;
            float z = Math.Max(material.Emissive.Z, 0f) * strength;
            float importance = CalculateDdgiEmissiveImportance(x, y, z);
            return new CoreVector4(x, y, z, importance);
        }

        internal static CoreVector4 BuildDdgiMaterialPolicy(ModelMaterial material, bool compactAlbedoValid)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            CoreVector4 emissive = BuildDdgiAverageEmissive(material);
            float preferredMip = ResolvePreferredDdgiTextureMip(material);
            uint flags = compactAlbedoValid ? 1u << 2 : 0u;
            if (material.BaseColorTexture != null || !string.IsNullOrWhiteSpace(material.AlbedoTexturePath))
                flags |= 1u;
            if (material.EmissiveTexture != null || !string.IsNullOrWhiteSpace(material.EmissiveTexturePath))
                flags |= 1u << 1;

            return new CoreVector4(
                ToGpuAlphaModeCode(material.AlphaMode),
                preferredMip,
                emissive.W,
                flags);
        }

        private static uint BuildLegacyTransportFlags(ModelMaterial material, bool compactAlbedoValid)
        {
            GiMaterialTransportFlags flags =
                GiMaterialTransportFlags.LegacyV1Fallback |
                GiMaterialTransportFlags.EmissionProfileValid |
                GiMaterialTransportFlags.AlphaProfileValid |
                GiMaterialTransportFlags.ReceivesIndirectDiffuse |
                GiMaterialTransportFlags.ReflectsIndirectDiffuse;
            if (compactAlbedoValid)
                flags |= GiMaterialTransportFlags.BaseStatisticsValid |
                         GiMaterialTransportFlags.DiffuseProfileValid;
            if (material.DoubleSided)
                flags |= GiMaterialTransportFlags.DoubleSided;
            if (material.Unlit)
                flags |= GiMaterialTransportFlags.Unlit;
            if (material.BaseColorTexture != null || !string.IsNullOrWhiteSpace(material.AlbedoTexturePath))
                flags |= GiMaterialTransportFlags.HasBaseColorTexture;
            if (material.MetallicRoughnessTexture != null || !string.IsNullOrWhiteSpace(material.MetallicRoughnessTexturePath))
                flags |= GiMaterialTransportFlags.HasMetallicRoughnessTexture;
            if (material.OcclusionTexture != null || !string.IsNullOrWhiteSpace(material.OcclusionTexturePath))
                flags |= GiMaterialTransportFlags.HasOcclusionTexture;
            if (material.EmissiveTexture != null || !string.IsNullOrWhiteSpace(material.EmissiveTexturePath))
                flags |= GiMaterialTransportFlags.HasEmissiveTexture;
            if (material.IsGeometryDecal)
                flags |= GiMaterialTransportFlags.GeometryDecal;
            return (uint)flags;
        }

        private static float ResolvePreferredDdgiTextureMip(ModelMaterial material)
        {
            bool hasDdgiTexture =
                material.BaseColorTexture != null ||
                material.EmissiveTexture != null ||
                !string.IsNullOrWhiteSpace(material.AlbedoTexturePath) ||
                !string.IsNullOrWhiteSpace(material.EmissiveTexturePath);
            return hasDdgiTexture ? 2.0f : 0.0f;
        }

        internal static float CalculateDdgiEmissiveImportance(float x, float y, float z)
        {
            return Math.Max(0f, 0.2126f * x + 0.7152f * y + 0.0722f * z);
        }

        public static GPUMaterialExtensionData BuildGpuMaterialExtensionData(
            ModelMaterial material,
            MaterialExtensionTextureIndices textureIndices)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            float attenuationDistance = float.IsFinite(material.AttenuationDistance)
                ? Math.Max(material.AttenuationDistance, 0f)
                : 0f;

            return new GPUMaterialExtensionData
            {
                Clearcoat = new CoreVector4(
                    Math.Clamp(material.ClearcoatFactor, 0f, 1f),
                    Math.Clamp(material.ClearcoatRoughness, 0f, 1f),
                    Math.Clamp(material.ClearcoatNormalScale, 0f, 4f),
                    Math.Clamp(material.EmissiveStrength, 0f, 128f)),
                SheenColor = new CoreVector4(
                    Math.Max(material.SheenColor.X, 0f),
                    Math.Max(material.SheenColor.Y, 0f),
                    Math.Max(material.SheenColor.Z, 0f),
                    Math.Clamp(material.SheenRoughness, 0f, 1f)),
                Anisotropy = new CoreVector4(
                    Math.Clamp(material.AnisotropyStrength, 0f, 1f),
                    material.AnisotropyRotation,
                    0f,
                    0f),
                Transmission = new CoreVector4(
                    Math.Clamp(material.TransmissionFactor, 0f, 1f),
                    Math.Clamp(material.Ior, 1f, 3f),
                    Math.Max(material.ThicknessFactor, 0f),
                    attenuationDistance),
                AttenuationColor = new CoreVector4(
                    Math.Max(material.AttenuationColor.X, 0f),
                    Math.Max(material.AttenuationColor.Y, 0f),
                    Math.Max(material.AttenuationColor.Z, 0f),
                    0f),
                Subsurface = new CoreVector4(
                    Math.Max(material.SubsurfaceColor.X, 0f),
                    Math.Max(material.SubsurfaceColor.Y, 0f),
                    Math.Max(material.SubsurfaceColor.Z, 0f),
                    Math.Clamp(material.SubsurfaceStrength, 0f, 1f)),
                SpecularColor = new CoreVector4(
                    Math.Max(material.SpecularColor.X, 0f),
                    Math.Max(material.SpecularColor.Y, 0f),
                    Math.Max(material.SpecularColor.Z, 0f),
                    Math.Clamp(material.SpecularFactor, 0f, 1f)),
                Iridescence = new CoreVector4(
                    Math.Clamp(material.IridescenceFactor, 0f, 1f),
                    Math.Clamp(material.IridescenceIor, 1f, 3f),
                    Math.Max(material.IridescenceThicknessMinimum, 0f),
                    Math.Max(material.IridescenceThicknessMaximum, 0f)),
                Dispersion = new CoreVector4(
                    Math.Max(material.Dispersion, 0f),
                    Math.Clamp(material.ThinTransmissionTint.X, 0f, 1f),
                    Math.Clamp(material.ThinTransmissionTint.Y, 0f, 1f),
                    Math.Clamp(material.ThinTransmissionTint.Z, 0f, 1f)),
                ClearcoatOffsetScale = ToOffsetScale(material.ClearcoatTexture),
                ClearcoatRoughnessOffsetScale = ToOffsetScale(material.ClearcoatRoughnessTexture),
                ClearcoatNormalOffsetScale = ToOffsetScale(material.ClearcoatNormalTexture),
                SheenColorOffsetScale = ToOffsetScale(material.SheenColorTexture),
                SheenRoughnessOffsetScale = ToOffsetScale(material.SheenRoughnessTexture),
                AnisotropyOffsetScale = ToOffsetScale(material.AnisotropyTexture),
                TransmissionOffsetScale = ToOffsetScale(material.TransmissionTexture),
                ThicknessOffsetScale = ToOffsetScale(material.ThicknessTexture),
                SpecularOffsetScale = ToOffsetScale(material.SpecularTexture),
                SpecularColorOffsetScale = ToOffsetScale(material.SpecularColorTexture),
                IridescenceOffsetScale = ToOffsetScale(material.IridescenceTexture),
                IridescenceThicknessOffsetScale = ToOffsetScale(material.IridescenceThicknessTexture),
                SubsurfaceOffsetScale = ToOffsetScale(material.SubsurfaceTexture),
                ExtensionTextureRotations0 = new CoreVector4(
                    material.ClearcoatTexture?.RotationRadians ?? 0f,
                    material.ClearcoatRoughnessTexture?.RotationRadians ?? 0f,
                    material.ClearcoatNormalTexture?.RotationRadians ?? 0f,
                    material.SheenColorTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations1 = new CoreVector4(
                    material.SheenRoughnessTexture?.RotationRadians ?? 0f,
                    material.AnisotropyTexture?.RotationRadians ?? 0f,
                    material.TransmissionTexture?.RotationRadians ?? 0f,
                    material.ThicknessTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations2 = new CoreVector4(
                    material.SpecularTexture?.RotationRadians ?? 0f,
                    material.SpecularColorTexture?.RotationRadians ?? 0f,
                    material.IridescenceTexture?.RotationRadians ?? 0f,
                    material.IridescenceThicknessTexture?.RotationRadians ?? 0f),
                ExtensionTextureRotations3 = new CoreVector4(
                    material.SubsurfaceTexture?.RotationRadians ?? 0f,
                    0f,
                    0f,
                    0f),
                ExtensionTextureTexCoordSets0 = new CoreVector4(
                    Math.Clamp(material.ClearcoatTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ClearcoatRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ClearcoatNormalTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.SheenColorTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets1 = new CoreVector4(
                    Math.Clamp(material.SheenRoughnessTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.AnisotropyTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.TransmissionTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.ThicknessTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets2 = new CoreVector4(
                    Math.Clamp(material.SpecularTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.SpecularColorTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.IridescenceTexture?.TexCoordSet ?? 0, 0, 1),
                    Math.Clamp(material.IridescenceThicknessTexture?.TexCoordSet ?? 0, 0, 1)),
                ExtensionTextureTexCoordSets3 = new CoreVector4(
                    Math.Clamp(material.SubsurfaceTexture?.TexCoordSet ?? 0, 0, 1),
                    0f,
                    0f,
                    0f),
                ClearcoatTextureIndex = textureIndices.ClearcoatTextureIndex,
                ClearcoatRoughnessTextureIndex = textureIndices.ClearcoatRoughnessTextureIndex,
                ClearcoatNormalTextureIndex = textureIndices.ClearcoatNormalTextureIndex,
                SheenColorTextureIndex = textureIndices.SheenColorTextureIndex,
                SheenRoughnessTextureIndex = textureIndices.SheenRoughnessTextureIndex,
                AnisotropyTextureIndex = textureIndices.AnisotropyTextureIndex,
                TransmissionTextureIndex = textureIndices.TransmissionTextureIndex,
                ThicknessTextureIndex = textureIndices.ThicknessTextureIndex,
                SubsurfaceTextureIndex = textureIndices.SubsurfaceTextureIndex,
                SpecularTextureIndex = textureIndices.SpecularTextureIndex,
                SpecularColorTextureIndex = textureIndices.SpecularColorTextureIndex,
                IridescenceTextureIndex = textureIndices.IridescenceTextureIndex,
                IridescenceThicknessTextureIndex = textureIndices.IridescenceThicknessTextureIndex,
                Padding0 = OpticalMaterialGpuContract.PackFlags(
                    material.OpticalBoundaryKind ==
                        ModelOpticalBoundaryKind.WaterSurface
                        ? OpticalBoundaryKind.WaterSurface
                        : OpticalBoundaryKind.ClosedVolume,
                    material.GiCausticCasterPolicy switch
                    {
                        ModelGiCausticCasterPolicy.Disabled =>
                            GiCausticCasterPolicy.Disabled,
                        ModelGiCausticCasterPolicy.Mirror =>
                            GiCausticCasterPolicy.Mirror,
                        ModelGiCausticCasterPolicy.RoughSpecular =>
                            GiCausticCasterPolicy.RoughSpecular,
                        ModelGiCausticCasterPolicy.DielectricPriority =>
                            GiCausticCasterPolicy.DielectricPriority,
                        _ => GiCausticCasterPolicy.Default
                    },
                    material.GiTransmissionPolicy ==
                        ModelGiTransmissionPolicy.Volume &&
                    material.TransmissionFactor > 0f),
                Padding1 = OpticalMaterialGpuContract.PackHalf2(
                    new CoreVector2(material.WaterNormalVelocity0.X,
                        material.WaterNormalVelocity0.Y)),
                Padding2 = OpticalMaterialGpuContract.PackHalf2(
                    new CoreVector2(material.WaterNormalVelocity1.X,
                        material.WaterNormalVelocity1.Y)),
                Padding3 = OpticalMaterialGpuContract.PackHalf2(
                    new CoreVector2(material.WaterNormalUvScale0,
                        material.WaterNormalUvScale1))
            };
        }

        private static CoreVector4 ToOffsetScale(ModelTextureSlot? slot)
        {
            return slot == null
                ? new CoreVector4(0f, 0f, 1f, 1f)
                : new CoreVector4(slot.Offset.X, slot.Offset.Y, slot.Scale.X, slot.Scale.Y);
        }

        private static float ToGpuAlphaModeCode(ModelAlphaMode alphaMode)
        {
            return alphaMode switch
            {
                ModelAlphaMode.Mask => MaterialRenderMode.Mask.ToGpuAlphaModeCode(),
                ModelAlphaMode.Blend => MaterialRenderMode.Blend.ToGpuAlphaModeCode(),
                _ => MaterialRenderMode.Opaque.ToGpuAlphaModeCode()
            };
        }

        public static MaterialRenderMetadata BuildMaterialRenderMetadata(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));
            float alphaCutoff = ValidateAlphaCutoff(material.AlphaCutoff);

            MaterialSurfaceFlags flags = MaterialSurfaceFlags.ReceivesShadows;
            if (material.DoubleSided)
                flags |= MaterialSurfaceFlags.DoubleSided;
            if (material.IsGeometryDecal)
                flags |= MaterialSurfaceFlags.GeometryDecal;

            GiTransmissionPolicy transmissionPolicy = material.TransmissionFactor <= 0f
                ? GiTransmissionPolicy.None
                : material.GiTransmissionPolicy switch
                {
                    ModelGiTransmissionPolicy.ThinSurface => GiTransmissionPolicy.ThinSurface,
                    ModelGiTransmissionPolicy.Volume => GiTransmissionPolicy.Volume,
                    ModelGiTransmissionPolicy.Unsupported => GiTransmissionPolicy.Unsupported,
                    _ => GiTransmissionPolicy.Unsupported
                };
            bool requiresTransparentPass =
                transmissionPolicy != GiTransmissionPolicy.ThinSurface &&
                ((MaterialFeatureFlags)material.FeatureFlags).RequiresTransparentPass();

            return new MaterialRenderMetadata
            {
                BlendMode = requiresTransparentPass
                    ? MaterialBlendMode.AlphaBlend
                    : material.AlphaMode switch
                    {
                        ModelAlphaMode.Mask => MaterialBlendMode.Mask,
                        ModelAlphaMode.Blend => MaterialBlendMode.AlphaBlend,
                        _ => MaterialBlendMode.Opaque
                    },
                SurfaceFlags = flags,
                AlphaCutoff = alphaCutoff,
                TransmissionPolicy = transmissionPolicy,
                DecalLayer = material.DecalLayer,
                DecalDepthBias = Math.Clamp(material.DecalDepthBias, 0f, 0.01f)
            };
        }

        private static float ValidateAlphaCutoff(float alphaCutoff)
        {
            if (!float.IsFinite(alphaCutoff) || alphaCutoff < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alphaCutoff),
                    "Material alpha cutoff must be finite and non-negative.");
            }

            return alphaCutoff;
        }

        private static bool ShouldSampleOcclusionFromMetallicRoughnessTexture(ModelMaterial material)
        {
            if (material.MetallicRoughnessTexture?.Source != null && material.OcclusionTexture?.Source != null)
            {
                return string.Equals(
                    material.MetallicRoughnessTexture.Source.CacheIdentity,
                    material.OcclusionTexture.Source.CacheIdentity,
                    StringComparison.Ordinal);
            }

            return !string.IsNullOrWhiteSpace(material.MetallicRoughnessTexturePath) &&
                   !string.IsNullOrWhiteSpace(material.OcclusionTexturePath) &&
                   string.Equals(
                       Path.GetFullPath(material.MetallicRoughnessTexturePath),
                       Path.GetFullPath(material.OcclusionTexturePath),
                       StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldGenerateAlbedoMipmaps(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            return material.AlphaMode != ModelAlphaMode.Blend;
        }

        public static bool RequiresAlphaCoveragePreservingMips(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            return ModelMaterialTexturePolicy.ResolveBaseColorMipPolicy(material)
                .PreserveAlphaCoverage;
        }

        public static RuntimeTextureMipPolicy ResolveAlbedoRuntimeMipPolicy(ModelMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            ModelTextureMipPolicy mipPolicy =
                ModelMaterialTexturePolicy.ResolveBaseColorMipPolicy(material);
            return mipPolicy.PreserveAlphaCoverage
                ? RuntimeTextureMipPolicy.AlphaMask(
                    ValidateAlphaCutoff(mipPolicy.AlphaCutoff))
                : RuntimeTextureMipPolicy.Default;
        }

        private TextureHandle ResolveTextureHandle(
            ModelTextureSlot? textureSlot,
            string? texturePath,
            TextureHandle fallback,
            ref int defaultSubstitutions,
            bool generateMipmaps,
            bool srgb,
            TextureSemantic semantic,
            RuntimeTextureMipPolicy mipPolicy = default)
        {
            if (!fallback.IsValid)
                throw new InvalidOperationException("Default textures must be initialized before material upload.");

            if (textureSlot?.Source != null)
            {
                bool slotSrgb = textureSlot.ColorSpace == TextureColorSpace.Srgb;
                TextureHandle slotTexture = _backend.LoadTexture(
                    textureSlot.Source,
                    textureSlot.Sampler,
                    generateMipmaps,
                    slotSrgb,
                    requireWithinMemoryBudget: false,
                    semantic: semantic,
                    mipPolicy: mipPolicy);
                return slotTexture.IsValid ? slotTexture : fallback;
            }

            if (!string.IsNullOrWhiteSpace(texturePath) && !File.Exists(Path.GetFullPath(texturePath)))
            {
                string fullPath = Path.GetFullPath(texturePath);
                throw new FileNotFoundException($"Imported material texture was not found: {fullPath}", fullPath);
            }

            bool useFallback = string.IsNullOrWhiteSpace(texturePath);
            TextureHandle texture = _backend.LoadOptionalTextureFromFile(
                texturePath,
                fallback,
                generateMipmaps: generateMipmaps,
                srgb: srgb,
                semantic: semantic,
                mipPolicy: mipPolicy);

            if (useFallback || texture == fallback)
                defaultSubstitutions++;

            return texture;
        }

        private void RegisterModelMaterialLifetime(Model model, IReadOnlyList<MaterialHandle> materials)
        {
            var releases = new DurableMaterialReleaseBatch(
                materials,
                _backend.ReleaseMaterial);
            model.AddDisposeAction(
                releases.ReleaseOutstanding);
        }

        private int RegisterCookedOpacityMicromaps(
            CookedModelAsset cooked,
            IReadOnlyList<MeshManager.MeshRegistrationData> meshRegistrations,
            IReadOnlyList<MeshHandle> meshes,
            IReadOnlyList<MaterialHandle> materials,
            out string detail)
        {
            CookedOpacityMicromapPayloadLoadStatus loadStatus =
                cooked.OpacityMicromapLoadStatus;
            if (!loadStatus.SectionPresent)
            {
                detail = "opacity-micromap-section-absent";
                return 0;
            }
            if (!loadStatus.Accepted)
            {
                detail = string.IsNullOrWhiteSpace(loadStatus.Detail)
                    ? "opacity-micromap-section-rejected"
                    : loadStatus.Detail;
                return 0;
            }

            OpacityMicromapCookedPayload? payload =
                cooked.OpacityMicromapPayload;
            if (payload is null)
            {
                detail =
                    "opacity-micromap-load-status-accepted-without-payload";
                return 0;
            }
            int subMeshCount = cooked.Mesh.SubMeshes.Count;
            if (subMeshCount == 0 ||
                meshRegistrations.Count != subMeshCount ||
                meshes.Count != subMeshCount ||
                materials.Count != subMeshCount)
            {
                detail =
                    "opacity-micromap-runtime-submesh-domain-count-mismatch";
                return 0;
            }
            if (!CookedOpacityMicromapModelChunk.TryValidateModelAttachment(
                    payload,
                    cooked.Mesh,
                    cooked.Materials,
                    out _,
                    out string attachmentDetail))
            {
                detail =
                    "opacity-micromap-runtime-" + attachmentDetail;
                return 0;
            }

            var contractBySubMesh =
                new OpacityMicromapMaterialContract?[subMeshCount];
            int mappedContractCount = 0;
            foreach (OpacityMicromapMaterialContract contract in
                     payload.MaterialContracts)
            {
                int matchingSubMesh = -1;
                for (int subMeshIndex = 0;
                     subMeshIndex < subMeshCount;
                     subMeshIndex++)
                {
                    CookedSubMeshRecord candidate =
                        cooked.Mesh.SubMeshes[subMeshIndex];
                    if (candidate.IndexOffset < 0 ||
                        candidate.IndexCount <= 0 ||
                        candidate.IndexOffset % 3 != 0 ||
                        candidate.IndexCount % 3 != 0)
                    {
                        continue;
                    }

                    if (contract.FirstPrimitive ==
                            checked((uint)(candidate.IndexOffset / 3)) &&
                        contract.PrimitiveCount ==
                            checked((uint)(candidate.IndexCount / 3)) &&
                        contract.MaterialSlot ==
                            checked((uint)candidate.MaterialSlot))
                    {
                        if (matchingSubMesh >= 0)
                        {
                            detail =
                                "opacity-micromap-runtime-material-domain-ambiguous";
                            return 0;
                        }
                        matchingSubMesh = subMeshIndex;
                    }
                }

                if (matchingSubMesh < 0 ||
                    contractBySubMesh[matchingSubMesh].HasValue)
                {
                    detail =
                        "opacity-micromap-runtime-material-domain-mismatch";
                    return 0;
                }
                contractBySubMesh[matchingSubMesh] = contract;
                mappedContractCount++;
            }
            if (mappedContractCount != payload.MaterialContracts.Count)
            {
                detail =
                    "opacity-micromap-runtime-material-domain-incomplete";
                return 0;
            }

            var pendingRegistrations =
                new List<OpacityMicromapRuntimeMeshRegistration>(
                    mappedContractCount);
            string lastFallbackDetail =
                "opacity-micromap-runtime-no-eligible-submesh";
            for (int subMeshIndex = 0;
                 subMeshIndex < subMeshCount;
                 subMeshIndex++)
            {
                if (contractBySubMesh[subMeshIndex] is not { } contract)
                    continue;

                CookedSubMeshRecord subMesh =
                    cooked.Mesh.SubMeshes[subMeshIndex];
                MeshManager.MeshRegistrationData registration =
                    meshRegistrations[subMeshIndex];
                if (subMesh.SkinIndex >= 0 || registration.IsSkinned)
                {
                    lastFallbackDetail =
                        "opacity-micromap-runtime-rejects-deforming-submesh";
                    continue;
                }
                if (registration.Indices.Length == 0 ||
                    registration.Indices.Length % 3 != 0 ||
                    contract.PrimitiveCount != checked((uint)(
                        registration.Indices.Length / 3)))
                {
                    detail =
                        "opacity-micromap-runtime-primitive-domain-mismatch";
                    return 0;
                }

                MaterialHandle materialHandle = materials[subMeshIndex];
                MaterialDefinition material =
                    _backend.GetMaterialDefinition(materialHandle);
                if (material.AlphaMode != MaterialAlphaMode.Mask ||
                    BitConverter.SingleToUInt32Bits(
                        material.BaseColorFactor.W) !=
                        contract.MaterialAlphaBits ||
                    BitConverter.SingleToUInt32Bits(material.AlphaCutoff) !=
                        contract.AlphaCutoffBits)
                {
                    lastFallbackDetail =
                        "opacity-micromap-runtime-material-state-mismatch";
                    continue;
                }

                bool vertexAlphaMatches = true;
                foreach (GPUVertexUvColorStream vertex in
                         registration.VertexUvColors)
                {
                    if (BitConverter.SingleToUInt32Bits(vertex.Color.W) !=
                        contract.UniformVertexAlphaBits)
                    {
                        vertexAlphaMatches = false;
                        break;
                    }
                }
                if (!vertexAlphaMatches)
                {
                    lastFallbackDetail =
                        "opacity-micromap-runtime-vertex-alpha-mismatch";
                    continue;
                }

                uint firstPrimitive = checked((uint)(
                    subMesh.IndexOffset / 3));
                if (!OpacityMicromapRuntimePayloadPartitioner
                        .TryCreateSubmeshPayload(
                            payload,
                            firstPrimitive,
                            contract.PrimitiveCount,
                            contract.MaterialSlot,
                            out OpacityMicromapCookedPayload? localPayload,
                            out string partitionDetail))
                {
                    if (partitionDetail ==
                        "omm-runtime-partition-submesh-has-only-special-indices")
                    {
                        lastFallbackDetail =
                            "opacity-micromap-runtime-" + partitionDetail;
                        continue;
                    }

                    detail =
                        "opacity-micromap-runtime-" + partitionDetail;
                    return 0;
                }

                pendingRegistrations.Add(
                    new OpacityMicromapRuntimeMeshRegistration(
                        meshes[subMeshIndex],
                        materialHandle,
                        _backend.GetMaterialContentRevision(materialHandle),
                        OpacityMicromapRuntimeRegistrationStore
                            .ComputeMeshGeometryKey(
                                registration.VertexPositions,
                                registration.Indices),
                        localPayload!,
                        material.DoubleSided
                            ? StaticBlasRayGeometryPolicy
                                .TwoSidedCandidateConfirmationRequired
                            : StaticBlasRayGeometryPolicy
                                .CandidateConfirmationRequired,
                        AccelerationStructureManager.StaticBlasBuildAbi));
            }

            if (pendingRegistrations.Count == 0)
            {
                detail = lastFallbackDetail;
                return 0;
            }

            var registeredMeshes = new List<MeshHandle>(
                pendingRegistrations.Count);
            foreach (OpacityMicromapRuntimeMeshRegistration registration in
                     pendingRegistrations)
            {
                if (_opacityMicromapRegistrations.TryRegisterInitialReference(
                        registration,
                        out string registrationDetail))
                {
                    registeredMeshes.Add(registration.Mesh);
                    continue;
                }

                foreach (MeshHandle registeredMesh in registeredMeshes)
                {
                    _opacityMicromapRegistrations.ReleaseMeshReference(
                        registeredMesh);
                }
                if (registrationDetail ==
                    "omm-runtime-registration-mesh-conflict")
                {
                    throw new InvalidOperationException(
                        "A mesh handle was reused while an incompatible " +
                        "opacity-micromap registration was still live.");
                }

                detail = "opacity-micromap-runtime-" + registrationDetail;
                return 0;
            }

            detail = pendingRegistrations.Count == 1
                ? "opacity-micromap-runtime-registration-ready"
                : "opacity-micromap-runtime-submesh-registrations-ready:" +
                    pendingRegistrations.Count;
            return pendingRegistrations.Count;
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposeCompleted)
                    return;
                if (_uploadInProgress)
                {
                    throw new InvalidOperationException(
                        "Model upload service disposal cannot re-enter an active upload.");
                }

                _disposeStarted = true;
                Exception? rollbackFailure =
                    TryDrainPendingRollbacksLocked();
                if (rollbackFailure != null)
                {
                    throw new AggregateException(
                        "Model upload service disposal could not complete pending upload ownership rollback.",
                        rollbackFailure);
                }

                _disposeCompleted = true;
            }

            GC.SuppressFinalize(this);
        }

        private void AttachRenderObjectResourceLifetime(
            RenderObject renderObject)
        {
            renderObject.AttachResourceLifetime(
                _retainMeshResource,
                _releaseMeshResource,
                _retainMaterialResource,
                _releaseMaterialResource,
                retainCurrentResources: false);
        }

        private void RetainMeshHandle(MeshHandle handle)
        {
            _backend.RetainMesh(handle);
            try
            {
                _opacityMicromapRegistrations.RetainMeshReference(handle);
            }
            catch (Exception retainFailure)
            {
                try
                {
                    _backend.ReleaseMesh(handle);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Mesh retention and OMM-registration rollback both failed.",
                        retainFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        private void ReleaseMeshHandle(MeshHandle handle)
        {
            // Remove the association before the backend may recycle the mesh
            // slot. If backend release is retryable, a second registration
            // release is intentionally a no-op.
            _opacityMicromapRegistrations.ReleaseMeshReference(handle);
            _backend.ReleaseMesh(handle);
        }

        private static MeshHandle RequireMeshHandle(object resource)
        {
            return resource is MeshHandle handle && handle.IsValid
                ? handle
                : throw new InvalidOperationException(
                    "Render-object mesh resource is not a valid mesh handle.");
        }

        private static MaterialHandle RequireMaterialHandle(
            object resource)
        {
            return resource is MaterialHandle handle && handle.IsValid
                ? handle
                : throw new InvalidOperationException(
                    "Render-object material resource is not a valid material handle.");
        }

        private static void AddDynamicTextureIndex(HashSet<int> indices, int textureIndex)
        {
            if (textureIndex >= BindlessIndex.FirstDynamicTextureIndex)
                indices.Add(textureIndex);
        }

        private static Vector3[] ComputeNormals(CoreVector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 p0 = ToNumericsVector(positions[i0]);
                Vector3 p1 = ToNumericsVector(positions[i1]);
                Vector3 p2 = ToNumericsVector(positions[i2]);

                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
                if (faceNormal.LengthSquared() > 0f)
                    faceNormal = Vector3.Normalize(faceNormal);

                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = normals[i].LengthSquared() > 0f
                    ? Vector3.Normalize(normals[i])
                    : Vector3.UnitZ;
            }

            return normals;
        }

        private static CoreVector3 NormalizeOrDefault(CoreVector3 value, CoreVector3 fallback)
        {
            float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
            if (lengthSquared <= float.Epsilon)
                return fallback;

            float inverseLength = 1f / MathF.Sqrt(lengthSquared);
            return new CoreVector3(value.X * inverseLength, value.Y * inverseLength, value.Z * inverseLength);
        }

        private static float CalculateTangentHandedness(CoreVector3 normal, CoreVector3 tangent, CoreVector3 bitangent)
        {
            if (bitangent.X * bitangent.X + bitangent.Y * bitangent.Y + bitangent.Z * bitangent.Z <= float.Epsilon)
                return 1f;

            CoreVector3 derivedBitangent = CoreVector3.Cross(normal, tangent);
            float sign = CoreVector3.Dot(derivedBitangent, bitangent);
            return sign < 0f ? -1f : 1f;
        }

        private static Vector3 ToNumericsVector(CoreVector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static CoreVector3 ToCoreVector(Vector3 value)
        {
            return new CoreVector3(value.X, value.Y, value.Z);
        }

        private sealed record MaterialUploadResult(
            MaterialHandle[] Materials,
            HashSet<int> DynamicTextureIndices,
            int DefaultWhiteSubstitutions,
            int DefaultNormalSubstitutions,
            int DefaultBlackSubstitutions,
            int BlendMaterialCount);
    }

    public readonly record struct MaterialTextureIndices(
        int AlbedoTextureIndex,
        int NormalTextureIndex,
        int MetallicRoughnessTextureIndex,
        int EmissiveTextureIndex,
        int OcclusionTextureIndex = BindlessIndex.DefaultWhiteTexture);

    public readonly record struct MaterialExtensionTextureIndices(
        int ClearcoatTextureIndex,
        int ClearcoatRoughnessTextureIndex,
        int ClearcoatNormalTextureIndex,
        int SheenColorTextureIndex,
        int SheenRoughnessTextureIndex,
        int AnisotropyTextureIndex,
        int TransmissionTextureIndex,
        int ThicknessTextureIndex,
        int SubsurfaceTextureIndex,
        int SpecularTextureIndex,
        int SpecularColorTextureIndex,
        int IridescenceTextureIndex,
        int IridescenceThicknessTextureIndex);

    internal sealed record MaterialTextureBindings(
        MaterialTextureIndices TextureIndices,
        MaterialExtensionTextureIndices ExtensionTextureIndices,
        IReadOnlyList<TextureHandle> TextureHandles);

    internal enum ModelUploadPublicationStage
    {
        AfterMaterialRegistration,
        AfterPrimitiveMaterialRegistration,
        AfterMeshRegistration,
        AfterRenderObjectAttachment,
        AfterBaseMaterialTransfer
    }
}
