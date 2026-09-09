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

using Njulf.Core.Math;

namespace Njulf.Rendering;

    /// <summary>Vulkan renderer startup configuration supplied to Game.ConfigureRendering or AddRendering.</summary>
    /// <remarks>Configure before renderer initialization on the game thread. Validation and optional device features
    /// honor environment defaults unless explicitly overridden. Use GraphicsDevice.Settings for runtime quality changes.</remarks>
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
        private string? _directionalShadowQualificationManifestPath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_DIRECTIONAL_SHADOW_QUALIFICATION_MANIFEST"));

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
        /// Optional artifact-pinned directional-shadow promotion manifest.
        /// Missing or rejected evidence leaves explicit ray modes Experimental
        /// and keeps CSM temporal Auto dormant.
        /// </summary>
        public string? DirectionalShadowQualificationManifestPath
        {
            get => _directionalShadowQualificationManifestPath;
            set => _directionalShadowQualificationManifestPath =
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
#if DEBUG || NJULF_DEVELOPMENT
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
