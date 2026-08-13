using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Pure validity state for directional static shadow-cache layers.  The
    /// pass owns recording/transition commands; this tracker owns the
    /// proof that a layer belongs to the current signature and image pair.
    /// Keeping the policy independent of Vulkan makes cache transitions
    /// deterministic and directly testable.
    /// </summary>
    internal sealed class DirectionalShadowCacheStateTracker
    {
        private uint _validMask;
        private uint _recordedRefreshMask;
        private uint _resourceGeneration;
        private uint _signatureMask;
        private readonly ulong[] _signatures = new ulong[ShadowSettings.MaxDirectionalCascades];

        public uint ValidMask => _validMask;
        /// <summary>
        /// Layers refreshed into the current command buffer but not yet made
        /// durable by a successful graphics submission.
        /// </summary>
        public uint RecordedRefreshMask => _recordedRefreshMask;
        public uint ResourceGeneration => _resourceGeneration;
        public ulong Signature => _signatures[0];
        public bool HasSignature => _signatureMask != 0u;

        public ulong GetSignature(int cascade)
        {
            uint bit = GetCascadeBit(cascade);
            return (_signatureMask & bit) != 0u ? _signatures[cascade] : 0UL;
        }

        public bool IsDirty(
            uint activeMask,
            ulong requiredSignature,
            uint resourceGeneration,
            bool resourcesDefined,
            bool forceRefresh)
        {
            Span<ulong> signatures = stackalloc ulong[ShadowSettings.MaxDirectionalCascades];
            signatures.Fill(requiredSignature);
            return GetDirtyMask(
                activeMask,
                signatures,
                resourceGeneration,
                resourcesDefined,
                forceRefresh) != 0u;
        }

        public uint GetDirtyMask(
            uint activeMask,
            ReadOnlySpan<ulong> requiredSignatures,
            uint resourceGeneration,
            bool resourcesDefined,
            bool forceRefresh)
        {
            activeMask &= (1u << ShadowSettings.MaxDirectionalCascades) - 1u;
            if (activeMask == 0u)
            {
                Invalidate();
                return 0u;
            }
            if (requiredSignatures.Length < ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentException("One signature per directional cascade is required.", nameof(requiredSignatures));

            if (!resourcesDefined)
            {
                Invalidate();
                return activeMask;
            }
            if (forceRefresh)
            {
                InvalidateMask(activeMask);
                return activeMask;
            }
            if (_signatureMask != 0u && _resourceGeneration != resourceGeneration)
                Invalidate();

            uint dirtyMask = 0u;
            for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
            {
                uint bit = 1u << cascade;
                if ((activeMask & bit) == 0u)
                    continue;

                bool signatureMatches = (_signatureMask & bit) != 0u &&
                    _signatures[cascade] == requiredSignatures[cascade];
                if (!signatureMatches)
                {
                    _validMask &= ~bit;
                    _recordedRefreshMask &= ~bit;
                    _signatureMask &= ~bit;
                    _signatures[cascade] = 0UL;
                    dirtyMask |= bit;
                    continue;
                }

                if ((_validMask & bit) == 0u)
                    dirtyMask |= bit;
            }

            return dirtyMask;
        }

        public void BeginRefresh(uint activeMask)
        {
            _validMask &= ~activeMask;
            _recordedRefreshMask &= ~activeMask;
        }

        public void RecordRefresh(uint refreshedMask, ulong signature, uint resourceGeneration)
        {
            Span<ulong> signatures = stackalloc ulong[ShadowSettings.MaxDirectionalCascades];
            signatures.Fill(signature);
            RecordRefresh(refreshedMask, signatures, resourceGeneration);
        }

        public void RecordRefresh(
            uint refreshedMask,
            ReadOnlySpan<ulong> signatures,
            uint resourceGeneration)
        {
            if (signatures.Length < ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentException("One signature per directional cascade is required.", nameof(signatures));
            if (_signatureMask != 0u && _resourceGeneration != resourceGeneration)
                Invalidate();

            // Recording is sufficient to copy a freshly cleared/rendered
            // static layer into this command buffer's working map. It is not
            // sufficient to reuse that layer in a later frame: submission may
            // still fail or the command buffer may be discarded.
            _recordedRefreshMask |= refreshedMask;
            for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
            {
                uint bit = 1u << cascade;
                if ((refreshedMask & bit) == 0u)
                    continue;
                _signatures[cascade] = signatures[cascade];
                _signatureMask |= bit;
            }
            _resourceGeneration = resourceGeneration;
        }

        /// <summary>
        /// Promotes successfully recorded layers after their owning graphics
        /// submission is accepted. Graphics-queue ordering then makes the
        /// cache contents available to later frame submissions; resource
        /// replacement remains protected by the generation comparison.
        /// </summary>
        public void ConfirmRecordedRefreshSubmission()
        {
            _validMask |= _recordedRefreshMask;
            _recordedRefreshMask = 0u;
        }

        public uint GetReusableMask(uint activeMask) => activeMask & _validMask;

        /// <summary>
        /// Returns layers usable by the current command buffer. This permits
        /// a newly recorded clear/render to be copied into the working map
        /// without prematurely granting cross-frame reuse.
        /// </summary>
        public uint GetCurrentSubmissionCopyMask(uint activeMask)
        {
            uint availableMask = _validMask | _recordedRefreshMask;
            return activeMask & availableMask;
        }

        public DirectionalShadowCacheLayerState GetLayerState(int cascade, uint activeMask, uint refreshMask)
        {
            uint bit = GetCascadeBit(cascade);
            if ((activeMask & bit) == 0u)
                return DirectionalShadowCacheLayerState.Invalid;
            if ((_recordedRefreshMask & bit) != 0u || (refreshMask & bit) != 0u)
                return DirectionalShadowCacheLayerState.RefreshRecorded;

            return (_validMask & bit) != 0u
                ? DirectionalShadowCacheLayerState.Valid
                : DirectionalShadowCacheLayerState.Invalid;
        }

        public void Invalidate()
        {
            _validMask = 0u;
            _recordedRefreshMask = 0u;
            _signatureMask = 0u;
            Array.Clear(_signatures, 0, _signatures.Length);
            _resourceGeneration = 0u;
        }

        private void InvalidateMask(uint mask)
        {
            _validMask &= ~mask;
            _recordedRefreshMask &= ~mask;
            _signatureMask &= ~mask;
            for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
            {
                uint bit = 1u << cascade;
                if ((mask & bit) != 0u)
                    _signatures[cascade] = 0UL;
            }
            if (_signatureMask == 0u)
                _resourceGeneration = 0u;
        }

        private static uint GetCascadeBit(int cascade)
        {
            if (cascade < 0 || cascade >= ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentOutOfRangeException(nameof(cascade));
            return 1u << cascade;
        }
    }
}
