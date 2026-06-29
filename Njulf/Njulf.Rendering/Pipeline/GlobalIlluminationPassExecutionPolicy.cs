using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    internal static class GlobalIlluminationPassExecutionPolicy
    {
        public const uint ForwardDebugViewNone = 0u;
        public const uint ForwardDebugViewGlobalIlluminationFirst = 80u;
        public const uint ForwardDebugViewGlobalIlluminationFinalIndirect = 80u;
        public const uint ForwardDebugViewGlobalIlluminationLast = 102u;

        public static bool IsDdgiDebugView(GlobalIlluminationDebugView view)
        {
            return view is GlobalIlluminationDebugView.DdgiIrradiance
                or GlobalIlluminationDebugView.DdgiVisibility
                or GlobalIlluminationDebugView.DdgiProbeIndex
                or GlobalIlluminationDebugView.DdgiProbeState
                or GlobalIlluminationDebugView.DdgiProbeRelocation
                or GlobalIlluminationDebugView.DdgiLeakClamp
                or GlobalIlluminationDebugView.DdgiCoverage
                or GlobalIlluminationDebugView.DdgiCascadeSelection
                or GlobalIlluminationDebugView.DdgiCascadeBlendWeight
                or GlobalIlluminationDebugView.DdgiUpdateReasons
                or GlobalIlluminationDebugView.DdgiRayBudget
                or GlobalIlluminationDebugView.DdgiGatherLocalVolume
                or GlobalIlluminationDebugView.DdgiGatherClipmap
                or GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight
                or GlobalIlluminationDebugView.DdgiGatherFallback
                or GlobalIlluminationDebugView.DdgiRawDiffuse
                or GlobalIlluminationDebugView.DdgiSuppressionMask;
        }

        public static bool IsSsgiDebugView(GlobalIlluminationDebugView view)
        {
            return view is GlobalIlluminationDebugView.SsgiRaw
                or GlobalIlluminationDebugView.SsgiFiltered
                or GlobalIlluminationDebugView.SsgiHistory
                or GlobalIlluminationDebugView.SsgiRayHitMask
                or GlobalIlluminationDebugView.SsgiHistoryRejection;
        }

        public static bool ShouldRunSsgiProducer(GlobalIlluminationSettings gi)
        {
            if (gi == null)
                throw new ArgumentNullException(nameof(gi));

            return gi.EffectiveUseSsgi;
        }

        public static bool ShouldRunSsgiProducer(GlobalIlluminationSettings gi, uint forwardDebugViewMode)
        {
            return ShouldRunSsgiProducer(gi) && IsForwardDebugViewCompatibleWithSsgiProducer(forwardDebugViewMode);
        }

        public static bool ShouldCompositeSsgi(GlobalIlluminationSettings gi)
        {
            if (gi == null)
                throw new ArgumentNullException(nameof(gi));

            return gi.EffectiveUseSsgi &&
                gi.DebugView is GlobalIlluminationDebugView.None or GlobalIlluminationDebugView.FinalIndirect;
        }

        public static bool ShouldCompositeSsgi(GlobalIlluminationSettings gi, uint forwardDebugViewMode)
        {
            return ShouldCompositeSsgi(gi) && IsForwardDebugViewCompatibleWithSsgiComposite(forwardDebugViewMode);
        }

        public static bool IsForwardDebugViewCompatibleWithSsgiComposite(uint forwardDebugViewMode)
        {
            return forwardDebugViewMode is ForwardDebugViewNone or ForwardDebugViewGlobalIlluminationFinalIndirect;
        }

        public static bool IsForwardDebugViewCompatibleWithSsgiProducer(uint forwardDebugViewMode)
        {
            return forwardDebugViewMode == ForwardDebugViewNone ||
                (forwardDebugViewMode >= ForwardDebugViewGlobalIlluminationFirst &&
                    forwardDebugViewMode <= ForwardDebugViewGlobalIlluminationLast);
        }
    }
}
