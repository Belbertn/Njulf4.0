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
        private ulong _signature;
        private bool _hasSignature;

        public uint ValidMask => _validMask;
        /// <summary>
        /// Layers refreshed into the current command buffer but not yet made
        /// durable by a successful graphics submission.
        /// </summary>
        public uint RecordedRefreshMask => _recordedRefreshMask;
        public uint ResourceGeneration => _resourceGeneration;
        public ulong Signature => _signature;
        public bool HasSignature => _hasSignature;

        public bool IsDirty(
            uint activeMask,
            ulong requiredSignature,
            uint resourceGeneration,
            bool resourcesDefined,
            bool forceRefresh)
        {
            if (activeMask == 0u)
            {
                Invalidate();
                return false;
            }

            if (!resourcesDefined || forceRefresh)
            {
                Invalidate();
                return true;
            }

            if (!_hasSignature ||
                _resourceGeneration != resourceGeneration ||
                _signature != requiredSignature ||
                (_validMask & activeMask) != activeMask)
            {
                if (_resourceGeneration != resourceGeneration ||
                    _signature != requiredSignature)
                {
                    _validMask = 0u;
                    _recordedRefreshMask = 0u;
                }

                return true;
            }

            return false;
        }

        public void BeginRefresh(uint activeMask)
        {
            _validMask &= ~activeMask;
            _recordedRefreshMask &= ~activeMask;
        }

        public void RecordRefresh(uint refreshedMask, ulong signature, uint resourceGeneration)
        {
            // Recording is sufficient to copy a freshly cleared/rendered
            // static layer into this command buffer's working map. It is not
            // sufficient to reuse that layer in a later frame: submission may
            // still fail or the command buffer may be discarded.
            _recordedRefreshMask |= refreshedMask;
            _signature = signature;
            _resourceGeneration = resourceGeneration;
            _hasSignature = true;
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

        public uint GetReusableMask(uint activeMask) =>
            activeMask != 0u && (_validMask & activeMask) == activeMask
                ? activeMask
                : 0u;

        /// <summary>
        /// Returns layers usable by the current command buffer. This permits
        /// a newly recorded clear/render to be copied into the working map
        /// without prematurely granting cross-frame reuse.
        /// </summary>
        public uint GetCurrentSubmissionCopyMask(uint activeMask)
        {
            uint availableMask = _validMask | _recordedRefreshMask;
            return activeMask != 0u && (availableMask & activeMask) == activeMask
                ? activeMask
                : 0u;
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
            _hasSignature = false;
            _signature = 0UL;
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
