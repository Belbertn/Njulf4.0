using Njulf.Assets;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleScenePreparationWorkTests
{
    [TestCase("complete")]
    [TestCase("cancel-before-start")]
    [TestCase("cancel")]
    [TestCase("shutdown")]
    [TestCase("failure")]
    [TestCase("superseded")]
    public void WorkerRequestsAssembleAndReleaseOnlyOnThePumpThread(string outcome)
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        int references = 2;
        void CheckThread() => Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(ownerThread));
        void Retain() { CheckThread(); references++; }
        void Release() { CheckThread(); references--; }
        object owner = new();
        using var mesh = new VulkanMesh(owner, new MeshHandle(1, 1), default,
            _ => Retain(), _ => Release(), CheckThread);
        using var material = new VulkanMaterial(owner, new MaterialHandle(1, 1), "template",
            _ => Retain(), _ => Release(), CheckThread);
        using var template = new Model();
        template.Add(new RenderObject(mesh, material));
        int baselineReferences = references;
        using var scene = new Scene();
        using var dispatcher = new RenderThreadContentUploadDispatcher();
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("assembly failed");
        int publications = 0;

        IEnumerable<object?> Build()
        {
            for (int i = 0; i < 520; i++)
            {
                CheckThread();
                if (outcome == "failure" && i == 513)
                    throw failure;
                scene.Add(template.CreateRenderObjectInstance(0));
                yield return null;
            }
        }

        var work = new SampleScenePreparationWork(scene, Build(), () =>
        {
            CheckThread();
            if (outcome == "superseded")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            publications++;
        });
        Task<bool>? preparation = null;
        Task.Run(() =>
        {
            if (outcome == "cancel-before-start")
                cancellation.Cancel();
            preparation = dispatcher.DispatchAsync(work, cancellation.Token);
        }).GetAwaiter().GetResult();
        Assert.That(scene.RenderObjects, Is.Empty);

        dispatcher.ProcessFrame(TimeSpan.Zero, maximumCallbacks: 1);
        if (outcome != "cancel-before-start")
        {
            Assert.That(scene.RenderObjects.Count, Is.EqualTo(512));
            Assert.That(preparation!.IsCompleted, Is.False);
            Assert.That(publications, Is.Zero);
        }
        if (outcome == "cancel")
            Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
        if (outcome == "shutdown")
            dispatcher.BeginShutdown();

        for (int i = 0; i < 16 && !preparation!.IsCompleted; i++)
            dispatcher.ProcessFrame(TimeSpan.Zero, maximumCallbacks: 1);
        Assert.That(preparation!.IsCompleted, Is.True);
        Assert.That(dispatcher.PendingCount, Is.Zero);
        if (outcome == "complete")
        {
            Assert.That(preparation.GetAwaiter().GetResult(), Is.True);
            Assert.That(publications, Is.EqualTo(1));
            Assert.That(scene.RenderObjects.Count, Is.EqualTo(520));
            Assert.That(references, Is.EqualTo(baselineReferences + 1040));
            scene.Dispose();
        }
        else
        {
            if (outcome == "failure")
                Assert.That(preparation.Exception!.InnerException, Is.SameAs(failure));
            else
                Assert.That(preparation.IsCanceled, Is.True);
            Assert.That(publications, Is.Zero);
            Assert.That(scene.RenderObjects, Is.Empty);
        }
        Assert.That(references, Is.EqualTo(baselineReferences));
    }
}
