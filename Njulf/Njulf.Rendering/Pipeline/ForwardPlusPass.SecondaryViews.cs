using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe partial class ForwardPlusPass
{
    private SecondaryViewRenderer? _secondaryViews;

    internal void ConfigureSecondaryViews(SceneDataBuilder scene, FoliageCullPass foliageCull,
        AutomaticPlanarReflectionManager planar)
    {
        if (_secondaryViews is not null) throw new InvalidOperationException("Secondary view renderer is already configured.");
        _secondaryViews = new SecondaryViewRenderer(_context, _bindlessHeap, _meshPipeline,
            _foliagePipeline, _foliageManager, foliageCull, _skyboxPipeline,
            _bufferManager ?? throw new InvalidOperationException("Capture buffers are unavailable."),
            scene, _settings, planar, this);
    }

    internal bool BeginSecondaryProbeFeedback(int frameIndex, SceneRenderingData scene,
        in ReflectionCaptureViewContext view)
    {
        PrepareReflectionReceiverFeedbackFace(frameIndex, scene, view);
        return _simpleDdgiReflectionFeedbackRequiredForCurrentView;
    }

    internal void EndSecondaryProbeFeedback(bool completed)
    {
        if (completed && _simpleDdgiReflectionFeedbackRequiredForCurrentView)
            _reflectionFeedbackFacesRecordedForCurrentBatch++;
        _simpleDdgiReflectionFeedbackRequiredForCurrentView = false;
        _reflectionFeedbackCubemapArrayLayer = 0;
    }
}
