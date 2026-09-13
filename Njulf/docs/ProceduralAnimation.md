# Procedural animation

`AnimationPose.ResetToBindPose(skeleton)` restores authored local bind matrices and immediately
builds global matrices. Parents may appear after children; invalid parent indices and cycles
are rejected. The skeleton joint count must match the pose. Authored `LocalBindPose` and
`LocalBindTransform` should describe the same transform; the matrix preserves the exact bind data.

For standalone poses, edit local TRS values and explicitly rebuild once after the batch:

```csharp
var pose = new AnimationPose(skeleton.Joints.Count);
pose.ResetToBindPose(skeleton);
AnimationTransform local = pose.GetLocalTransform(jointIndex);
pose.SetLocalTransform(jointIndex,
    new AnimationTransform(local.Translation, rotation, local.Scale));
pose.BuildGlobalMatrices(skeleton);
```

For a rendered animator, use `EditPose` after animation evaluation. It also updates skin
matrices and `PoseRevision`, which rendering uses to detect changed animation:

```csharp
animator.EditPose(pose =>
{
    var local = pose.GetLocalTransform(jointIndex);
    pose.SetLocalTransform(jointIndex,
        new AnimationTransform(local.Translation, rotation, local.Scale));
});
```

Edits last until the next animation evaluation (`Update` while playing, `Play`, `Seek`, or
`Stop`). Reapply procedural offsets after each evaluation when needed. Do not edit
`CurrentPose` directly and expect skin matrices to update. If the callback throws, partial
edits are published before the exception propagates, keeping matrices and revision consistent.

`Play` and `CrossFade` check every channel before changing playback state. Cubic spline
interpolation remains available in imported diagnostics but is not supported for playback.
The error identifies the clip, channel, joint, and path, and asks for export/resampling to
linear or step interpolation. Clip/sampler collections should remain unchanged during playback.
