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
    public sealed class RenderingOptions
    {
        private RendererValidationSettings _validationSettings = RendererValidationSettings.FromEnvironment();
        private VulkanOptionalDeviceFeatures _optionalDeviceFeatures =
            VulkanOptionalDeviceFeatures.FromEnvironment();
        private bool _optionalDeviceFeaturesExplicitlyConfigured =
            Environment.GetEnvironmentVariable(
                "NJULF_ENABLE_EXT_OPACITY_MICROMAP") is not null;
        private string? _advancedGiPrerequisiteManifestPath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_PREREQUISITE_MANIFEST"));
        private string? _advancedGiQualificationManifestPath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_QUALIFICATION_MANIFEST"));
        private string? _advancedGiRuntimeEvidenceBundlePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_RUNTIME_EVIDENCE_BUNDLE"));
        private string? _advancedGiStartupProfilePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_STARTUP_PROFILE"));

        public bool EnableValidation
        {
            get => _validationSettings.EnableValidation;
            set => _validationSettings = _validationSettings with
            {
                Mode = value ? RendererValidationMode.Standard : RendererValidationMode.Off
            };
        }

        public RendererValidationSettings ValidationSettings
        {
            get => _validationSettings;
            set => _validationSettings = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Logical-device feature requests. Unless explicitly overridden, C1's
        /// device chain follows the pre-initialization opacity-micromap mode.
        /// Physical-device advertisement alone never enables an extension.
        /// </summary>
        public VulkanOptionalDeviceFeatures OptionalDeviceFeatures
        {
            get => _optionalDeviceFeaturesExplicitlyConfigured
                ? _optionalDeviceFeatures
                : _optionalDeviceFeatures with
                {
                    EnableExtOpacityMicromap = ShouldRequestExtOpacityMicromap(
                        InitialSettings.GlobalIllumination)
                };
            set
            {
                _optionalDeviceFeatures = value;
                _optionalDeviceFeaturesExplicitlyConfigured = true;
            }
        }

        public bool EnableExtOpacityMicromap
        {
            get => OptionalDeviceFeatures.EnableExtOpacityMicromap;
            set
            {
                _optionalDeviceFeatures = _optionalDeviceFeatures with
                {
                    EnableExtOpacityMicromap = value
                };
                _optionalDeviceFeaturesExplicitlyConfigured = true;
            }
        }

        internal static bool ShouldRequestExtOpacityMicromap(
            GlobalIlluminationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return settings.DdgiOpacityMicromapMode is
                DdgiOpacityMicromapMode.ExtFourStateExperiment or
                DdgiOpacityMicromapMode.AutoQualified;
        }

        /// <summary>
        /// Settings consumed while the renderer is constructed, before its
        /// immutable render-graph and optional-device inventory are selected.
        /// Applications must place startup-only advanced-GI mode changes here;
        /// mutating the live renderer after <c>Initialize</c> cannot retroactively
        /// create an omitted graph branch.
        /// </summary>
        public RenderSettings InitialSettings { get; internal set; } = new();

        /// <summary>
        /// Optional cold-start profile. It is resolved immediately after the
        /// application configuration callback, before Vulkan optional-device
        /// features are requested.
        /// </summary>
        public string? AdvancedGiStartupProfilePath
        {
            get => _advancedGiStartupProfilePath;
            set => _advancedGiStartupProfilePath =
                RendererValidationSettings.NormalizeOptionalPath(value);
        }

        public AdvancedGiRuntimeContentBinding AdvancedGiContentBinding
        {
            get;
            set;
        } = AdvancedGiRuntimeContentBinding.Empty;

        public string AdvancedGiStartupProfileStatus { get; private set; } =
            "not-configured";

        internal void ResolveAdvancedGiStartupProfile()
        {
            if (_advancedGiStartupProfilePath is not { } path)
                return;
            if (!AdvancedGiStartupProfileCodec.TryLoad(
                    path,
                    out AdvancedGiStartupProfile? profile,
                    out string detail) || profile is null)
            {
                // A named startup transaction is authoritative. Never combine
                // a rejected/torn profile with ambient manifests or partially
                // configured modes from a different launch mechanism.
                AdvancedGiContentBinding =
                    AdvancedGiRuntimeContentBinding.Empty;
                _advancedGiPrerequisiteManifestPath = null;
                _advancedGiQualificationManifestPath = null;
                _advancedGiRuntimeEvidenceBundlePath = null;
                AdvancedGiCandidateProfilePath = null;
                GlobalIlluminationSettings gi =
                    InitialSettings.GlobalIllumination;
                gi.SimpleDdgiReceiverFeedbackMode =
                    SimpleDdgiReceiverFeedbackMode.Off;
                gi.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Off;
                gi.SimpleDdgiDirectionalGuidingMode =
                    SimpleDdgiDirectionalGuidingMode.Off;
                gi.GiCausticMode = GiCausticMode.Off;
                gi.SimpleDdgiNearFieldResidualMode =
                    SimpleDdgiNearFieldResidualMode.Off;
                gi.SimpleDdgiReceiverFeedbackQualificationId = string.Empty;
                gi.DdgiOpacityMicromapQualificationId = string.Empty;
                gi.SimpleDdgiDirectionalGuidingQualificationId = string.Empty;
                gi.GiCausticQualificationId = string.Empty;
                gi.SimpleDdgiNearFieldResidualQualificationId = string.Empty;
                AdvancedGiStartupProfileStatus = "rejected:" + detail;
                return;
            }

            InitialSettings = profile.Settings;
            AdvancedGiContentBinding = profile.ContentBinding;
            _advancedGiPrerequisiteManifestPath =
                profile.PrerequisiteManifestPath;
            _advancedGiQualificationManifestPath =
                profile.QualificationManifestPath;
            _advancedGiRuntimeEvidenceBundlePath =
                profile.RuntimeEvidenceBundlePath;
            AdvancedGiCandidateProfilePath = profile.CandidateProfilePath;
            AdvancedGiStartupProfileStatus = "accepted:valid";
        }

        /// <summary>
        /// Optional Phase-0 frozen-contract manifest. Invalid input is rejected
        /// fail-closed and canonical GI remains available.
        /// </summary>
        public string? AdvancedGiPrerequisiteManifestPath
        {
            get => _advancedGiPrerequisiteManifestPath;
            set => _advancedGiPrerequisiteManifestPath =
                RendererValidationSettings.NormalizeOptionalPath(value);
        }

        /// <summary>
        /// Optional authenticated per-device promotion manifest. Invalid,
        /// incomplete, stale, or tampered input is rejected fail-closed.
        /// </summary>
        public string? AdvancedGiQualificationManifestPath
        {
            get => _advancedGiQualificationManifestPath;
            set => _advancedGiQualificationManifestPath =
                RendererValidationSettings.NormalizeOptionalPath(value);
        }

        /// <summary>
        /// Optional exact C4/C5 scene/layout evidence bundle. The common
        /// qualification manifest still gates AutoQualified; this file adds
        /// the feature-specific configuration and source identity required to
        /// create those immutable graph variants.
        /// </summary>
        public string? AdvancedGiRuntimeEvidenceBundlePath
        {
            get => _advancedGiRuntimeEvidenceBundlePath;
            set => _advancedGiRuntimeEvidenceBundlePath =
                RendererValidationSettings.NormalizeOptionalPath(value);
        }

        /// <summary>
        /// Optional bounded candidate authorization used only by explicit C4
        /// and C5 experiment modes. AutoQualified never consumes this input.
        /// </summary>
        public string? AdvancedGiCandidateProfilePath { get; set; }

        /// <summary>
        /// Optional strongly typed configuration hook for C4/C5 evidence whose
        /// exact scene/layout binding is application-owned. It runs after the
        /// common manifests are loaded and before renderer initialization.
        /// </summary>
        public Action<VulkanRenderer>? ConfigureAdvancedGiEvidence { get; set; }

        public static bool DefaultEnableValidation { get; } =
#if DEBUG
            true;
#else
            false;
#endif

        private static readonly TextureBudgetProfile DefaultTextureBudgetProfile = ReadTextureBudgetProfile();
        private uint _maxImportedTextureDimension = ReadMaxImportedTextureDimension(DefaultTextureBudgetProfile);

        public TextureBudgetProfile TextureBudgetProfile { get; private set; } = DefaultTextureBudgetProfile;
        public uint MaxImportedTextureDimension
        {
            get => _maxImportedTextureDimension;
            set
            {
                TextureBudgetProfile = TextureBudgetProfile.Custom;
                _maxImportedTextureDimension = value;
            }
        }
        public ulong StagingBufferSize { get; set; } = ReadStagingBufferSize();

        public void ApplyTextureBudgetProfile(TextureBudgetProfile profile)
        {
            TextureBudgetProfile = profile;
            _maxImportedTextureDimension = GetProfileMaxDimension(profile);
        }

        public void SetCustomMaxImportedTextureDimension(uint maxDimension)
        {
            TextureBudgetProfile = TextureBudgetProfile.Custom;
            _maxImportedTextureDimension = maxDimension;
        }

        private static TextureBudgetProfile ReadTextureBudgetProfile()
        {
            string? explicitMax = Environment.GetEnvironmentVariable("NJULF_MAX_IMPORTED_TEXTURE_SIZE");
            if (uint.TryParse(explicitMax, out _))
                return TextureBudgetProfile.Custom;

            string? value = Environment.GetEnvironmentVariable("NJULF_TEXTURE_BUDGET_PROFILE");
            return Enum.TryParse(value, ignoreCase: true, out TextureBudgetProfile parsed)
                ? parsed
                : TextureBudgetProfile.Development;
        }

        private static uint ReadMaxImportedTextureDimension(TextureBudgetProfile profile)
        {
            string? value = Environment.GetEnvironmentVariable("NJULF_MAX_IMPORTED_TEXTURE_SIZE");
            return uint.TryParse(value, out uint parsed) ? parsed : GetProfileMaxDimension(profile);
        }

        private static uint GetProfileMaxDimension(TextureBudgetProfile profile)
        {
            return profile switch
            {
                TextureBudgetProfile.HighQuality => 2048u,
                TextureBudgetProfile.Cinematic => 4096u,
                _ => 1024u
            };
        }

        private static ulong ReadStagingBufferSize()
        {
            string? value = Environment.GetEnvironmentVariable("NJULF_STAGING_BUFFER_SIZE_BYTES");
            return ulong.TryParse(value, out ulong parsed) ? parsed : StagingRing.DefaultStagingBufferSize;
        }
    }

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
            services.TryAddSingleton<IModelRenderUploadService, ModelRenderUploadService>();
            services.TryAddSingleton<LightManager>();
            services.TryAddSingleton<SceneDataBuilder>();
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
                    initialSettings: renderingOptions.InitialSettings);

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
