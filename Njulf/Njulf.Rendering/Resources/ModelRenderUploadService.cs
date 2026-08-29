using System;
using System.Collections.Concurrent;
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
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Resources
{
    public sealed class ModelRenderUploadService :
        IModelRenderUploadService,
        ICooperativeModelRenderUploadService,
        ICooperativeSourceModelRenderUploadService,
        IDisposable
    {
        private readonly IModelRenderUploadBackend _backend;
        private readonly RuntimePrimitiveTransportProfileBuilder _runtimePrimitiveProfiles;
        private readonly OpacityMicromapRuntimeRegistrationStore
            _opacityMicromapRegistrations;
        private readonly MeshletStreamingResidencyCoordinator?
            _meshletResidencyCoordinator;
        private readonly SceneSubmissionSettings? _sceneSubmissionSettings;
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
        private CooperativeModelUploadWork? _cooperativeUploadOwner;
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

        public ModelRenderUploadService(
            MeshManager meshManager,
            TextureManager textureManager,
            MaterialManager materialManager,
            OpacityMicromapRuntimeRegistrationStore
                opacityMicromapRegistrations,
            MeshletStreamingResidencyCoordinator
                meshletResidencyCoordinator,
            SceneSubmissionSettings sceneSubmissionSettings)
            : this(
                new ModelRenderUploadBackend(
                    meshManager,
                    textureManager,
                    materialManager),
                opacityMicromapRegistrations,
                meshletResidencyCoordinator,
                sceneSubmissionSettings)
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
            : this(
                backend,
                opacityMicromapRegistrations,
                meshletResidencyCoordinator: null,
                sceneSubmissionSettings: null)
        {
        }

        internal ModelRenderUploadService(
            IModelRenderUploadBackend backend,
            OpacityMicromapRuntimeRegistrationStore
                opacityMicromapRegistrations,
            MeshletStreamingResidencyCoordinator?
                meshletResidencyCoordinator,
            SceneSubmissionSettings? sceneSubmissionSettings)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _opacityMicromapRegistrations =
                opacityMicromapRegistrations ??
                throw new ArgumentNullException(
                    nameof(opacityMicromapRegistrations));
            _meshletResidencyCoordinator =
                meshletResidencyCoordinator;
            _sceneSubmissionSettings = sceneSubmissionSettings;
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
                    return UploadWithTextureBatch(
                        () => UploadModelCore(modelMesh));
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
                    return UploadWithTextureBatch(
                        () => UploadCookedModelCore(cooked));
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
                var uploadProfileDiagnostics =
                    new List<string>(profileAuthenticationDiagnostics);
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
                        out int lod2Count,
                        out MeshletHierarchyNode[] hierarchyNodes,
                        out int hierarchyRootNode);
                    uint[] meshletVertices = payload.MeshletVertices.AsSpan(subMesh.MeshletVertexOffset, subMesh.MeshletVertexCount).ToArray();
                    uint[] meshletTriangles = payload.MeshletTriangles.AsSpan(subMesh.MeshletTriangleOffset, subMesh.MeshletTriangleCount).ToArray();
                    GPUVertexSkinningData[] skinning = BuildCookedSkinning(payload, subMesh);
                    uint[] coarseRayProxyIndices =
                        BuildCookedCoarseRayProxyIndices(
                            payload,
                            subMesh);
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
                        subMesh.CausticTopologyEvidence,
                        ResolveLodSimplificationError(subMesh, 1),
                        ResolveLodSimplificationError(subMesh, 2),
                        hierarchyNodes,
                        hierarchyRootNode,
                        coarseRayProxyIndices);
                }

                MeshletPhysicalResidencySession? residencySession = null;
                if (_meshletResidencyCoordinator is { } coordinator &&
                    _sceneSubmissionSettings is { } submissionSettings)
                {
                    MeshletPhysicalResidencySessionOpenResult result =
                        MeshletPhysicalResidencySession.TryOpenAsync(
                                cooked,
                                coordinator,
                                submissionSettings
                                    .GpuMeshletStreamingEnabled)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    if (result.Active && result.Session is { } session)
                    {
                        residencySession = session;
                        model.AddSharedDisposeAction(session.Dispose);
                        foreach (MeshletStreamingSubMeshActivation activation in
                                 result.ActivationPlan.SubMeshes)
                        {
                            if (!activation.Active)
                                continue;
                            registrations[activation.SubMeshIndex]
                                .EnableManagedPhysicalResidency(
                                    session.Package.GetSubMeshGpuBinding(
                                        activation.SubMeshIndex));
                        }
                        uploadProfileDiagnostics.Add(
                            $"Managed meshlet residency active for " +
                            $"{result.ActivationPlan.ActiveSubMeshCount} submeshes; " +
                            $"estimated VRAM avoided=" +
                            $"{result.ActivationPlan.EstimatedBytesAvoided} bytes.");
                    }
                    else if (!string.IsNullOrWhiteSpace(
                                 result.FallbackReason))
                    {
                        uploadProfileDiagnostics.Add(
                            "Managed meshlet residency retained full-resident storage: " +
                            result.FallbackReason);
                    }
                }

                MeshHandle[] lifetimeMeshes =
                    _backend.RegisterMeshes(registrations);
                rollback.TrackMeshes(lifetimeMeshes);
                if (residencySession is not null)
                {
                    for (int index = 0;
                         index < registrations.Length;
                         index++)
                    {
                        if (registrations[index].ManagedResidencyBinding is null)
                            continue;
                        uint vertexOffset = registrations[index]
                            .RegisteredVertexOffset ??
                            throw new InvalidOperationException(
                                "A managed mesh registration did not publish its global vertex offset.");
                        residencySession.FinalizeSubMeshVertexOffset(
                            index,
                            vertexOffset);
                    }
                }
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
                        uploadProfileDiagnostics),
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

            Dictionary<int, GiPrimitiveTransportProfile> profilesBySubMesh =
                BuildCookedProfilesBySubMesh(materialTable, subMeshes);

            var resolved = new MaterialHandle[subMeshes.Count];
            primitiveProfiles = new GiPrimitiveTransportProfile?[subMeshes.Count];
            var diagnostics = new List<string>();
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                resolved[subMeshIndex] = ResolveCookedPrimitiveMaterial(
                    subMeshIndex,
                    subMeshes[subMeshIndex],
                    baseMaterials,
                    profilesBySubMesh,
                    rollback,
                    diagnostics,
                    out primitiveProfiles[subMeshIndex]);
            }

            authenticationDiagnostics = diagnostics;
            return resolved;
        }

        private static Dictionary<int, GiPrimitiveTransportProfile>
            BuildCookedProfilesBySubMesh(
                CookedMaterialTable materialTable,
                IReadOnlyList<CookedSubMeshRecord> subMeshes)
        {
            var profilesBySubMesh =
                new Dictionary<int, GiPrimitiveTransportProfile>();
            foreach (GiPrimitiveTransportProfile profile in
                     materialTable.PrimitiveTransportProfiles)
            {
                if ((uint)profile.SubMeshIndex >= (uint)subMeshes.Count)
                {
                    throw new InvalidDataException(
                        $"Primitive transport profile references submesh {profile.SubMeshIndex}, " +
                        $"but the cooked mesh has {subMeshes.Count} submeshes.");
                }
                if (!profilesBySubMesh.TryAdd(
                        profile.SubMeshIndex,
                        profile))
                {
                    throw new InvalidDataException(
                        $"Cooked material data contains duplicate primitive transport profiles for submesh {profile.SubMeshIndex}.");
                }
            }

            return profilesBySubMesh;
        }

        private MaterialHandle ResolveCookedPrimitiveMaterial(
            int subMeshIndex,
            CookedSubMeshRecord subMesh,
            IReadOnlyList<MaterialHandle> baseMaterials,
            IReadOnlyDictionary<int, GiPrimitiveTransportProfile>
                profilesBySubMesh,
            ModelUploadRollbackLedger rollback,
            ICollection<string> diagnostics,
            out GiPrimitiveTransportProfile? primitiveProfile)
        {
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
                primitiveProfile = null;
                MaterialHandle retained = RetainPrimitiveBaseMaterial(
                    baseHandle,
                    rollback);
                UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage
                        .AfterPrimitiveMaterialRegistration);
                return retained;
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
                        "Cooked primitive profile was invalidated after " +
                        "authenticated texture upload: " +
                        authenticationFailure);
                if (diagnostics.Count < 16)
                {
                    diagnostics.Add(
                        $"Submesh {subMeshIndex} ('{subMesh.Name}'): " +
                        authenticationFailure);
                }
            }

            primitiveProfile = cookedProfile;
            MaterialHandle resolved = RegisterPrimitiveProfileMaterial(
                baseHandle,
                cookedProfile,
                rollback);
            UploadPublicationFaultInjector?.Invoke(
                ModelUploadPublicationStage
                    .AfterPrimitiveMaterialRegistration);
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

        private static uint[] BuildCookedCoarseRayProxyIndices(
            CookedMeshPayload payload,
            CookedSubMeshRecord subMesh) =>
            payload.CoarseRayProxyIndices.AsSpan(
                subMesh.CoarseRayProxyIndexOffset,
                subMesh.CoarseRayProxyIndexCount).ToArray();

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
            out int lod2Count,
            out MeshletHierarchyNode[] hierarchyNodes,
            out int hierarchyRootNode)
        {
            lod0Count = subMesh.MeshletCount;
            lod1Count = subMesh.MeshletLod1Count;
            lod2Count = subMesh.MeshletLod2Count;
            var combined = new Meshlet[checked(
                lod0Count + lod1Count + lod2Count +
                subMesh.HierarchyMeshletCount)];
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
            payload.HierarchyMeshlets.AsSpan(
                subMesh.HierarchyMeshletOffset,
                subMesh.HierarchyMeshletCount).CopyTo(
                    combined.AsSpan(checked(
                        lod0Count + lod1Count + lod2Count)));
            hierarchyNodes = payload.HierarchyNodes.AsSpan(
                subMesh.HierarchyNodeOffset,
                subMesh.HierarchyNodeCount).ToArray();
            hierarchyRootNode = subMesh.HierarchyRootNode < 0
                ? -1
                : checked(
                    subMesh.HierarchyRootNode -
                    subMesh.HierarchyNodeOffset);
            return combined;
        }

        private Model UploadWithTextureBatch(Func<Model> upload)
        {
            ArgumentNullException.ThrowIfNull(upload);
            using IModelTextureUploadBatch batch =
                _backend.BeginTextureUploadBatch();
            Model? model = null;
            try
            {
                model = upload();
                batch.Complete();
                return model;
            }
            catch (Exception uploadFailure)
            {
                if (model == null)
                    throw;

                try
                {
                    model.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "A model upload batch failed after publication and model cleanup was incomplete.",
                        uploadFailure,
                        cleanupFailure);
                }

                throw;
            }
        }

        public IContentUploadWork<Model> PrepareCookedModelUpload(
            CookedModelAsset cooked,
            Action<ModelUploadWorkProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(cooked);
            cancellationToken.ThrowIfCancellationRequested();
            ReportUploadWorkProgress(
                progress,
                ContentLoadStage.Preparing,
                0,
                Math.Max(1, cooked.BytesRead),
                "converting cooked mesh streams off the render thread");

            long preparationStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            PreparedModelCpuData prepared =
                PrepareCookedModelCpuData(
                    cooked,
                    progress,
                    cancellationToken);
            Console.WriteLine(
                $"Cooked model preparation profile: " +
                $"model='{prepared.Model.Name}', " +
                $"meshes={prepared.Meshes.Length}, " +
                $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds:F3}ms.");
            return new CooperativeModelUploadWork(
                this,
                cooked,
                prepared,
                progress,
                cancellationToken);
        }

        public IContentUploadWork<Model> PrepareModelUpload(
            ModelMesh modelMesh,
            Action<ModelUploadWorkProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(modelMesh);
            cancellationToken.ThrowIfCancellationRequested();
            ReportUploadWorkProgress(
                progress,
                ContentLoadStage.Preparing,
                0,
                1,
                "converting imported mesh streams off the render thread");

            long preparationStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            PreparedModelCpuData prepared = PrepareSourceModelCpuData(
                modelMesh,
                progress,
                cancellationToken);
            Console.WriteLine(
                $"Source model preparation profile: " +
                $"model='{prepared.Model.Name}', " +
                $"meshes={prepared.Meshes.Length}, " +
                $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds:F3}ms.");
            return new CooperativeModelUploadWork(
                this,
                cooked: null,
                prepared,
                progress,
                cancellationToken);
        }

        private static PreparedModelCpuData PrepareCookedModelCpuData(
            CookedModelAsset cooked,
            Action<ModelUploadWorkProgress>? progress,
            CancellationToken cancellationToken)
        {
            CookedMeshPayload payload = cooked.Mesh;
            if (payload.SubMeshes.Count == 0)
            {
                throw new CookedAssetFormatException(
                    cooked.PackagePath,
                    "mesh payload contains no submeshes");
            }

            _ = BuildCookedProfilesBySubMesh(
                cooked.Materials,
                payload.SubMeshes);
            var model = new Model
            {
                Name = string.IsNullOrWhiteSpace(cooked.Manifest.Name)
                    ? "Model"
                    : cooked.Manifest.Name,
                BoundingBox = cooked.Manifest.BoundingBox,
                BoundingSphere = cooked.Manifest.BoundingSphere
            };
            model.AddSkeletons(cooked.Animation.Skeletons);
            model.AddSkins(cooked.Animation.Skins);
            model.AddAnimationClips(cooked.Animation.AnimationClips);
            model.AddLights(cooked.Manifest.Lights);

            var meshes = new PreparedModelMeshData[
                payload.SubMeshes.Count];
            var subMeshes = new PreparedModelSubMeshData[
                payload.SubMeshes.Count];
            long totalBytes = Math.Max(1, cooked.BytesRead);
            for (int i = 0; i < payload.SubMeshes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CookedSubMeshRecord subMesh = payload.SubMeshes[i];
                GPUVertexPositionStream[] vertexPositions =
                    BuildCookedPositionStream(payload, subMesh);
                GPUVertexNormalTangentStream[] vertexNormalTangents =
                    BuildCookedNormalTangentStream(payload, subMesh);
                GPUVertexUvColorStream[] vertexUvColors =
                    BuildCookedUvColorStream(payload, subMesh);
                uint[] indices = payload.Indices.AsSpan(
                    subMesh.IndexOffset,
                    subMesh.IndexCount).ToArray();
                Meshlet[] meshlets = BuildCookedMeshletRanges(
                    payload,
                    subMesh,
                    out int lod0Count,
                    out int lod1Count,
                    out int lod2Count,
                    out MeshletHierarchyNode[] hierarchyNodes,
                    out int hierarchyRootNode);
                uint[] meshletVertices = payload.MeshletVertices.AsSpan(
                    subMesh.MeshletVertexOffset,
                    subMesh.MeshletVertexCount).ToArray();
                uint[] meshletTriangles = payload.MeshletTriangles.AsSpan(
                    subMesh.MeshletTriangleOffset,
                    subMesh.MeshletTriangleCount).ToArray();
                GPUVertexSkinningData[] skinning =
                    BuildCookedSkinning(payload, subMesh);
                uint[] coarseRayProxyIndices =
                    BuildCookedCoarseRayProxyIndices(
                        payload,
                        subMesh);
                meshes[i] = new PreparedModelMeshData(
                    SourceVertices: null,
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
                    skinning,
                    subMesh.CausticTopologyEvidence,
                    ResolveLodSimplificationError(subMesh, 1),
                    ResolveLodSimplificationError(subMesh, 2),
                    hierarchyNodes,
                    hierarchyRootNode,
                    coarseRayProxyIndices);
                subMeshes[i] = new PreparedModelSubMeshData(
                    subMesh.Name,
                    subMesh.MaterialSlot,
                    subMesh.SkinIndex,
                    subMesh.SkinningBindTransform,
                    subMesh.BoundingBox);

                if ((i & 63) == 63 ||
                    i + 1 == payload.SubMeshes.Count)
                {
                    long completed = checked((long)Math.Round(
                        totalBytes * 0.30 *
                        (i + 1) /
                        payload.SubMeshes.Count));
                    ReportUploadWorkProgress(
                        progress,
                        ContentLoadStage.Preparing,
                        completed,
                        totalBytes,
                        $"converted {i + 1}/{payload.SubMeshes.Count} cooked meshes");
                }
            }

            IReadOnlyList<ModelMaterial> importedMaterials =
                cooked.Materials.Materials.Count > 0
                    ? cooked.Materials.Materials
                    : new[] { ModelMaterial.Default };
            return new PreparedModelCpuData(
                model,
                meshes,
                subMeshes,
                importedMaterials,
                cooked.Materials.Pipelines,
                RuntimeProfiles: null,
                RuntimeProfileDiagnostics: null,
                totalBytes,
                SourceKind: "cooked");
        }

        private PreparedModelCpuData PrepareSourceModelCpuData(
            ModelMesh modelMesh,
            Action<ModelUploadWorkProgress>? progress,
            CancellationToken cancellationToken)
        {
            ValidateModelMesh(modelMesh);
            var model = new Model
            {
                Name = string.IsNullOrWhiteSpace(modelMesh.Name)
                    ? "Model"
                    : modelMesh.Name,
                BoundingBox = modelMesh.BoundingBox,
                BoundingSphere = modelMesh.BoundingSphere
            };
            model.AddSkeletons(modelMesh.Skeletons);
            model.AddSkins(modelMesh.Skins);
            model.AddAnimationClips(modelMesh.AnimationClips);
            model.AddLights(modelMesh.Lights);

            IReadOnlyList<ModelMaterial> importedMaterials =
                modelMesh.Materials.Count > 0
                    ? modelMesh.Materials
                    : new[] { ModelMaterial.Default };
            IReadOnlyList<ModelSubMesh> sourceSubMeshes =
                modelMesh.SubMeshes.Count > 0
                    ? modelMesh.SubMeshes
                    : new[]
                    {
                        new ModelSubMesh
                        {
                            Name = string.IsNullOrWhiteSpace(modelMesh.Name)
                                ? "Mesh"
                                : modelMesh.Name,
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

            foreach (ModelSubMesh subMesh in sourceSubMeshes)
                ValidateSubMesh(subMesh, nameof(modelMesh));
            RuntimePrimitiveTransportProfileBuildResult profileBuild =
                _runtimePrimitiveProfiles.Build(
                    sourceSubMeshes,
                    importedMaterials);
            var meshes = new PreparedModelMeshData[sourceSubMeshes.Count];
            var subMeshes = new PreparedModelSubMeshData[
                sourceSubMeshes.Count];
            var meshletLodBuilder = new RendererMeshletLodBuilder();
            long totalBytes = 0;
            foreach (ModelSubMesh subMesh in sourceSubMeshes)
            {
                totalBytes = checked(
                    totalBytes +
                    (long)subMesh.Vertices.Length * 64L +
                    (long)subMesh.Indices.Length * 28L +
                    (long)subMesh.JointIndices0.Length * 32L);
            }
            totalBytes = Math.Max(1, totalBytes);
            for (int i = 0; i < sourceSubMeshes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelSubMesh subMesh = sourceSubMeshes[i];
                GPUVertex[] vertices = BuildGpuVertices(subMesh);
                GPUVertexSkinningData[] skinning =
                    BuildGpuSkinningData(subMesh, model);
                RendererMeshletLodBuild meshletLods =
                    meshletLodBuilder.Build(subMesh);
                int materialIndex = ResolveSubMeshMaterialIndex(
                    subMesh,
                    importedMaterials.Count);
                ModelGiCausticHeroTopologyEvidence causticEvidence =
                    default;
                ModelMaterial sourceMaterial = importedMaterials[
                    materialIndex];
                if (sourceMaterial.GiCausticParticipation !=
                    ModelGiCausticParticipationMode.None)
                {
                    _ = ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
                        subMesh.Vertices,
                        subMesh.Indices,
                        isSkinned: subMesh.SkinIndex >= 0,
                        out causticEvidence,
                        out _);
                }

                meshes[i] = new PreparedModelMeshData(
                    vertices,
                    Array.Empty<GPUVertexPositionStream>(),
                    Array.Empty<GPUVertexNormalTangentStream>(),
                    Array.Empty<GPUVertexUvColorStream>(),
                    subMesh.Indices,
                    meshletLods.Meshlets,
                    meshletLods.MeshletVertices,
                    meshletLods.MeshletTriangles,
                    Lod0Count: meshletLods.Ranges[0].MeshletCount,
                    Lod1Count: meshletLods.Ranges[1].MeshletCount,
                    Lod2Count: meshletLods.Ranges[2].MeshletCount,
                    skinning,
                    causticEvidence,
                    Lod1SimplificationError:
                        meshletLods.SimplificationErrors[1],
                    Lod2SimplificationError:
                        meshletLods.SimplificationErrors[2],
                    HierarchyNodes: meshletLods.HierarchyNodes,
                    HierarchyRootNode:
                        meshletLods.HierarchyRootNode,
                    CoarseRayProxyIndices: Array.Empty<uint>());
                subMeshes[i] = new PreparedModelSubMeshData(
                    subMesh.Name,
                    materialIndex,
                    subMesh.SkinIndex,
                    subMesh.SkinningBindTransform,
                    subMesh.BoundingBox);
                if ((i & 63) == 63 || i + 1 == sourceSubMeshes.Count)
                {
                    long completed = checked((long)Math.Round(
                        totalBytes * 0.30 *
                        (i + 1) /
                        sourceSubMeshes.Count));
                    ReportUploadWorkProgress(
                        progress,
                        ContentLoadStage.Preparing,
                        completed,
                        totalBytes,
                        $"converted {i + 1}/{sourceSubMeshes.Count} imported meshes");
                }
            }

            return new PreparedModelCpuData(
                model,
                meshes,
                subMeshes,
                importedMaterials,
                Array.Empty<CookedMaterialPipeline>(),
                profileBuild.Profiles,
                profileBuild.Diagnostics,
                totalBytes,
                SourceKind: "source");
        }

        private static void ReportUploadWorkProgress(
            Action<ModelUploadWorkProgress>? progress,
            ContentLoadStage stage,
            long completedBytes,
            long totalBytes,
            string detail)
        {
            if (progress == null)
                return;
            try
            {
                progress(new ModelUploadWorkProgress(
                    stage,
                    Math.Clamp(completedBytes, 0, totalBytes),
                    Math.Max(1, totalBytes),
                    detail));
            }
            catch
            {
                // Progress is diagnostic and cannot change resource ownership.
            }
        }

        private static float ResolveLodSimplificationError(
            CookedSubMeshRecord subMesh,
            int lodLevel)
        {
            for (int i = 0; i < subMesh.LodRanges.Count; i++)
            {
                ProcessedMeshLodRange range = subMesh.LodRanges[i];
                if (range.Level == lodLevel)
                {
                    return float.IsFinite(range.SimplificationError) &&
                           range.SimplificationError >= 0f
                        ? range.SimplificationError
                        : -1f;
                }
            }

            return -1f;
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
                    CookedMaterialPipeline? cookedPipeline =
                        cookedPipelines is { Count: > 0 }
                            ? cookedPipelines[i]
                            : null;
                    materials[i] = RegisterImportedMaterial(
                        importedMaterials[i],
                        cookedPipeline,
                        ownership,
                        dynamicTextureIndices,
                        ref defaultWhiteSubstitutions,
                        ref defaultNormalSubstitutions,
                        ref defaultBlackSubstitutions,
                        ref blendMaterialCount,
                        preparedSources: null);
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

        private MaterialHandle RegisterImportedMaterial(
            ModelMaterial material,
            CookedMaterialPipeline? cookedPipeline,
            ModelUploadOwnershipLedger ownership,
            HashSet<int> dynamicTextureIndices,
            ref int defaultWhiteSubstitutions,
            ref int defaultNormalSubstitutions,
            ref int defaultBlackSubstitutions,
            ref int blendMaterialCount,
            IReadOnlyList<ModelTextureSource?>? preparedSources,
            TextureHandle[]? preloadedTextureReferences = null)
        {
            if (material.AlphaMode == ModelAlphaMode.Blend)
                blendMaterialCount++;

            MaterialTextureBindings textureBindings =
                ResolveMaterialTextureBindings(
                    material,
                    ref defaultWhiteSubstitutions,
                    ref defaultNormalSubstitutions,
                    ref defaultBlackSubstitutions,
                    ownership.PendingTextures,
                    preparedSources);

            ReleasePreloadedTextureReferences(
                ownership,
                preloadedTextureReferences);

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

            MaterialDefinition definition = BuildMaterialDefinition(
                material,
                textureBindings,
                cookedPipeline);
            MaterialHandle handle =
                _backend.RegisterMaterialDefinition(definition);
            // RegisterMaterialDefinition transfers every pending texture
            // occurrence to this logical material reference on success.
            ownership.CommitPendingTexturesTo(handle);
            return handle;
        }

        private void ReleasePreloadedTextureReferences(
            ModelUploadOwnershipLedger ownership,
            TextureHandle[]? preloadedTextureReferences)
        {
            if (preloadedTextureReferences == null)
                return;

            for (int i = 0; i < preloadedTextureReferences.Length; i++)
            {
                TextureHandle handle = preloadedTextureReferences[i];
                if (!handle.IsValid)
                    continue;

                // The final binding resolution retained the same cached
                // texture occurrence. Remove and release the temporary
                // preload occurrence before the material takes ownership of
                // the final occurrences. Failed releases are put back into
                // the durable rollback ledger.
                if (!ownership.PendingTextures.Remove(handle))
                {
                    throw new InvalidOperationException(
                        "A preloaded material texture was not owned by the " +
                        "upload rollback ledger.");
                }

                try
                {
                    _backend.ReleaseTexture(handle);
                    preloadedTextureReferences[i] = TextureHandle.Invalid;
                }
                catch
                {
                    ownership.PendingTextures.Add(handle);
                    throw;
                }
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

        private PreparedMaterialTextureSources PrepareMaterialTextureSources(
            ModelMaterial material,
            ConcurrentDictionary<
                PreparedTextureSourceCacheKey,
                Lazy<ModelTextureSource>> preparedSourceCache,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(material);
            ArgumentNullException.ThrowIfNull(preparedSourceCache);
            var prepared = new List<PreparedMaterialTextureSlot>(18);
            long encodedBytes = 0;

            void Add(
                ModelTextureSlot? slot,
                string? legacyPath,
                PreparedTextureFallback fallback,
                bool generateMipmaps,
                bool srgb,
                TextureSemantic semantic,
                RuntimeTextureMipPolicy mipPolicy = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelTextureSource? source = slot?.Source;
                if (source == null &&
                    !string.IsNullOrWhiteSpace(legacyPath))
                {
                    string fullPath = Path.GetFullPath(legacyPath);
                    source = new ModelTextureSource
                    {
                        DebugName = Path.GetFileName(fullPath),
                        FilePath = fullPath,
                        CacheIdentity = fullPath
                    };
                }

                if (source == null)
                {
                    prepared.Add(new PreparedMaterialTextureSlot(
                        Source: null,
                        slot?.Sampler ?? TextureSamplerDescription.Default,
                        generateMipmaps,
                        slot?.ColorSpace == TextureColorSpace.Srgb ||
                            slot == null && srgb,
                        semantic,
                        mipPolicy,
                        fallback));
                    return;
                }

                bool effectiveSrgb =
                    slot?.ColorSpace == TextureColorSpace.Srgb ||
                    slot == null && srgb;
                ModelTextureSource snapshot = PrepareTextureSourceCached(
                    source,
                    slot?.Sampler ?? TextureSamplerDescription.Default,
                    effectiveSrgb,
                    semantic,
                    mipPolicy,
                    preparedSourceCache,
                    cancellationToken);
                prepared.Add(new PreparedMaterialTextureSlot(
                    snapshot,
                    slot?.Sampler ?? TextureSamplerDescription.Default,
                    generateMipmaps,
                    effectiveSrgb,
                    semantic,
                    mipPolicy,
                    fallback));
                encodedBytes = checked(
                    encodedBytes +
                    (snapshot.PreparedSnapshot?.EncodedBytes.LongLength ??
                     snapshot.EncodedByteLength));
            }

            Add(
                material.BaseColorTexture,
                material.AlbedoTexturePath,
                PreparedTextureFallback.White,
                ShouldGenerateAlbedoMipmaps(material),
                srgb: true,
                TextureSemantic.Color,
                ResolveAlbedoRuntimeMipPolicy(material));
            Add(
                material.NormalTexture,
                material.NormalTexturePath,
                PreparedTextureFallback.Normal,
                generateMipmaps: true,
                srgb: false,
                TextureSemantic.Normal);
            Add(
                material.MetallicRoughnessTexture,
                material.MetallicRoughnessTexturePath,
                PreparedTextureFallback.Black,
                generateMipmaps: true,
                srgb: false,
                TextureSemantic.Data);
            Add(
                material.OcclusionTexture,
                material.OcclusionTexturePath,
                PreparedTextureFallback.White,
                generateMipmaps: true,
                srgb: false,
                TextureSemantic.Scalar);
            Add(
                material.EmissiveTexture,
                material.EmissiveTexturePath,
                PreparedTextureFallback.White,
                generateMipmaps: true,
                srgb: true,
                TextureSemantic.Color);

            if (((MaterialFeatureFlags)material.FeatureFlags)
                .RequiresExtensionData())
            {
                Add(material.ClearcoatTexture, material.ClearcoatTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.ClearcoatRoughnessTexture, material.ClearcoatRoughnessTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.ClearcoatNormalTexture, material.ClearcoatNormalTexturePath, PreparedTextureFallback.Normal, true, false, TextureSemantic.Normal);
                Add(material.SheenColorTexture, material.SheenColorTexturePath, PreparedTextureFallback.White, true, true, TextureSemantic.Color);
                Add(material.SheenRoughnessTexture, material.SheenRoughnessTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.AnisotropyTexture, material.AnisotropyTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Data);
                Add(material.TransmissionTexture, material.TransmissionTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.ThicknessTexture, material.ThicknessTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.SubsurfaceTexture, material.SubsurfaceTexturePath, PreparedTextureFallback.White, true, true, TextureSemantic.Color);
                Add(material.SpecularTexture, material.SpecularTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.SpecularColorTexture, material.SpecularColorTexturePath, PreparedTextureFallback.White, true, true, TextureSemantic.Color);
                Add(material.IridescenceTexture, material.IridescenceTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
                Add(material.IridescenceThicknessTexture, material.IridescenceThicknessTexturePath, PreparedTextureFallback.White, true, false, TextureSemantic.Scalar);
            }

            return new PreparedMaterialTextureSources(
                prepared,
                encodedBytes);
        }

        private ModelTextureSource PrepareTextureSourceCached(
            ModelTextureSource source,
            TextureSamplerDescription sampler,
            bool srgb,
            TextureSemantic semantic,
            RuntimeTextureMipPolicy mipPolicy,
            ConcurrentDictionary<
                PreparedTextureSourceCacheKey,
                Lazy<ModelTextureSource>> cache,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? identity = !string.IsNullOrWhiteSpace(
                    source.CacheIdentity)
                ? source.CacheIdentity
                : !string.IsNullOrWhiteSpace(source.FilePath)
                    ? Path.GetFullPath(source.FilePath)
                    : null;
            if (identity == null)
            {
                return _backend.PrepareTextureSource(
                    source,
                    sampler,
                    srgb,
                    semantic,
                    mipPolicy);
            }

            var key = new PreparedTextureSourceCacheKey(
                identity,
                source.SourceKind,
                source.ContainerKind,
                sampler,
                srgb,
                semantic,
                mipPolicy);
            var candidate = new Lazy<ModelTextureSource>(
                () => _backend.PrepareTextureSource(
                    source,
                    sampler,
                    srgb,
                    semantic,
                    mipPolicy),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<ModelTextureSource> preparation =
                cache.GetOrAdd(key, candidate);
            try
            {
                return preparation.Value;
            }
            catch
            {
                if (cache.TryGetValue(key, out Lazy<ModelTextureSource>? current) &&
                    ReferenceEquals(current, preparation))
                {
                    cache.TryRemove(key, out _);
                }
                throw;
            }
        }

        private TextureHandle PreloadPreparedMaterialTexture(
            PreparedMaterialTextureSlot slot,
            out bool ownsReference)
        {
            ArgumentNullException.ThrowIfNull(slot);
            if (slot.Source == null)
            {
                ownsReference = false;
                return TextureHandle.Invalid;
            }

            TextureHandle loaded = _backend.LoadTexture(
                slot.Source,
                slot.Sampler,
                slot.GenerateMipmaps,
                slot.Srgb,
                requireWithinMemoryBudget: false,
                slot.Semantic,
                slot.MipPolicy);
            if (loaded.IsValid)
            {
                ownsReference = true;
                return loaded;
            }

            ownsReference = false;
            return slot.Fallback switch
            {
                PreparedTextureFallback.White =>
                    _backend.DefaultWhiteTexture,
                PreparedTextureFallback.Normal =>
                    _backend.DefaultNormalTexture,
                PreparedTextureFallback.Black =>
                    _backend.DefaultBlackTexture,
                _ => throw new InvalidOperationException(
                    $"Unsupported prepared texture fallback " +
                    $"'{slot.Fallback}'.")
            };
        }

        private MaterialTextureBindings ResolveMaterialTextureBindings(
            ModelMaterial material,
            ref int defaultWhiteSubstitutions,
            ref int defaultNormalSubstitutions,
            ref int defaultBlackSubstitutions,
            ICollection<TextureHandle> pendingTextureOwnership,
            IReadOnlyList<ModelTextureSource?>? preparedSources = null)
        {
            int preparedSourceIndex = 0;
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
                ModelTextureSource? preparedSource =
                    preparedSources == null
                        ? null
                        : preparedSources[preparedSourceIndex++];
                TextureHandle handle = this.ResolveTextureHandle(
                    textureSlot,
                    texturePath,
                    fallback,
                    ref defaultSubstitutions,
                    generateMipmaps,
                    srgb,
                    semantic,
                    mipPolicy,
                    preparedSource);
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

            MaterialTextureBindings result = new(
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
            if (preparedSources != null &&
                preparedSourceIndex != preparedSources.Count)
            {
                throw new InvalidOperationException(
                    $"Prepared material texture count {preparedSources.Count} " +
                    $"does not match the {preparedSourceIndex} resolved slots.");
            }

            return result;
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
                _ when material.IsThinGlass => MaterialShadingModel.ThinGlass,
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
            RuntimeTextureMipPolicy mipPolicy = default,
            ModelTextureSource? preparedSource = null)
        {
            if (!fallback.IsValid)
                throw new InvalidOperationException("Default textures must be initialized before material upload.");

            if (preparedSource != null)
            {
                bool preparedSrgb = textureSlot?.ColorSpace ==
                    TextureColorSpace.Srgb ||
                    textureSlot == null && srgb;
                TextureHandle preparedTexture = _backend.LoadTexture(
                    preparedSource,
                    textureSlot?.Sampler ??
                        TextureSamplerDescription.Default,
                    generateMipmaps,
                    preparedSrgb,
                    requireWithinMemoryBudget: false,
                    semantic,
                    mipPolicy);
                return preparedTexture.IsValid
                    ? preparedTexture
                    : fallback;
            }

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
                        _backend.GetOpacityMicromapMaterialRevision(
                            materialHandle),
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

        private sealed class CooperativeModelUploadWork :
            IContentUploadWork<Model>
        {
            private const int PrimitiveMaterialBatchSize = 64;
            private const int RenderObjectBatchSize = 128;
            private const int TextureSlotBatchSize = 18;
            private const int MaximumPendingTextureUploadBatches = 16;
            private const int MaterialPreparationLookahead = 16;
            private const long FirstMeshSubmissionBytes =
                16L * 1024L * 1024L;

            private readonly ModelRenderUploadService _owner;
            private readonly CookedModelAsset? _cooked;
            private readonly PreparedModelCpuData _cpu;
            private readonly Action<ModelUploadWorkProgress>? _progress;
            private readonly CancellationToken _externalCancellation;
            private readonly CancellationTokenSource _preparationCancellation;
            private readonly IReadOnlyList<ModelMaterial> _importedMaterials;
            private readonly IReadOnlyList<CookedMaterialPipeline> _pipelines;
            private readonly Dictionary<int, GiPrimitiveTransportProfile>
                _profilesBySubMesh;
            private readonly MaterialHandle[] _materials;
            private readonly MaterialHandle[] _subMeshMaterials;
            private readonly GiPrimitiveTransportProfile?[]
                _primitiveProfiles;
            private readonly List<string> _profileDiagnostics = [];
            private readonly HashSet<int> _dynamicTextureIndices = [];

            private CookedModelUploadPhase _phase =
                CookedModelUploadPhase.WaitingForOwnership;
            private ModelUploadOwnershipLedger? _materialOwnership;
            private ModelUploadRollbackLedger? _rollback;
            private readonly Task<PreparedMaterialTextureSources>?[]
                _materialPreparations;
            private readonly ConcurrentDictionary<
                PreparedTextureSourceCacheKey,
                Lazy<ModelTextureSource>> _preparedTextureSourceCache = [];
            private PreparedMaterialTextureSources?
                _preparedMaterialTextures;
            private TextureHandle[]? _materialPreloadReferences;
            private readonly Queue<IModelTextureUploadBatch>
                _pendingTextureUploadBatches = [];
            private IModelMeshUpload? _pendingMeshUpload;
            private Task<MeshManager.MeshRegistrationData[]>?
                _registrationPreparation;
            private Task<MeshletPhysicalResidencySessionOpenResult>?
                _residencyPreparation;
            private MeshletPhysicalResidencySession? _residencySession;
            private MeshManager.MeshRegistrationData[]? _registrations;
            private MeshHandle[]? _meshes;
            private Model? _result;
            private int _materialIndex;
            private int _materialTextureIndex;
            private int _primitiveIndex;
            private int _meshRegistrationIndex;
            private int _pendingMeshFirst;
            private int _pendingMeshCount;
            private ulong _pendingMeshStagedBytes;
            private int _renderObjectIndex;
            private int _defaultWhiteSubstitutions;
            private int _defaultNormalSubstitutions;
            private int _defaultBlackSubstitutions;
            private int _blendMaterialCount;
            private int _opacityMicromapRuntimeRegistrationCount;
            private string _opacityMicromapRuntimeDetail;
            private bool _baseMaterialsTransferredToRollback;
            private bool _materialRegistrationsComplete;
            private bool _ownsUploadSlot;
            private bool _cancellationRequested;
            private readonly long _uploadStartedTimestamp =
                System.Diagnostics.Stopwatch.GetTimestamp();
            private long _phaseStartedTimestamp;

            public CooperativeModelUploadWork(
                ModelRenderUploadService owner,
                CookedModelAsset? cooked,
                PreparedModelCpuData cpu,
                Action<ModelUploadWorkProgress>? progress,
                CancellationToken externalCancellation)
            {
                _owner = owner;
                _cooked = cooked;
                _cpu = cpu;
                _progress = progress;
                _externalCancellation = externalCancellation;
                _preparationCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        externalCancellation);
                _importedMaterials = cpu.Materials;
                _pipelines = cpu.Pipelines;
                if (_pipelines.Count > 0 &&
                    _pipelines.Count != _importedMaterials.Count)
                {
                    throw new InvalidDataException(
                        $"Cooked material pipeline count {_pipelines.Count} " +
                        $"does not match material count {_importedMaterials.Count}.");
                }

                _profilesBySubMesh = cooked is null
                    ? new Dictionary<int, GiPrimitiveTransportProfile>()
                    : BuildCookedProfilesBySubMesh(
                        cooked.Materials,
                        cooked.Mesh.SubMeshes);
                _materials = new MaterialHandle[_importedMaterials.Count];
                _materialPreparations =
                    new Task<PreparedMaterialTextureSources>?[
                        _importedMaterials.Count];
                _subMeshMaterials = new MaterialHandle[
                    cpu.SubMeshes.Length];
                _primitiveProfiles = new GiPrimitiveTransportProfile?[
                    cpu.SubMeshes.Length];
                _opacityMicromapRuntimeDetail =
                    cooked?.OpacityMicromapLoadStatus.Detail ??
                    "opacity-micromap-source-model";
                if (cpu.RuntimeProfileDiagnostics is { } diagnostics)
                {
                    _profileDiagnostics.AddRange(
                        diagnostics.Messages.Take(16));
                }
                if (cooked is not null &&
                    owner._meshletResidencyCoordinator is { } coordinator &&
                    owner._sceneSubmissionSettings is { } submissionSettings)
                {
                    _residencyPreparation =
                        MeshletPhysicalResidencySession.TryOpenAsync(
                                cooked,
                                coordinator,
                                submissionSettings
                                    .GpuMeshletStreamingEnabled,
                                _preparationCancellation.Token)
                            .AsTask();
                }
                _phaseStartedTimestamp = _uploadStartedTimestamp;
            }

            public ContentUploadStepResult ExecuteStep(
                in ContentUploadSliceBudget budget)
            {
                if (_phase == CookedModelUploadPhase.Completed)
                {
                    return ContentUploadStepResult.Complete(
                        _cpu.TotalBytes,
                        _cpu.TotalBytes,
                        "model upload already completed");
                }
                if (_phase == CookedModelUploadPhase.Cancelled)
                {
                    return ContentUploadStepResult.Cancelled(
                        detail: "model upload cancelled");
                }

                if (_externalCancellation.IsCancellationRequested)
                    RequestCancellation();

                lock (_owner._lifecycleLock)
                {
                    if (_cancellationRequested)
                        return CancelLocked();

                    if (!_ownsUploadSlot && !TryAcquireUploadSlotLocked())
                    {
                        return Yield(
                            ContentLoadStage.WaitingForUpload,
                            0.30,
                            "waiting for the active model upload transaction");
                    }

                    try
                    {
                        return _phase switch
                        {
                            CookedModelUploadPhase.Materials =>
                                ExecuteMaterialStepLocked(budget),
                            CookedModelUploadPhase.PrimitiveMaterials =>
                                ExecutePrimitiveMaterialStepLocked(budget),
                            CookedModelUploadPhase.PreparingRegistrations =>
                                ExecuteRegistrationPreparationStepLocked(),
                            CookedModelUploadPhase.RegisteringMeshes =>
                                ExecuteMeshRegistrationStepLocked(budget),
                            CookedModelUploadPhase.AwaitingResidencyBootstrap =>
                                ExecuteResidencyBootstrapStepLocked(),
                            CookedModelUploadPhase.AttachingRenderObjects =>
                                ExecuteRenderObjectStepLocked(budget),
                            CookedModelUploadPhase.Finalizing =>
                                FinalizeLocked(),
                            _ => throw new InvalidOperationException(
                                $"Unsupported cooked upload phase '{_phase}'.")
                        };
                    }
                    catch (Exception uploadFailure)
                    {
                        throw RollbackFailureLocked(uploadFailure);
                    }
                }
            }

            public Model GetResult()
            {
                if (_phase != CookedModelUploadPhase.Completed ||
                    _result == null)
                {
                    throw new InvalidOperationException(
                        "The cooperative model upload has not completed.");
                }

                return _result;
            }

            public void RequestCancellation()
            {
                _cancellationRequested = true;
                try
                {
                    _preparationCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A terminal work item has already released preparation.
                }
            }

            private bool TryAcquireUploadSlotLocked()
            {
                _owner.EnsureLifecycleLockHeld();
                if (_owner._cooperativeUploadOwner != null &&
                    !ReferenceEquals(
                        _owner._cooperativeUploadOwner,
                        this))
                {
                    return false;
                }
                if (_owner._uploadInProgress &&
                    !ReferenceEquals(
                        _owner._cooperativeUploadOwner,
                        this))
                {
                    return false;
                }

                _owner.BeginUploadLocked();
                try
                {
                    _owner.PrepareForUploadLocked();
                    _owner._cooperativeUploadOwner = this;
                    _ownsUploadSlot = true;
                    _materialOwnership = new ModelUploadOwnershipLedger(
                        _importedMaterials.Count,
                        // One retained occurrence per preloaded slot plus one
                        // final occurrence while the material is assembled.
                        pendingTextureCapacity: 36,
                        _owner._backend.ReleaseMaterial,
                        _owner._backend.ReleaseTexture);
                    _rollback = new ModelUploadRollbackLedger(
                        _cpu.Model,
                        checked(
                            _importedMaterials.Count +
                            _cpu.SubMeshes.Length),
                        _cpu.SubMeshes.Length,
                        _owner._releaseMeshHandle,
                        _owner._backend.ReleaseMaterial,
                        _owner._backend.ReleaseTexture);
                    _owner._backend.InitializeDefaultTextures();
                    _phase = CookedModelUploadPhase.Materials;
                    BeginProfiledPhase(CookedModelUploadPhase.Materials);
                    FillMaterialPreparationWindow();
                    return true;
                }
                catch
                {
                    _owner._cooperativeUploadOwner = null;
                    _ownsUploadSlot = false;
                    _owner.EndUploadLocked();
                    throw;
                }
            }

            private ContentUploadStepResult ExecuteMaterialStepLocked(
                in ContentUploadSliceBudget budget)
            {
                bool textureGpuWorkComplete =
                    TryDrainCompletedTextureUploadsLocked();
                if (_materialRegistrationsComplete)
                {
                    if (!textureGpuWorkComplete)
                    {
                        return Yield(
                            ContentLoadStage.AwaitingGpu,
                            0.65,
                            $"all materials registered; waiting for " +
                            $"{_pendingTextureUploadBatches.Count} queued " +
                            "texture upload batches");
                    }

                    return CompleteMaterialRegistrationPhaseLocked();
                }
                if (!textureGpuWorkComplete &&
                    _pendingTextureUploadBatches.Count >=
                    MaximumPendingTextureUploadBatches)
                {
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        MaterialProgress(0.0),
                        $"texture upload window full " +
                        $"({_pendingTextureUploadBatches.Count}/" +
                        $"{MaximumPendingTextureUploadBatches}); " +
                        "waiting for the oldest GPU batch");
                }

                if (_preparedMaterialTextures == null &&
                    _materialPreparations[_materialIndex] == null)
                {
                    FillMaterialPreparationWindow();
                }
                if (_preparedMaterialTextures == null)
                {
                    Task<PreparedMaterialTextureSources> preparation =
                        _materialPreparations[_materialIndex] ??
                        throw new InvalidOperationException(
                            "Material preparation was not scheduled.");
                    if (!preparation.IsCompleted)
                    {
                        return Yield(
                            ContentLoadStage.Preparing,
                            MaterialProgress(0.0),
                            $"reading and authenticating material {_materialIndex + 1}/" +
                            _importedMaterials.Count);
                    }

                    _preparedMaterialTextures =
                        preparation.GetAwaiter().GetResult();
                    _materialPreparations[_materialIndex] = null;
                    _materialTextureIndex = 0;
                    _materialPreloadReferences = new TextureHandle[
                        _preparedMaterialTextures.Slots.Count];
                }

                PreparedMaterialTextureSources prepared =
                    _preparedMaterialTextures;

                ulong maximumSubmissionBytes = checked((ulong)Math.Max(
                    1,
                    budget.MaximumSubmissionBytes));
                if (_materialTextureIndex < prepared.Slots.Count)
                {
                    long started =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                    int firstTexture = _materialTextureIndex;
                    IModelTextureUploadBatch? uploadBatch =
                        _owner._backend.BeginTextureUploadBatch(
                            maximumSubmissionBytes);
                    try
                    {
                        while (_materialTextureIndex < prepared.Slots.Count &&
                               _materialTextureIndex - firstTexture <
                               TextureSlotBatchSize)
                        {
                            PreparedMaterialTextureSlot slot =
                                prepared.Slots[_materialTextureIndex];

                            TextureHandle handle =
                                _owner.PreloadPreparedMaterialTexture(
                                    slot,
                                    out bool ownsReference);
                            if (ownsReference)
                            {
                                (_materialOwnership ??
                                 throw new InvalidOperationException(
                                     "Material upload ownership is unavailable."))
                                    .PendingTextures.Add(handle);
                                _materialPreloadReferences![
                                    _materialTextureIndex] = handle;
                            }

                            _materialTextureIndex++;
                            if (budget.RemainingCpuTime > TimeSpan.Zero &&
                                System.Diagnostics.Stopwatch.GetElapsedTime(
                                    started) >= TimeSpan.FromTicks(Math.Max(
                                    1,
                                    budget.RemainingCpuTime.Ticks / 2)))
                            {
                                break;
                            }
                        }

                        uploadBatch.Complete();
                        if (uploadBatch.TryCompleteGpuWork())
                        {
                            uploadBatch.Dispose();
                        }
                        else
                        {
                            _pendingTextureUploadBatches.Enqueue(
                                uploadBatch);
                        }
                        uploadBatch = null;
                    }
                    finally
                    {
                        uploadBatch?.Dispose();
                    }

                    if (_materialTextureIndex < prepared.Slots.Count)
                    {
                        double textureFraction = prepared.Slots.Count == 0
                            ? 1.0
                            : _materialTextureIndex /
                              (double)prepared.Slots.Count;
                        return Yield(
                            ContentLoadStage.Uploading,
                            MaterialProgress(textureFraction),
                            $"prepared {_materialTextureIndex}/" +
                            $"{prepared.Slots.Count} texture slots for material " +
                            $"{_materialIndex + 1}/{_importedMaterials.Count}");
                    }
                }

                CookedMaterialPipeline? pipeline = _pipelines.Count > 0
                    ? _pipelines[_materialIndex]
                    : null;
                _materials[_materialIndex] =
                    _owner.RegisterImportedMaterial(
                        _importedMaterials[_materialIndex],
                        pipeline,
                        _materialOwnership ??
                            throw new InvalidOperationException(
                                "Material upload ownership is unavailable."),
                        _dynamicTextureIndices,
                        ref _defaultWhiteSubstitutions,
                        ref _defaultNormalSubstitutions,
                        ref _defaultBlackSubstitutions,
                        ref _blendMaterialCount,
                        prepared.Sources,
                        _materialPreloadReferences);

                _materialIndex++;
                _preparedMaterialTextures = null;
                _materialPreloadReferences = null;
                _materialTextureIndex = 0;
                ContentUploadStepResult progress = Yield(
                    ContentLoadStage.Uploading,
                    MaterialProgress(0.0),
                    $"uploaded {_materialIndex}/{_importedMaterials.Count} materials " +
                    $"({prepared.EncodedBytes / (1024.0 * 1024.0):F1} MiB authenticated)");
                if (_materialIndex < _importedMaterials.Count)
                {
                    FillMaterialPreparationWindow();
                    return progress;
                }

                _materialRegistrationsComplete = true;
                if (_pendingTextureUploadBatches.Count > 0)
                {
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        0.65,
                        $"all materials registered; draining " +
                        $"{_pendingTextureUploadBatches.Count} queued " +
                        "texture upload batches");
                }

                return CompleteMaterialRegistrationPhaseLocked();
            }

            private bool TryDrainCompletedTextureUploadsLocked()
            {
                while (_pendingTextureUploadBatches.Count > 0)
                {
                    IModelTextureUploadBatch pending =
                        _pendingTextureUploadBatches.Peek();
                    if (!pending.TryCompleteGpuWork())
                        return false;
                    pending.Dispose();
                    _pendingTextureUploadBatches.Dequeue();
                }

                return true;
            }

            private ContentUploadStepResult
                CompleteMaterialRegistrationPhaseLocked()
            {
                ModelUploadRollbackLedger rollback = _rollback ??
                    throw new InvalidOperationException(
                        "Model upload rollback ownership is unavailable.");
                rollback.TrackBaseMaterials(_materials);
                _baseMaterialsTransferredToRollback = true;
                _materialOwnership = null;
                _owner.UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage.AfterMaterialRegistration);
                _phase = CookedModelUploadPhase.PrimitiveMaterials;
                BeginProfiledPhase(CookedModelUploadPhase.PrimitiveMaterials);
                return Yield(
                    ContentLoadStage.Uploading,
                    0.65,
                    $"uploaded {_materialIndex}/" +
                    $"{_importedMaterials.Count} materials; " +
                    "all texture GPU work complete");
            }

            private ContentUploadStepResult
                ExecutePrimitiveMaterialStepLocked(
                    in ContentUploadSliceBudget budget)
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                int first = _primitiveIndex;
                while (_primitiveIndex < _cpu.SubMeshes.Length &&
                       _primitiveIndex - first < PrimitiveMaterialBatchSize)
                {
                    ModelUploadRollbackLedger rollback = _rollback ??
                        throw new InvalidOperationException(
                            "Model upload rollback ownership is unavailable.");
                    if (_cooked is not null)
                    {
                        CookedSubMeshRecord cookedSubMesh =
                            _cooked.Mesh.SubMeshes[_primitiveIndex];
                        _subMeshMaterials[_primitiveIndex] =
                            _owner.ResolveCookedPrimitiveMaterial(
                                _primitiveIndex,
                                cookedSubMesh,
                                _materials,
                                _profilesBySubMesh,
                                rollback,
                                _profileDiagnostics,
                                out _primitiveProfiles[_primitiveIndex]);
                    }
                    else
                    {
                        PreparedModelSubMeshData subMesh =
                            _cpu.SubMeshes[_primitiveIndex];
                        GiPrimitiveTransportProfile[] profiles =
                            _cpu.RuntimeProfiles ??
                            throw new InvalidOperationException(
                                "Source model primitive profiles are unavailable.");
                        GiPrimitiveTransportProfile profile =
                            profiles[_primitiveIndex];
                        MaterialHandle baseMaterial =
                            _materials[subMesh.MaterialIndex];
                        if (!_owner.TryAuthenticatePrimitiveTextureHashes(
                                baseMaterial,
                                profile,
                                out string? authenticationFailure))
                        {
                            profile =
                                RuntimePrimitiveTransportProfileBuilder
                                    .InvalidateProfile(
                                        profile,
                                        "Runtime primitive profile was invalidated after texture upload: " +
                                        authenticationFailure);
                            profiles[_primitiveIndex] = profile;
                            if (_profileDiagnostics.Count < 16)
                            {
                                _profileDiagnostics.Add(
                                    $"Submesh {_primitiveIndex} ('{subMesh.Name}'): " +
                                    authenticationFailure);
                            }
                        }

                        _primitiveProfiles[_primitiveIndex] = profile;
                        _subMeshMaterials[_primitiveIndex] =
                            _owner.RegisterPrimitiveProfileMaterial(
                                baseMaterial,
                                profile,
                                rollback);
                        _owner.UploadPublicationFaultInjector?.Invoke(
                            ModelUploadPublicationStage
                                .AfterPrimitiveMaterialRegistration);
                    }
                    _primitiveIndex++;

                    if (budget.RemainingCpuTime > TimeSpan.Zero &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(started) >=
                        TimeSpan.FromTicks(Math.Max(
                            1,
                            budget.RemainingCpuTime.Ticks / 2)))
                    {
                        break;
                    }
                }

                double fraction = _cpu.SubMeshes.Length == 0
                    ? 1.0
                    : _primitiveIndex /
                      (double)_cpu.SubMeshes.Length;
                ContentUploadStepResult result = Yield(
                    ContentLoadStage.Uploading,
                    0.65 + fraction * 0.10,
                    $"resolved {_primitiveIndex}/{_cpu.SubMeshes.Length} " +
                    "primitive material bindings");
                if (_primitiveIndex < _cpu.SubMeshes.Length)
                    return result;

                _registrationPreparation = Task.Run(
                    BuildRegistrations,
                    _preparationCancellation.Token);
                _phase = CookedModelUploadPhase.PreparingRegistrations;
                BeginProfiledPhase(CookedModelUploadPhase.PreparingRegistrations);
                return result;
            }

            private ContentUploadStepResult
                ExecuteRegistrationPreparationStepLocked()
            {
                Task<MeshManager.MeshRegistrationData[]> task =
                    _registrationPreparation ??
                    throw new InvalidOperationException(
                        "Mesh registration preparation was not started.");
                if (!task.IsCompleted)
                {
                    return Yield(
                        ContentLoadStage.Preparing,
                        0.78,
                        "finalizing persistent mesh upload streams in the background");
                }

                if (_residencyPreparation is { IsCompleted: false })
                {
                    return Yield(
                        ContentLoadStage.Preparing,
                        0.79,
                        "authenticating pinned meshlet pages in the background");
                }

                _registrations = task.GetAwaiter().GetResult();
                _registrationPreparation = null;
                if (_residencyPreparation is { } residencyPreparation)
                {
                    MeshletPhysicalResidencySessionOpenResult result =
                        residencyPreparation.GetAwaiter().GetResult();
                    _residencyPreparation = null;
                    if (result.Active && result.Session is { } session)
                    {
                        _residencySession = session;
                        foreach (MeshletStreamingSubMeshActivation activation in
                                 result.ActivationPlan.SubMeshes)
                        {
                            if (!activation.Active)
                                continue;
                            _registrations[activation.SubMeshIndex]
                                .EnableManagedPhysicalResidency(
                                    session.Package.GetSubMeshGpuBinding(
                                        activation.SubMeshIndex));
                        }
                        _profileDiagnostics.Add(
                            $"Managed meshlet residency active for " +
                            $"{result.ActivationPlan.ActiveSubMeshCount} submeshes; " +
                            $"estimated VRAM avoided=" +
                            $"{result.ActivationPlan.EstimatedBytesAvoided} bytes.");
                    }
                    else if (!string.IsNullOrWhiteSpace(
                                 result.FallbackReason))
                    {
                        _profileDiagnostics.Add(
                            "Managed meshlet residency retained full-resident storage: " +
                            result.FallbackReason);
                    }
                }
                _phase = CookedModelUploadPhase.RegisteringMeshes;
                BeginProfiledPhase(CookedModelUploadPhase.RegisteringMeshes);
                return Yield(
                    ContentLoadStage.WaitingForUpload,
                    0.82,
                    $"{_registrations.Length} mesh streams ready for GPU submission");
            }

            private ContentUploadStepResult ExecuteMeshRegistrationStepLocked(
                in ContentUploadSliceBudget budget)
            {
                MeshManager.MeshRegistrationData[] registrations =
                    _registrations ?? throw new InvalidOperationException(
                        "Prepared mesh registrations are unavailable.");
                _meshes ??= new MeshHandle[registrations.Length];

                if (_pendingMeshUpload == null)
                {
                    long submissionStarted =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                    int first = _meshRegistrationIndex;
                    long maximumSubmissionBytes = first == 0
                        ? Math.Min(
                            budget.MaximumSubmissionBytes,
                            FirstMeshSubmissionBytes)
                        : budget.MaximumSubmissionBytes;
                    int count = SelectMeshRegistrationBatch(
                        registrations,
                        first,
                        maximumSubmissionBytes,
                        out ulong stagedBytes);
                    if (count <= 0)
                    {
                        throw new InvalidOperationException(
                            "A non-empty cooked mesh upload produced an empty registration batch.");
                    }
                    Report(
                        ContentLoadStage.Uploading,
                        0.82 + 0.08 * first / registrations.Length,
                        $"submitting meshes {first + 1}-{first + count}/" +
                        $"{registrations.Length} " +
                        $"({stagedBytes / (1024.0 * 1024.0):F1} MiB staged)");
                    var registrationBatch = new ArraySegment<
                        MeshManager.MeshRegistrationData>(
                        registrations,
                        first,
                        count);
                    IModelMeshUpload upload = first == 0
                        ? _owner._backend.BeginMeshUploadWithCapacity(
                            registrationBatch,
                            registrations)
                        : _owner._backend.BeginMeshUpload(
                            registrationBatch);
                    ReportMeshUploadHostHitch(
                        "submission",
                        submissionStarted,
                        first,
                        count,
                        stagedBytes);
                    if (upload.Handles.Count != count)
                    {
                        upload.Dispose();
                        throw new InvalidOperationException(
                            $"The mesh backend returned {upload.Handles.Count} handles " +
                            $"for a {count}-mesh registration batch.");
                    }

                    _pendingMeshUpload = upload;
                    _pendingMeshFirst = first;
                    _pendingMeshCount = count;
                    _pendingMeshStagedBytes = stagedBytes;
                }

                IModelMeshUpload pending = _pendingMeshUpload;
                long completionStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                if (!pending.TryCompleteGpuWork())
                {
                    ReportMeshUploadHostHitch(
                        "fence-poll",
                        completionStarted,
                        _pendingMeshFirst,
                        _pendingMeshCount,
                        _pendingMeshStagedBytes);
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        0.82 + 0.08 * _meshRegistrationIndex /
                        registrations.Length,
                        $"waiting for GPU completion of meshes " +
                        $"{_pendingMeshFirst + 1}-" +
                        $"{_pendingMeshFirst + _pendingMeshCount}/" +
                        $"{registrations.Length} " +
                        $"({_pendingMeshStagedBytes / (1024.0 * 1024.0):F1} MiB staged)");
                }
                ReportMeshUploadHostHitch(
                    "fence-publication",
                    completionStarted,
                    _pendingMeshFirst,
                    _pendingMeshCount,
                    _pendingMeshStagedBytes);

                for (int i = 0; i < _pendingMeshCount; i++)
                {
                    _meshes[_pendingMeshFirst + i] =
                        pending.Handles[i];
                }
                (_rollback ?? throw new InvalidOperationException(
                    "Model upload rollback ownership is unavailable."))
                    .TrackMeshes(pending.Handles);
                _meshRegistrationIndex = checked(
                    _pendingMeshFirst + _pendingMeshCount);
                pending.Dispose();
                _pendingMeshUpload = null;
                _pendingMeshFirst = 0;
                _pendingMeshCount = 0;
                _pendingMeshStagedBytes = 0;
                if (_meshRegistrationIndex < registrations.Length)
                {
                    double fraction = _meshRegistrationIndex /
                        (double)registrations.Length;
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        0.82 + fraction * 0.08,
                        $"uploaded {_meshRegistrationIndex}/" +
                        $"{registrations.Length} persistent mesh streams");
                }

                if (_cooked is not null)
                {
                    _opacityMicromapRuntimeRegistrationCount =
                        _owner.RegisterCookedOpacityMicromaps(
                            _cooked,
                            registrations,
                            _meshes,
                            _subMeshMaterials,
                            out _opacityMicromapRuntimeDetail);
                }
                _owner.UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage.AfterMeshRegistration);
                if (_residencySession is { } residencySession)
                {
                    for (int index = 0;
                         index < registrations.Length;
                         index++)
                    {
                        if (registrations[index].ManagedResidencyBinding is null)
                            continue;
                        uint vertexOffset = registrations[index]
                            .RegisteredVertexOffset ??
                            throw new InvalidOperationException(
                                "A managed mesh registration did not publish its global vertex offset.");
                        residencySession.FinalizeSubMeshVertexOffset(
                            index,
                            vertexOffset);
                    }
                    _phase =
                        CookedModelUploadPhase.AwaitingResidencyBootstrap;
                    BeginProfiledPhase(
                        CookedModelUploadPhase.AwaitingResidencyBootstrap);
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        0.90,
                        "waiting for fence-safe pinned meshlet residency");
                }

                _phase = CookedModelUploadPhase.AttachingRenderObjects;
                BeginProfiledPhase(CookedModelUploadPhase.AttachingRenderObjects);
                return Yield(
                    ContentLoadStage.AwaitingGpu,
                    0.90,
                    "mesh upload submitted; publishing render objects");
            }

            private ContentUploadStepResult
                ExecuteResidencyBootstrapStepLocked()
            {
                MeshletPhysicalResidencySession session =
                    _residencySession ??
                    throw new InvalidOperationException(
                        "The managed residency bootstrap session is unavailable.");
                if (!session.IsReadyForPublication)
                {
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        0.90,
                        "pinned meshlet pages are uploading through the frame budget");
                }
                _phase = CookedModelUploadPhase.AttachingRenderObjects;
                BeginProfiledPhase(
                    CookedModelUploadPhase.AttachingRenderObjects);
                return Yield(
                    ContentLoadStage.AwaitingGpu,
                    0.90,
                    "pinned meshlet residency is fence-safe; publishing render objects");
            }

            private static void ReportMeshUploadHostHitch(
                string operation,
                long started,
                int first,
                int count,
                ulong stagedBytes)
            {
                double elapsedMilliseconds =
                    System.Diagnostics.Stopwatch
                        .GetElapsedTime(started)
                        .TotalMilliseconds;
                if (elapsedMilliseconds <= 33.0)
                    return;

                Console.WriteLine(
                    $"Mesh upload host hitch: operation={operation}, " +
                    $"elapsed={elapsedMilliseconds:F3}ms, " +
                    $"meshes={first + 1}-{first + count}, " +
                    $"staged={stagedBytes / (1024.0 * 1024.0):F1}MiB.");
            }

            private static int SelectMeshRegistrationBatch(
                IReadOnlyList<MeshManager.MeshRegistrationData> registrations,
                int first,
                long maximumSubmissionBytes,
                out ulong stagedBytes)
            {
                if ((uint)first >= (uint)registrations.Count)
                    throw new ArgumentOutOfRangeException(nameof(first));

                ulong maximum = checked((ulong)Math.Max(
                    1,
                    maximumSubmissionBytes));
                stagedBytes = 0;
                int count = 0;
                while (first + count < registrations.Count)
                {
                    ulong next = registrations[first + count]
                        .EstimateCookedUploadStagingBytes();
                    if (count > 0 &&
                        (stagedBytes >= maximum ||
                         next > maximum - stagedBytes))
                    {
                        break;
                    }

                    stagedBytes = checked(stagedBytes + next);
                    count++;
                }

                return count;
            }

            private ContentUploadStepResult ExecuteRenderObjectStepLocked(
                in ContentUploadSliceBudget budget)
            {
                MeshHandle[] meshes = _meshes ??
                    throw new InvalidOperationException(
                        "Uploaded mesh handles are unavailable.");
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                int first = _renderObjectIndex;
                while (_renderObjectIndex < meshes.Length &&
                       _renderObjectIndex - first < RenderObjectBatchSize)
                {
                    PreparedModelSubMeshData subMesh =
                        _cpu.SubMeshes[_renderObjectIndex];
                    RenderObject renderObject =
                        subMesh.SkinIndex >= 0 &&
                        subMesh.SkinIndex < _cpu.Model.Skins.Count
                            ? new SkinnedRenderObject(
                                meshes[_renderObjectIndex],
                                _subMeshMaterials[_renderObjectIndex])
                            {
                                SkinIndex = subMesh.SkinIndex,
                                Animator = CreateAnimator(
                                    _cpu.Model,
                                    subMesh.SkinIndex),
                                SkinningBindTransform =
                                    subMesh.SkinningBindTransform
                            }
                            : new RenderObject(
                                meshes[_renderObjectIndex],
                                _subMeshMaterials[_renderObjectIndex]);
                    renderObject.Name = string.IsNullOrWhiteSpace(subMesh.Name)
                        ? _cpu.Model.Name
                        : subMesh.Name;
                    renderObject.LocalMeshBounds = subMesh.BoundingBox;
                    _cpu.Model.Add(renderObject);
                    _owner.AttachRenderObjectResourceLifetime(renderObject);
                    (_rollback ?? throw new InvalidOperationException(
                        "Model upload rollback ownership is unavailable."))
                        .MarkRenderObjectAttached();
                    _owner.UploadPublicationFaultInjector?.Invoke(
                        ModelUploadPublicationStage
                            .AfterRenderObjectAttachment);
                    _renderObjectIndex++;

                    if (budget.RemainingCpuTime > TimeSpan.Zero &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(started) >=
                        TimeSpan.FromTicks(Math.Max(
                            1,
                            budget.RemainingCpuTime.Ticks / 2)))
                    {
                        break;
                    }
                }

                double fraction = meshes.Length == 0
                    ? 1.0
                    : _renderObjectIndex / (double)meshes.Length;
                ContentUploadStepResult result = Yield(
                    ContentLoadStage.Uploading,
                    0.90 + fraction * 0.08,
                    $"attached {_renderObjectIndex}/{meshes.Length} render objects");
                if (_renderObjectIndex == meshes.Length)
                {
                    _phase = CookedModelUploadPhase.Finalizing;
                    BeginProfiledPhase(CookedModelUploadPhase.Finalizing);
                }
                return result;
            }

            private ContentUploadStepResult FinalizeLocked()
            {
                int opacityMicromapPayloadAcceptedCount =
                    _cooked?.OpacityMicromapLoadStatus.Accepted == true &&
                    _cooked.OpacityMicromapPayload is not null
                        ? 1
                        : 0;
                RuntimePrimitiveTransportProfileBuildDiagnostics?
                    sourceDiagnostics = _cpu.RuntimeProfileDiagnostics;
                var diagnostics = new ModelRenderUploadDiagnostics(
                    _cpu.Model.Name,
                    _cpu.Model.RenderObjects.Count,
                    _cpu.SubMeshes.Length,
                    (_rollback ?? throw new InvalidOperationException(
                        "Model upload rollback ownership is unavailable."))
                        .TrackedMaterialCount,
                    _dynamicTextureIndices.Count,
                    _defaultWhiteSubstitutions,
                    _defaultNormalSubstitutions,
                    _defaultBlackSubstitutions,
                    _blendMaterialCount,
                    _primitiveProfiles.Count(static profile =>
                        profile != null &&
                        profile.IsComplete &&
                        profile.Quality !=
                            GiPrimitiveTransportProfileQuality.Invalid),
                    _primitiveProfiles.Count(static profile =>
                        profile != null && !profile.IsComplete ||
                        profile != null &&
                        profile.Quality ==
                            GiPrimitiveTransportProfileQuality.Invalid),
                    sourceDiagnostics?.ProfileCacheHitCount ?? 0,
                    sourceDiagnostics?.ProfileCacheMissCount ?? 0,
                    sourceDiagnostics?.TextureAnalysisFailureCount ?? 0,
                    sourceDiagnostics?.PackageOmittedEmissiveRecordCount ??
                    _cooked?.Materials.PrimitiveTransportProfiles.Sum(
                        static profile => Math.Max(
                            profile.EmissiveCandidateTriangleCount -
                            profile.EmissiveTriangles.Length,
                            0)) ?? 0,
                    string.Join(" | ", _profileDiagnostics),
                    opacityMicromapPayloadAcceptedCount,
                    _opacityMicromapRuntimeRegistrationCount,
                    _opacityMicromapRuntimeDetail);
                _owner.RegisterModelMaterialLifetime(
                    _cpu.Model,
                    _materials);
                if (_residencySession is { } residencySession)
                {
                    _cpu.Model.AddSharedDisposeAction(
                        residencySession.Dispose);
                    _residencySession = null;
                }
                _rollback!.TransferBaseMaterialsToModel();
                _owner.UploadPublicationFaultInjector?.Invoke(
                    ModelUploadPublicationStage.AfterBaseMaterialTransfer);
                _rollback.Commit();
                _owner.SetLastUploadDiagnostics(diagnostics);
                _result = _cpu.Model;
                _phase = CookedModelUploadPhase.Completed;
                BeginProfiledPhase(CookedModelUploadPhase.Completed);
                ReleaseUploadSlotLocked();
                _preparationCancellation.Dispose();
                Report(
                    ContentLoadStage.Ready,
                    1.0,
                    "model is fully resident");
                return ContentUploadStepResult.Complete(
                    _cpu.TotalBytes,
                    _cpu.TotalBytes,
                    "model is fully resident");
            }

            private void BeginProfiledPhase(CookedModelUploadPhase next)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                Console.WriteLine(
                    $"Model upload profile: source={_cpu.SourceKind}, " +
                    $"model='{_cpu.Model.Name}', " +
                    $"phase={next}, " +
                    $"previousPhaseElapsed={System.Diagnostics.Stopwatch.GetElapsedTime(_phaseStartedTimestamp, now).TotalMilliseconds:F3}ms, " +
                    $"total={System.Diagnostics.Stopwatch.GetElapsedTime(_uploadStartedTimestamp, now).TotalMilliseconds:F3}ms.");
                _phaseStartedTimestamp = now;
            }

            private MeshManager.MeshRegistrationData[] BuildRegistrations()
            {
                var registrations =
                    new MeshManager.MeshRegistrationData[_cpu.Meshes.Length];
                int completedCount = 0;
                var options = new ParallelOptions
                {
                    CancellationToken = _preparationCancellation.Token,
                    MaxDegreeOfParallelism = Math.Clamp(
                        Environment.ProcessorCount / 2,
                        1,
                        4)
                };
                Parallel.For(
                    0,
                    registrations.Length,
                    options,
                    i =>
                    {
                        PreparedModelMeshData mesh = _cpu.Meshes[i];
                        MeshManager.MeshRegistrationData registration =
                            mesh.SourceVertices is { } sourceVertices
                                ? new MeshManager.MeshRegistrationData(
                                    sourceVertices,
                                    mesh.Indices,
                                    mesh.Meshlets,
                                    mesh.MeshletVertices,
                                    mesh.MeshletTriangles,
                                    mesh.Lod0Count,
                                    mesh.Lod1Count,
                                    mesh.Lod2Count,
                                    skinningData: mesh.Skinning.Length == 0
                                        ? null
                                        : mesh.Skinning,
                                    primitiveTransportProfile:
                                        _primitiveProfiles[i],
                                    causticTopologyEvidence:
                                        mesh.CausticTopologyEvidence,
                                    lod1SimplificationError:
                                        mesh.Lod1SimplificationError,
                                    lod2SimplificationError:
                                        mesh.Lod2SimplificationError,
                                    hierarchyNodes:
                                        mesh.HierarchyNodes,
                                    hierarchyRootNode:
                                        mesh.HierarchyRootNode,
                                    coarseRayProxyIndices:
                                        mesh.CoarseRayProxyIndices)
                                : new MeshManager.MeshRegistrationData(
                                    mesh.VertexPositions,
                                    mesh.VertexNormalTangents,
                                    mesh.VertexUvColors,
                                    mesh.Indices,
                                    mesh.Meshlets,
                                    mesh.MeshletVertices,
                                    mesh.MeshletTriangles,
                                    mesh.Lod0Count,
                                    mesh.Lod1Count,
                                    mesh.Lod2Count,
                                    mesh.Skinning.Length == 0
                                        ? null
                                        : mesh.Skinning,
                                    _primitiveProfiles[i],
                                    mesh.CausticTopologyEvidence,
                                    mesh.Lod1SimplificationError,
                                    mesh.Lod2SimplificationError,
                                    mesh.HierarchyNodes,
                                    mesh.HierarchyRootNode,
                                    mesh.CoarseRayProxyIndices);
                        registration.PrepareTransportGeometry();
                        registrations[i] = registration;

                        int completed = Interlocked.Increment(
                            ref completedCount);
                        if ((completed & 63) != 0 &&
                            completed != registrations.Length)
                        {
                            return;
                        }

                        double fraction = registrations.Length == 0
                            ? 1.0
                            : completed /
                              (double)registrations.Length;
                        Report(
                            ContentLoadStage.Preparing,
                            0.75 + fraction * 0.07,
                            $"finalized {completed}/" +
                            $"{registrations.Length} mesh streams");
                    });

                return registrations;
            }

            private void FillMaterialPreparationWindow()
            {
                if (_materialIndex >= _importedMaterials.Count)
                    return;
                int end = Math.Min(
                    _importedMaterials.Count,
                    checked(_materialIndex + MaterialPreparationLookahead));
                for (int index = _materialIndex; index < end; index++)
                {
                    if (_materialPreparations[index] != null)
                        continue;

                    ModelMaterial material = _importedMaterials[index];
                    _materialPreparations[index] = Task.Run(
                        () => _owner.PrepareMaterialTextureSources(
                            material,
                            _preparedTextureSourceCache,
                            _preparationCancellation.Token),
                        _preparationCancellation.Token);
                }
            }

            private ContentUploadStepResult CancelLocked()
            {
                List<Exception>? rollbackFailures = null;
                if (_pendingMeshUpload != null)
                {
                    if (!_pendingMeshUpload.TryCancelGpuWork())
                    {
                        int total = _registrations?.Length ??
                                    _pendingMeshFirst +
                                    _pendingMeshCount;
                        return Yield(
                            ContentLoadStage.AwaitingGpu,
                            0.82 + 0.08 * _meshRegistrationIndex /
                            Math.Max(total, 1),
                            "cancellation requested; draining submitted " +
                            $"mesh upload {_pendingMeshFirst + 1}-" +
                            $"{_pendingMeshFirst + _pendingMeshCount}/" +
                            $"{total}");
                    }

                    _pendingMeshUpload.Dispose();
                    _pendingMeshUpload = null;
                    _pendingMeshFirst = 0;
                    _pendingMeshCount = 0;
                    _pendingMeshStagedBytes = 0;
                }

                if (!TryDrainCompletedTextureUploadsLocked())
                {
                    return Yield(
                        ContentLoadStage.AwaitingGpu,
                        MaterialProgress(0.0),
                        "cancellation requested; draining " +
                        $"{_pendingTextureUploadBatches.Count} submitted " +
                        "texture upload batches");
                }

                if (!TryDrainCancelledPreparationLocked(
                        ref rollbackFailures))
                {
                    return Yield(
                        ContentLoadStage.Preparing,
                        0.30,
                        "cancellation requested; draining background model preparation");
                }

                if (!_baseMaterialsTransferredToRollback &&
                    _materialOwnership != null)
                {
                    Exception? failure =
                        _materialOwnership.TryRollback();
                    if (failure != null)
                    {
                        _owner.PublishPendingMaterialRollbackLocked(
                            _materialOwnership);
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                if (_rollback != null)
                {
                    Exception? failure = _rollback.TryRollback();
                    if (failure != null)
                    {
                        _owner.PublishPendingModelUploadRollbackLocked(
                            _rollback);
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                _phase = CookedModelUploadPhase.Cancelled;
                ReleaseUploadSlotLocked();
                _preparationCancellation.Dispose();
                if (rollbackFailures is { Count: > 0 })
                {
                    throw new AggregateException(
                        "Cooperative model upload cancellation could not " +
                        "complete ownership rollback.",
                        rollbackFailures);
                }

                Report(
                    ContentLoadStage.Cancelled,
                    0,
                    "model upload cancelled and rolled back");
                return ContentUploadStepResult.Cancelled(
                    detail: "model upload cancelled and rolled back");
            }

            private bool TryDrainCancelledPreparationLocked(
                ref List<Exception>? failures)
            {
                bool materialsDrained = true;
                for (int i = 0; i < _materialPreparations.Length; i++)
                {
                    if (!TryObserveCancelledPreparation(
                            ref _materialPreparations[i],
                            ref failures))
                    {
                        materialsDrained = false;
                    }
                }
                bool registrationsDrained =
                    TryObserveCancelledPreparation(
                        ref _registrationPreparation,
                        ref failures);
                bool residencyDrained =
                    TryObserveCancelledResidencyPreparation(
                        ref failures);
                return materialsDrained &&
                       registrationsDrained &&
                       residencyDrained;
            }

            private bool TryObserveCancelledResidencyPreparation(
                ref List<Exception>? failures)
            {
                Task<MeshletPhysicalResidencySessionOpenResult>? task =
                    _residencyPreparation;
                if (task == null)
                {
                    _residencySession?.Dispose();
                    _residencySession = null;
                    return true;
                }
                if (!task.IsCompleted)
                    return false;
                try
                {
                    task.GetAwaiter().GetResult().Session?.Dispose();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(failure);
                }
                _residencyPreparation = null;
                _residencySession?.Dispose();
                _residencySession = null;
                return true;
            }

            private static bool TryObserveCancelledPreparation<T>(
                ref Task<T>? preparation,
                ref List<Exception>? failures)
            {
                Task<T>? task = preparation;
                if (task == null)
                    return true;
                if (!task.IsCompleted)
                    return false;

                try
                {
                    _ = task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Cancellation was requested explicitly; observing the
                    // task is the only remaining ownership obligation.
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(failure);
                }

                preparation = null;
                return true;
            }

            private Exception RollbackFailureLocked(
                Exception uploadFailure)
            {
                List<Exception>? rollbackFailures = null;
                DrainPreparationAfterFailureLocked();
                if (_pendingMeshUpload != null)
                {
                    try
                    {
                        // Exceptional cleanup may block so candidate buffers
                        // and staging allocations cannot outlive submitted
                        // commands that still reference them.
                        _pendingMeshUpload.Dispose();
                        _pendingMeshUpload = null;
                        _pendingMeshFirst = 0;
                        _pendingMeshCount = 0;
                        _pendingMeshStagedBytes = 0;
                    }
                    catch (Exception failure)
                    {
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                while (_pendingTextureUploadBatches.Count > 0)
                {
                    IModelTextureUploadBatch pendingTextureUpload =
                        _pendingTextureUploadBatches.Dequeue();
                    try
                    {
                        // Failure cleanup is exceptional and may block. It
                        // must complete submitted copies before image
                        // ownership rollback can destroy their resources.
                        pendingTextureUpload.Dispose();
                    }
                    catch (Exception failure)
                    {
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                if (!_baseMaterialsTransferredToRollback &&
                    _materialOwnership != null)
                {
                    Exception? failure =
                        _materialOwnership.TryRollback();
                    if (failure != null)
                    {
                        _owner.PublishPendingMaterialRollbackLocked(
                            _materialOwnership);
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                if (_rollback != null)
                {
                    Exception? failure = _rollback.TryRollback();
                    if (failure != null)
                    {
                        _owner.PublishPendingModelUploadRollbackLocked(
                            _rollback);
                        (rollbackFailures ??= []).Add(failure);
                    }
                }

                _phase = CookedModelUploadPhase.Cancelled;
                ReleaseUploadSlotLocked();
                _preparationCancellation.Dispose();
                return rollbackFailures is not { Count: > 0 }
                    ? uploadFailure
                    : new AggregateException(
                        "Cooperative model upload failed and ownership " +
                        "rollback was incomplete.",
                        new[] { uploadFailure }.Concat(
                            rollbackFailures));
            }

            private void DrainPreparationAfterFailureLocked()
            {
                try
                {
                    _preparationCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                for (int i = 0; i < _materialPreparations.Length; i++)
                {
                    ObserveFailedPreparation(
                        ref _materialPreparations[i]);
                }

                ObserveFailedPreparation(ref _registrationPreparation);
                if (_residencyPreparation is { } residencyPreparation)
                {
                    try
                    {
                        residencyPreparation.GetAwaiter().GetResult()
                            .Session?.Dispose();
                    }
                    catch
                    {
                    }
                    _residencyPreparation = null;
                }
                _residencySession?.Dispose();
                _residencySession = null;
            }

            private static void ObserveFailedPreparation<T>(
                ref Task<T>? preparation)
            {
                Task<T>? task = preparation;
                if (task == null)
                    return;

                try
                {
                    _ = task.GetAwaiter().GetResult();
                }
                catch
                {
                    // The upload failure that initiated rollback remains the
                    // canonical error. Speculative preparation only needs to
                    // be cancelled and observed before its token is disposed.
                }

                preparation = null;
            }

            private void ReleaseUploadSlotLocked()
            {
                if (!_ownsUploadSlot)
                    return;
                if (!ReferenceEquals(
                        _owner._cooperativeUploadOwner,
                        this))
                {
                    throw new InvalidOperationException(
                        "Cooperative upload ownership changed before release.");
                }

                _owner._cooperativeUploadOwner = null;
                _ownsUploadSlot = false;
                _owner.EndUploadLocked();
            }

            private double MaterialProgress(double tail)
            {
                double fraction = _importedMaterials.Count == 0
                    ? 1.0
                    : (_materialIndex + tail) /
                      _importedMaterials.Count;
                return 0.30 + fraction * 0.35;
            }

            private ContentUploadStepResult Yield(
                ContentLoadStage stage,
                double fraction,
                string detail)
            {
                long completed = checked((long)Math.Round(
                    _cpu.TotalBytes * Math.Clamp(fraction, 0.0, 1.0)));
                ReportUploadWorkProgress(
                    _progress,
                    stage,
                    completed,
                    _cpu.TotalBytes,
                    detail);
                return ContentUploadStepResult.Yield(
                    completed,
                    _cpu.TotalBytes,
                    detail);
            }

            private void Report(
                ContentLoadStage stage,
                double fraction,
                string detail)
            {
                long completed = checked((long)Math.Round(
                    _cpu.TotalBytes * Math.Clamp(fraction, 0.0, 1.0)));
                ReportUploadWorkProgress(
                    _progress,
                    stage,
                    completed,
                    _cpu.TotalBytes,
                    detail);
            }
        }

        private enum CookedModelUploadPhase
        {
            WaitingForOwnership,
            Materials,
            PrimitiveMaterials,
            PreparingRegistrations,
            RegisteringMeshes,
            AwaitingResidencyBootstrap,
            AttachingRenderObjects,
            Finalizing,
            Completed,
            Cancelled
        }

        private sealed record PreparedModelCpuData(
            Model Model,
            PreparedModelMeshData[] Meshes,
            PreparedModelSubMeshData[] SubMeshes,
            IReadOnlyList<ModelMaterial> Materials,
            IReadOnlyList<CookedMaterialPipeline> Pipelines,
            GiPrimitiveTransportProfile[]? RuntimeProfiles,
            RuntimePrimitiveTransportProfileBuildDiagnostics?
                RuntimeProfileDiagnostics,
            long TotalBytes,
            string SourceKind);

        private sealed record PreparedModelMeshData(
            GPUVertex[]? SourceVertices,
            GPUVertexPositionStream[] VertexPositions,
            GPUVertexNormalTangentStream[] VertexNormalTangents,
            GPUVertexUvColorStream[] VertexUvColors,
            uint[] Indices,
            Meshlet[] Meshlets,
            uint[] MeshletVertices,
            uint[] MeshletTriangles,
            int Lod0Count,
            int Lod1Count,
            int Lod2Count,
            GPUVertexSkinningData[] Skinning,
            ModelGiCausticHeroTopologyEvidence CausticTopologyEvidence,
            float Lod1SimplificationError,
            float Lod2SimplificationError,
            MeshletHierarchyNode[] HierarchyNodes,
            int HierarchyRootNode,
            uint[] CoarseRayProxyIndices);

        private sealed record PreparedModelSubMeshData(
            string Name,
            int MaterialIndex,
            int SkinIndex,
            CoreMatrix4x4 SkinningBindTransform,
            CoreBoundingBox BoundingBox);

        private readonly record struct PreparedTextureSourceCacheKey(
            string Identity,
            TextureSourceKind SourceKind,
            TextureContainerKind ContainerKind,
            TextureSamplerDescription Sampler,
            bool Srgb,
            TextureSemantic Semantic,
            RuntimeTextureMipPolicy MipPolicy);

        private sealed class PreparedMaterialTextureSources
        {
            public PreparedMaterialTextureSources(
                IReadOnlyList<PreparedMaterialTextureSlot> slots,
                long encodedBytes)
            {
                Slots = slots ?? throw new ArgumentNullException(
                    nameof(slots));
                EncodedBytes = encodedBytes;
                Sources = slots
                    .Select(static slot => slot.Source)
                    .ToArray();
            }

            public IReadOnlyList<PreparedMaterialTextureSlot> Slots
            {
                get;
            }

            public IReadOnlyList<ModelTextureSource?> Sources { get; }

            public long EncodedBytes { get; }
        }

        private sealed record PreparedMaterialTextureSlot(
            ModelTextureSource? Source,
            TextureSamplerDescription Sampler,
            bool GenerateMipmaps,
            bool Srgb,
            TextureSemantic Semantic,
            RuntimeTextureMipPolicy MipPolicy,
            PreparedTextureFallback Fallback)
        {
            public long EncodedBytes =>
                Source?.PreparedSnapshot?.EncodedBytes.LongLength ??
                Source?.EncodedByteLength ??
                0;
        }

        private enum PreparedTextureFallback
        {
            White,
            Normal,
            Black
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
