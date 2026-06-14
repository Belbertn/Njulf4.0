using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    internal static class RenderGraphDeclarationCompiler
    {
        public static RenderGraphCompilationDiagnostics Compile(
            IReadOnlyList<RenderGraphPassDesc> passes,
            IReadOnlyList<RenderGraphImageDesc> images,
            IReadOnlyList<RenderGraphBufferDesc> buffers,
            RenderGraphUsagePlan usage)
        {
            RenderGraphPassDesc[] sorted = TopologicalSort(passes);
            HashSet<string> culled = CullPasses(sorted);
            RenderGraphPassDesc[] compiled = sorted
                .Where(pass => !culled.Contains(pass.Name))
                .ToArray();

            IReadOnlyList<RenderGraphResourceLifetime> lifetimes = BuildLifetimes(compiled, images, buffers);
            ulong bytes = EstimateResourceBytes(lifetimes, images, buffers);
            int barrierCount = EstimateBarrierCount(compiled, culled);

            return new RenderGraphCompilationDiagnostics(
                compiled.Select(pass => pass.Name).ToArray(),
                culled.ToArray(),
                lifetimes,
                bytes,
                barrierCount);
        }

        private static RenderGraphPassDesc[] TopologicalSort(IReadOnlyList<RenderGraphPassDesc> passes)
        {
            var byName = passes.ToDictionary(pass => pass.Name, StringComparer.Ordinal);
            var remainingDependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (RenderGraphPassDesc pass in passes)
            {
                var dependencies = new HashSet<string>(pass.DependsOn, StringComparer.Ordinal);
                remainingDependencies[pass.Name] = dependencies;
                foreach (string dependency in dependencies)
                {
                    if (!byName.ContainsKey(dependency))
                        throw new InvalidOperationException($"Render graph pass '{pass.Name}' depends on unknown pass '{dependency}'.");
                    if (!dependents.TryGetValue(dependency, out List<string>? dependentList))
                    {
                        dependentList = new List<string>();
                        dependents.Add(dependency, dependentList);
                    }

                    dependentList.Add(pass.Name);
                }
            }

            var ready = new Queue<string>(passes
                .Where(pass => remainingDependencies[pass.Name].Count == 0)
                .Select(pass => pass.Name));
            var sorted = new List<RenderGraphPassDesc>(passes.Count);

            while (ready.Count > 0)
            {
                string passName = ready.Dequeue();
                sorted.Add(byName[passName]);

                if (!dependents.TryGetValue(passName, out List<string>? dependentList))
                    continue;

                foreach (string dependent in dependentList)
                {
                    remainingDependencies[dependent].Remove(passName);
                    if (remainingDependencies[dependent].Count == 0)
                        ready.Enqueue(dependent);
                }
            }

            if (sorted.Count != passes.Count)
            {
                string cycle = string.Join(", ", remainingDependencies
                    .Where(pair => pair.Value.Count > 0)
                    .Select(pair => $"{pair.Key} depends on {string.Join("/", pair.Value)}"));
                throw new InvalidOperationException($"Render graph dependency cycle detected: {cycle}");
            }

            return sorted.ToArray();
        }

        private static HashSet<string> CullPasses(IReadOnlyList<RenderGraphPassDesc> sorted)
        {
            var culled = new HashSet<string>(StringComparer.Ordinal);
            var neededResources = new HashSet<RenderGraphResourceHandle>();

            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                RenderGraphPassDesc pass = sorted[i];
                if (!pass.IsEnabled)
                {
                    culled.Add(pass.Name);
                    continue;
                }

                bool needed = pass.NeverCull || pass.HasExternalSideEffect || WritesNeededResource(pass, neededResources);
                if (!needed && HasOnlyTransientOutputs(pass))
                {
                    culled.Add(pass.Name);
                    continue;
                }

                foreach (RenderGraphResourceUse use in pass.Reads)
                    neededResources.Add(use.Handle);
                foreach (RenderGraphResourceUse use in pass.ReadWrites)
                    neededResources.Add(use.Handle);
            }

            return culled;
        }

        private static bool WritesNeededResource(RenderGraphPassDesc pass, HashSet<RenderGraphResourceHandle> neededResources)
        {
            foreach (RenderGraphResourceUse use in pass.Writes)
            {
                if (neededResources.Contains(use.Handle))
                    return true;
            }

            foreach (RenderGraphResourceUse use in pass.ReadWrites)
            {
                if (neededResources.Contains(use.Handle))
                    return true;
            }

            return false;
        }

        private static bool HasOnlyTransientOutputs(RenderGraphPassDesc pass)
        {
            return pass.Writes.Count > 0 || pass.ReadWrites.Count > 0;
        }

        private static IReadOnlyList<RenderGraphResourceLifetime> BuildLifetimes(
            IReadOnlyList<RenderGraphPassDesc> compiled,
            IReadOnlyList<RenderGraphImageDesc> images,
            IReadOnlyList<RenderGraphBufferDesc> buffers)
        {
            var firstUse = new Dictionary<RenderGraphResourceHandle, int>();
            var lastUse = new Dictionary<RenderGraphResourceHandle, int>();

            for (int i = 0; i < compiled.Count; i++)
            {
                RecordUses(compiled[i].Reads, i);
                RecordUses(compiled[i].Writes, i);
                RecordUses(compiled[i].ReadWrites, i);
            }

            var lifetimes = new List<RenderGraphResourceLifetime>(firstUse.Count);
            foreach (KeyValuePair<RenderGraphResourceHandle, int> entry in firstUse.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key.Index))
            {
                RenderGraphResourceHandle handle = entry.Key;
                lifetimes.Add(new RenderGraphResourceLifetime(
                    handle,
                    GetName(handle, images, buffers),
                    handle.Kind,
                    entry.Value,
                    lastUse[handle]));
            }

            return lifetimes;

            void RecordUses(IReadOnlyList<RenderGraphResourceUse> uses, int passIndex)
            {
                foreach (RenderGraphResourceUse use in uses)
                {
                    firstUse.TryAdd(use.Handle, passIndex);
                    lastUse[use.Handle] = passIndex;
                }
            }
        }

        private static ulong EstimateResourceBytes(
            IReadOnlyList<RenderGraphResourceLifetime> lifetimes,
            IReadOnlyList<RenderGraphImageDesc> images,
            IReadOnlyList<RenderGraphBufferDesc> buffers)
        {
            ulong total = 0;
            foreach (RenderGraphResourceLifetime lifetime in lifetimes)
            {
                if (lifetime.Kind == RenderGraphResourceKind.Image)
                {
                    RenderGraphImageDesc image = images[lifetime.Handle.Index];
                    if (image.Width == 0 || image.Height == 0)
                        continue;
                    total = checked(total + ImageByteEstimator.EstimateBytes(
                        image.Format,
                        new Extent3D { Width = image.Width, Height = image.Height, Depth = 1 },
                        image.MipCount,
                        image.ArrayLayers,
                        image.Samples));
                }
                else
                {
                    RenderGraphBufferDesc buffer = buffers[lifetime.Handle.Index];
                    total = checked(total + (buffer.ByteSize != 0 ? buffer.ByteSize : checked((ulong)buffer.Stride * buffer.Count)));
                }
            }

            return total;
        }

        private static int EstimateBarrierCount(IReadOnlyList<RenderGraphPassDesc> compiled, HashSet<string> culled)
        {
            int count = 0;
            var lastAccess = new Dictionary<RenderGraphResourceHandle, RenderGraphResourceAccess>();
            foreach (RenderGraphPassDesc pass in compiled)
            {
                if (culled.Contains(pass.Name))
                    continue;

                CountUses(pass.Reads);
                CountUses(pass.Writes);
                CountUses(pass.ReadWrites);
            }

            return count;

            void CountUses(IReadOnlyList<RenderGraphResourceUse> uses)
            {
                foreach (RenderGraphResourceUse use in uses)
                {
                    if (lastAccess.TryGetValue(use.Handle, out RenderGraphResourceAccess previous) && previous != use.Access)
                        count++;
                    lastAccess[use.Handle] = use.Access;
                }
            }
        }

        private static string GetName(
            RenderGraphResourceHandle handle,
            IReadOnlyList<RenderGraphImageDesc> images,
            IReadOnlyList<RenderGraphBufferDesc> buffers)
        {
            return handle.Kind == RenderGraphResourceKind.Image
                ? images[handle.Index].Name
                : buffers[handle.Index].Name;
        }
    }
}
