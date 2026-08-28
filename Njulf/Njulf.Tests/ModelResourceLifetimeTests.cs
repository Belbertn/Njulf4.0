using Njulf.Core.Scene;
using Njulf.Core.Interfaces;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelResourceLifetimeTests
{
    [Test]
    public void TemplateAndTwoInstances_ReleaseExactlyOnceInEveryDisposalOrder()
    {
        foreach (int[] order in PermutationsOfThree())
        {
            var tracker = new ResourceTracker();
            Model template = CreateTemplate(tracker);
            Model first = template.CreateInstance();
            Model second = template.CreateInstance();
            Model[] models = [template, first, second];

            Assert.Multiple(() =>
            {
                Assert.That(tracker.Count("mesh"), Is.EqualTo(3));
                Assert.That(
                    tracker.Count("material"),
                    Is.EqualTo(3));
            });

            foreach (int index in order)
                models[index].Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(tracker.Count("mesh"), Is.Zero);
                Assert.That(
                    tracker.Count("material"),
                    Is.Zero);
                Assert.That(tracker.MinimumCount, Is.Zero);
            });
        }
    }

    [Test]
    public void SingleRenderObjectInstance_RetainsOnlyRequestedObject()
    {
        var tracker = new ResourceTracker();
        var template = new Model();
        template.Add(CreateOwnedObject(
            tracker,
            "mesh-a",
            "material-a"));
        template.Add(CreateOwnedObject(
            tracker,
            "mesh-b",
            "material-b"));

        RenderObject instance =
            template.CreateRenderObjectInstance(1);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh-a"), Is.EqualTo(1));
            Assert.That(tracker.Count("material-a"), Is.EqualTo(1));
            Assert.That(tracker.Count("mesh-b"), Is.EqualTo(2));
            Assert.That(tracker.Count("material-b"), Is.EqualTo(2));
        });

        instance.Dispose();
        template.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh-a"), Is.Zero);
            Assert.That(tracker.Count("material-a"), Is.Zero);
            Assert.That(tracker.Count("mesh-b"), Is.Zero);
            Assert.That(tracker.Count("material-b"), Is.Zero);
            Assert.That(tracker.MinimumCount, Is.Zero);
        });
    }

    [Test]
    public void CopyOnWriteTransfer_IsolatedFromTemplateAndSibling()
    {
        var tracker = new ResourceTracker();
        Model template = CreateTemplate(tracker);
        Model first = template.CreateInstance();
        Model second = template.CreateInstance();
        RenderObject edited = first.RenderObjects[0];

        tracker.Transfer("material", "editable-material");
        edited.AdoptTransferredMaterial(
            "editable-material");

        Assert.Multiple(() =>
        {
            Assert.That(
                template.RenderObjects[0].Material,
                Is.EqualTo("material"));
            Assert.That(
                second.RenderObjects[0].Material,
                Is.EqualTo("material"));
            Assert.That(
                edited.Material,
                Is.EqualTo("editable-material"));
            Assert.That(
                tracker.Count("material"),
                Is.EqualTo(2));
            Assert.That(
                tracker.Count("editable-material"),
                Is.EqualTo(1));
        });

        first.Dispose();
        template.Dispose();
        second.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(
                tracker.Count("material"),
                Is.Zero);
            Assert.That(
                tracker.Count("editable-material"),
                Is.Zero);
        });
    }

    [Test]
    public void ResourceReplacement_RetainsNewBeforeReleasingOld()
    {
        var tracker = new ResourceTracker();
        var renderObject = new RenderObject(
            "mesh",
            "material");
        tracker.Seed("mesh");
        tracker.Seed("material");
        renderObject.AttachResourceLifetime(
            tracker.Retain,
            tracker.Release,
            tracker.Retain,
            tracker.Release,
            retainCurrentResources: false);

        renderObject.Mesh = "next-mesh";
        renderObject.Material = "next-material";

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh"), Is.Zero);
            Assert.That(
                tracker.Count("material"),
                Is.Zero);
            Assert.That(
                tracker.Count("next-mesh"),
                Is.EqualTo(1));
            Assert.That(
                tracker.Count("next-material"),
                Is.EqualTo(1));
        });

        renderObject.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(
                tracker.Count("next-mesh"),
                Is.Zero);
            Assert.That(
                tracker.Count("next-material"),
                Is.Zero);
        });
    }

    [Test]
    public void DisposeFailure_KeepsOnlyFailedReleaseForRetry()
    {
        var tracker = new ResourceTracker();
        var renderObject = new RenderObject(
            "mesh",
            "material");
        tracker.Seed("mesh");
        tracker.Seed("material");
        renderObject.AttachResourceLifetime(
            tracker.Retain,
            tracker.Release,
            tracker.Retain,
            tracker.Release,
            retainCurrentResources: false);
        tracker.FailNextRelease("mesh");

        Assert.That(
            () => renderObject.Dispose(),
            Throws.TypeOf<AggregateException>());
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh"), Is.EqualTo(1));
            Assert.That(
                tracker.Count("material"),
                Is.Zero);
            Assert.That(
                tracker.ReleaseCalls("mesh"),
                Is.EqualTo(1));
            Assert.That(
                tracker.ReleaseCalls("material"),
                Is.EqualTo(1));
        });

        renderObject.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh"), Is.Zero);
            Assert.That(
                tracker.ReleaseCalls("mesh"),
                Is.EqualTo(2));
            Assert.That(
                tracker.ReleaseCalls("material"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public void InstanceRetainAndRollbackFailure_IsRecoveredByModelRollback()
    {
        var tracker = new ResourceTracker();
        Model template = CreateTemplate(tracker);
        tracker.FailNextRetain("material");
        tracker.FailNextRelease("mesh");

        Assert.That(
            () => template.CreateInstance(),
            Throws.TypeOf<AggregateException>());
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh"), Is.EqualTo(1));
            Assert.That(
                tracker.Count("material"),
                Is.EqualTo(1));
        });

        template.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh"), Is.Zero);
            Assert.That(
                tracker.Count("material"),
                Is.Zero);
        });
    }

    [Test]
    public void RemoveAndClear_DisposeOwnedObjects_AndMutationAfterDisposeFails()
    {
        var tracker = new ResourceTracker();
        var model = new Model();
        RenderObject first =
            CreateOwnedObject(tracker, "mesh-a", "material-a");
        RenderObject second =
            CreateOwnedObject(tracker, "mesh-b", "material-b");
        model.Add(first);
        model.Add(second);

        model.Remove(first);
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count("mesh-a"), Is.Zero);
            Assert.That(
                tracker.Count("material-a"),
                Is.Zero);
            Assert.That(model.RenderObjects, Has.Count.EqualTo(1));
        });

        model.Clear();
        Assert.That(model.RenderObjects, Is.Empty);
        model.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(
                () => model.Add(new RenderObject()),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => model.AddDisposeAction(
                    static () => { }),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void AdoptTransferredMaterial_RequiresAttachedOwnedReference()
    {
        var unattached = new RenderObject(
            "mesh",
            "material");
        Assert.That(
            () => unattached.AdoptTransferredMaterial("copy"),
            Throws.InvalidOperationException);
    }

    [Test]
    public void SceneRemoval_ReleasesOnlyAfterLastOwnedRoleIsRemoved()
    {
        var tracker = new ResourceTracker();
        RenderObject renderObject =
            CreateOwnedObject(
                tracker,
                "scene-mesh",
                "scene-material");
        var scene = new Scene();
        scene.Add(renderObject);
        scene.Add((IUpdateable)renderObject);

        scene.Remove(renderObject);
        Assert.Multiple(() =>
        {
            Assert.That(
                tracker.Count("scene-mesh"),
                Is.EqualTo(1));
            Assert.That(
                scene.Updateables,
                Has.Count.EqualTo(1));
        });

        scene.Remove((IUpdateable)renderObject);
        Assert.Multiple(() =>
        {
            Assert.That(
                tracker.Count("scene-mesh"),
                Is.Zero);
            Assert.That(
                tracker.Count("scene-material"),
                Is.Zero);
        });
        scene.Dispose();
    }

    [Test]
    public void SceneDisposeFailure_BlocksMutationAndRetriesOutstandingLease()
    {
        var tracker = new ResourceTracker();
        RenderObject renderObject =
            CreateOwnedObject(
                tracker,
                "retry-mesh",
                "retry-material");
        var scene = new Scene();
        scene.Add(renderObject);
        tracker.FailNextRelease("retry-mesh");

        Assert.That(
            () => scene.Dispose(),
            Throws.TypeOf<AggregateException>());
        Assert.Multiple(() =>
        {
            Assert.That(
                tracker.Count("retry-mesh"),
                Is.EqualTo(1));
            Assert.That(
                tracker.Count("retry-material"),
                Is.Zero);
            Assert.That(
                () => scene.Add(new RenderObject()),
                Throws.TypeOf<ObjectDisposedException>());
        });

        scene.Dispose();
        Assert.That(
            tracker.Count("retry-mesh"),
            Is.Zero);
    }

    private static Model CreateTemplate(
        ResourceTracker tracker)
    {
        var model = new Model();
        model.Add(
            CreateOwnedObject(
                tracker,
                "mesh",
                "material"));
        return model;
    }

    private static RenderObject CreateOwnedObject(
        ResourceTracker tracker,
        object mesh,
        object material)
    {
        tracker.Seed(mesh);
        tracker.Seed(material);
        var renderObject =
            new RenderObject(mesh, material);
        renderObject.AttachResourceLifetime(
            tracker.Retain,
            tracker.Release,
            tracker.Retain,
            tracker.Release,
            retainCurrentResources: false);
        return renderObject;
    }

    private static IEnumerable<int[]>
        PermutationsOfThree()
    {
        yield return [0, 1, 2];
        yield return [0, 2, 1];
        yield return [1, 0, 2];
        yield return [1, 2, 0];
        yield return [2, 0, 1];
        yield return [2, 1, 0];
    }

    private sealed class ResourceTracker
    {
        private readonly Dictionary<object, int> _counts =
            new();
        private readonly Dictionary<object, int>
            _releaseCalls = new();
        private readonly HashSet<object> _failRetains = new();
        private readonly HashSet<object> _failReleases = new();

        public int MinimumCount { get; private set; }

        public void Seed(object resource)
        {
            _counts.TryGetValue(resource, out int count);
            _counts[resource] = count + 1;
        }

        public int Count(object resource) =>
            _counts.TryGetValue(resource, out int count)
                ? count
                : 0;

        public int ReleaseCalls(object resource) =>
            _releaseCalls.TryGetValue(
                resource,
                out int count)
                ? count
                : 0;

        public void FailNextRetain(object resource) =>
            _failRetains.Add(resource);

        public void FailNextRelease(object resource) =>
            _failReleases.Add(resource);

        public void Retain(object resource)
        {
            if (_failRetains.Remove(resource))
            {
                throw new InvalidOperationException(
                    $"Injected retain failure for {resource}.");
            }

            Seed(resource);
        }

        public void Release(object resource)
        {
            _releaseCalls.TryGetValue(
                resource,
                out int calls);
            _releaseCalls[resource] = calls + 1;
            if (_failReleases.Remove(resource))
            {
                throw new InvalidOperationException(
                    $"Injected release failure for {resource}.");
            }

            int final = checked(Count(resource) - 1);
            MinimumCount = Math.Min(MinimumCount, final);
            if (final < 0)
            {
                throw new InvalidOperationException(
                    $"Double release for {resource}.");
            }
            _counts[resource] = final;
        }

        public void Transfer(
            object previous,
            object next)
        {
            Release(previous);
            Seed(next);
        }
    }
}
