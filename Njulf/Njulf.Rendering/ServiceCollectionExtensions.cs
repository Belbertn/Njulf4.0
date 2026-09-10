using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Silk.NET.Windowing;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class RenderingServiceCollectionExtensions
    {
        public static IServiceCollection AddRendering(this IServiceCollection services, IWindow window)
        {
            return services.AddRendering(window, configure: null);
        }

        public static IServiceCollection AddRendering(
            this IServiceCollection services,
            IWindow window,
            Action<RenderingOptions>? configure)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (window == null)
                throw new ArgumentNullException(nameof(window));

            var options = new RenderingOptions();
            configure?.Invoke(options);
            options.ResolveAdvancedGiStartupProfile();

            services.AddSingleton(options);
            services.AddSingleton<IWindow>(window);

            services.TryAddSingleton(provider =>
            {
                var renderingOptions = provider.GetRequiredService<RenderingOptions>();
                var registeredWindow = provider.GetRequiredService<IWindow>();
                return new VulkanContext(
                    registeredWindow,
                    renderingOptions.ValidationSettings,
                    provider.GetService<RendererStartupLog>(),
                    DeviceRequirementOverride.FromEnvironment(),
                    renderingOptions.OptionalDeviceFeatures);
            });

            services.TryAddSingleton<SwapchainManager>();
            services.TryAddSingleton<SynchronizationManager>();
            services.TryAddSingleton<CommandBufferManager>();
            services.TryAddSingleton<GpuAllocationTracker>();
            services.TryAddSingleton<BufferManager>();
            services.TryAddSingleton(provider =>
            {
                var renderingOptions = provider.GetRequiredService<RenderingOptions>();
                return new StagingRing(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    renderingOptions.StagingBufferSize);
            });
            services.TryAddSingleton<FenceBasedDeleter>();
            services.TryAddSingleton<BindlessHeap>();
            services.TryAddSingleton(provider =>
            {
                var textureManager = new TextureManager(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetService<BindlessHeap>(),
                    provider.GetService<FenceBasedDeleter>());
                RenderingOptions options = provider.GetRequiredService<RenderingOptions>();
                textureManager.MaxLoadedTextureDimension = options.MaxImportedTextureDimension;
                textureManager.ActiveTextureBudgetProfile = options.TextureBudgetProfile;
                return textureManager;
            });
            services.TryAddSingleton<MeshManager>();
            services.TryAddSingleton<MaterialManager>();
            services.TryAddSingleton<OpacityMicromapRuntimeRegistrationStore>();
            services.TryAddSingleton(provider =>
                MeshletStreamingResidencyOptions.FromSettings(
                    provider.GetRequiredService<RenderingOptions>()
                        .InitialSettings.SceneSubmission));
            services.TryAddSingleton(provider =>
                provider.GetRequiredService<RenderingOptions>()
                    .InitialSettings.SceneSubmission);
            services.TryAddSingleton(provider =>
            {
                MeshletStreamingResidencyOptions residencyOptions =
                    provider.GetRequiredService<
                        MeshletStreamingResidencyOptions>();
                return new MeshletPhysicalPageCacheUploader(
                    residencyOptions.PhysicalPageCapacity);
            });
            services.TryAddSingleton(provider =>
                new MeshletStreamingResidencyCoordinator(
                    provider.GetRequiredService<
                        MeshletPhysicalPageCacheUploader>(),
                    provider.GetRequiredService<
                        MeshletStreamingResidencyOptions>()));
            services.TryAddSingleton(provider =>
                new MeshletFrameResidencyResolver(
                    provider.GetRequiredService<
                        MeshletStreamingResidencyCoordinator>(),
                    provider.GetRequiredService<
                        MeshletPhysicalPageCacheUploader>(),
                    provider.GetRequiredService<RenderingOptions>()
                        .InitialSettings.IsPerformanceOptimizationEnabled(
                            PerformanceOptimizationFeature
                                .ResolvedMeshletAddressing)));
            services.TryAddSingleton(provider =>
                new VulkanMeshletPhysicalResidencyResources(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetRequiredService<StagingRing>(),
                    provider.GetRequiredService<BindlessHeap>(),
                    provider.GetRequiredService<
                        MeshletPhysicalPageCacheUploader>(),
                    provider.GetRequiredService<
                        MeshletStreamingResidencyCoordinator>(),
                    provider.GetRequiredService<FenceBasedDeleter>()));
            services.TryAddSingleton<RenderThreadContentUploadDispatcher>();
            services.TryAddSingleton<Func<Njulf.Graphics.GraphicsDevice>>(provider =>
                () => Njulf.Graphics.RendererGraphicsExtensions.GetGraphicsDevice(provider.GetRequiredService<IRenderer>()));
            services.TryAddSingleton<IContentUploadDispatcher>(provider =>
                provider.GetRequiredService<RenderThreadContentUploadDispatcher>());
            services.TryAddSingleton<IContentUploadPump>(provider =>
                provider.GetRequiredService<RenderThreadContentUploadDispatcher>());
            services.TryAddSingleton<IModelRenderUploadService>(provider =>
                new ModelRenderUploadService(
                    provider.GetRequiredService<MeshManager>(),
                    provider.GetRequiredService<TextureManager>(),
                    provider.GetRequiredService<MaterialManager>(),
                    provider.GetRequiredService<
                        OpacityMicromapRuntimeRegistrationStore>(),
                    provider.GetRequiredService<
                        MeshletStreamingResidencyCoordinator>(),
                    provider.GetRequiredService<SceneSubmissionSettings>(),
                    provider.GetRequiredService<RenderingOptions>()
                        .InitialSettings.IsPerformanceOptimizationEnabled(
                            PerformanceOptimizationFeature
                                .MeshletWorkingSetAdmission)));
            services.TryAddSingleton<LightManager>();
            services.TryAddSingleton(provider =>
                new SceneDataBuilder(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<MeshManager>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetRequiredService<StagingRing>(),
                    provider.GetRequiredService<SynchronizationManager>(),
                    provider.GetRequiredService<MaterialManager>(),
                    provider.GetService<TextureManager>(),
                    provider.GetRequiredService<
                        MeshletFrameResidencyResolver>()));
            services.TryAddSingleton<RenderGraph>();

            services.TryAddSingleton(provider =>
            {
                RenderingOptions renderingOptions =
                    provider.GetRequiredService<RenderingOptions>();
                RendererStartupLog? startupLog =
                    provider.GetService<RendererStartupLog>();
                var renderer = new VulkanRenderer(
                    provider.GetRequiredService<IWindow>(),
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<SwapchainManager>(),
                    provider.GetRequiredService<SynchronizationManager>(),
                    provider.GetRequiredService<CommandBufferManager>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetRequiredService<TextureManager>(),
                    provider.GetRequiredService<MeshManager>(),
                    provider.GetRequiredService<MaterialManager>(),
                    provider.GetRequiredService<LightManager>(),
                    provider.GetRequiredService<BindlessHeap>(),
                    provider.GetRequiredService<RenderGraph>(),
                    provider.GetRequiredService<SceneDataBuilder>(),
                    provider.GetRequiredService<StagingRing>(),
                    provider.GetRequiredService<FenceBasedDeleter>(),
                    provider.GetRequiredService<IModelRenderUploadService>(),
                    ownsDependencies: false,
                    initialSettings: renderingOptions.InitialSettings,
                    startupLog: startupLog,
                    meshletPhysicalResidencyResources:
                        provider.GetRequiredService<
                            VulkanMeshletPhysicalResidencyResources>());

                ConfigureAdvancedGiStartup(
                    renderer,
                    renderingOptions,
                    startupLog);
                return renderer;
            });

            services.TryAddSingleton<IRenderer>(provider => provider.GetRequiredService<VulkanRenderer>());

            return services;
        }

        private static void ConfigureAdvancedGiStartup(
            VulkanRenderer renderer,
            RenderingOptions options,
            RendererStartupLog? startupLog)
        {
            const string profileStep = "AdvancedGI.StartupProfile";
            if (options.AdvancedGiStartupProfilePath is not null)
            {
                startupLog?.StepStarted(
                    profileStep,
                    options.AdvancedGiStartupProfilePath);
                startupLog?.StepSucceeded(
                    profileStep,
                    options.AdvancedGiStartupProfileStatus);
            }

            renderer.ConfigureAdvancedGiRuntimeContentBinding(
                options.AdvancedGiContentBinding);

            const string prerequisiteStep =
                "AdvancedGI.PrerequisiteManifest";
            if (options.AdvancedGiPrerequisiteManifestPath is { } prerequisitePath)
            {
                startupLog?.StepStarted(prerequisiteStep, prerequisitePath);
                bool accepted = renderer
                    .TryConfigureAdvancedGiPrerequisiteManifestFile(
                        prerequisitePath,
                        out string detail);
                startupLog?.StepSucceeded(
                    prerequisiteStep,
                    accepted
                        ? $"accepted:{detail}"
                        : $"rejected:{detail};canonical-gi-retained");
            }

            const string qualificationStep =
                "AdvancedGI.QualificationManifest";
            if (options.AdvancedGiQualificationManifestPath is { } qualificationPath)
            {
                startupLog?.StepStarted(qualificationStep, qualificationPath);
                bool accepted = renderer
                    .TryConfigureAdvancedGiQualificationManifestFile(
                        qualificationPath,
                        out string detail);
                startupLog?.StepSucceeded(
                    qualificationStep,
                    accepted
                        ? $"accepted:{detail}"
                        : $"rejected:{detail};canonical-gi-retained");
            }

            const string directionalShadowQualificationStep =
                "DirectionalShadow.QualificationManifest";
            if (options.DirectionalShadowQualificationManifestPath is
                { } directionalShadowQualificationPath)
            {
                startupLog?.StepStarted(
                    directionalShadowQualificationStep,
                    directionalShadowQualificationPath);
                bool accepted = renderer
                    .TryConfigureDirectionalShadowQualificationManifestFile(
                        directionalShadowQualificationPath,
                        out string detail);
                startupLog?.StepSucceeded(
                    directionalShadowQualificationStep,
                    accepted
                        ? $"accepted:{detail}"
                        : $"rejected:{detail};experimental-ray-and-csm-fallback-retained");
            }

            const string runtimeEvidenceStep =
                "AdvancedGI.RuntimeEvidenceBundle";
            if (options.AdvancedGiRuntimeEvidenceBundlePath is
                { } runtimeEvidencePath)
            {
                startupLog?.StepStarted(
                    runtimeEvidenceStep,
                    runtimeEvidencePath);
                bool accepted = renderer
                    .TryConfigureAdvancedGiRuntimeEvidenceBundleFile(
                        runtimeEvidencePath,
                        out string detail);
                startupLog?.StepSucceeded(
                    runtimeEvidenceStep,
                    accepted
                        ? $"accepted:{detail}"
                        : $"rejected:{detail};C4-C5-canonical-fallback-retained");
            }

            const string candidateStep = "AdvancedGI.CandidateProfile";
            if (options.AdvancedGiCandidateProfilePath is { } candidatePath)
            {
                startupLog?.StepStarted(candidateStep, candidatePath);
                bool accepted = renderer
                    .TryConfigureAdvancedGiCandidateProfileFile(
                        candidatePath,
                        out string detail);
                startupLog?.StepSucceeded(
                    candidateStep,
                    accepted
                        ? $"loaded:{detail};runtime-binding-pending"
                        : $"rejected:{detail};candidate-modes-retain-fallback");
            }

            options.ConfigureAdvancedGiEvidence?.Invoke(renderer);
        }
    }
}
