using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Njulf.Rendering.Pipeline;

public sealed record RenderGraphDiagnosticSnapshot(
    IReadOnlyList<string> CompiledPassOrder,
    IReadOnlyList<string> CulledPasses,
    IReadOnlyList<RenderGraphResourceLifetime> ResourceLifetimes,
    IReadOnlyList<RenderGraphAliasGroup> AliasGroups,
    int BarrierCount,
    string DotGraph);

public static class RenderGraphDiagnosticExporter
{
    public static RenderGraphDiagnosticSnapshot Export(
        RenderGraphDeclarationPlan declarationPlan,
        RenderGraphBarrierPlan barrierPlan,
        RenderGraphAliasPlan aliasPlan)
    {
        if (declarationPlan == null)
            throw new ArgumentNullException(nameof(declarationPlan));
        if (barrierPlan == null)
            throw new ArgumentNullException(nameof(barrierPlan));
        if (aliasPlan == null)
            throw new ArgumentNullException(nameof(aliasPlan));

        return new RenderGraphDiagnosticSnapshot(
            declarationPlan.Diagnostics.CompiledPassOrder.ToArray(),
            declarationPlan.Diagnostics.CulledPasses.ToArray(),
            declarationPlan.Diagnostics.ResourceLifetimes.ToArray(),
            aliasPlan.Groups.ToArray(),
            barrierPlan.BarrierCount,
            BuildDot(declarationPlan, aliasPlan));
    }

    public static string BuildDot(RenderGraphDeclarationPlan declarationPlan, RenderGraphAliasPlan? aliasPlan = null)
    {
        if (declarationPlan == null)
            throw new ArgumentNullException(nameof(declarationPlan));

        var builder = new StringBuilder();
        builder.AppendLine("digraph RenderGraph {");
        builder.AppendLine("  rankdir=LR;");

        var compiledPasses = new HashSet<string>(declarationPlan.Diagnostics.CompiledPassOrder, StringComparer.Ordinal);
        var passByName = declarationPlan.Passes.ToDictionary(pass => pass.Name, StringComparer.Ordinal);
        foreach (string passName in declarationPlan.Diagnostics.CompiledPassOrder)
        {
            RenderGraphPassDesc pass = passByName[passName];
            builder.Append("  \"").Append(Escape(pass.Name)).Append("\" [shape=box,label=\"")
                .Append(Escape(pass.Name)).Append("\\n").Append(pass.Queue).AppendLine("\"];");
        }

        foreach (RenderGraphResourceLifetime lifetime in declarationPlan.Diagnostics.ResourceLifetimes)
        {
            builder.Append("  \"").Append(ResourceNode(lifetime.Handle)).Append("\" [shape=ellipse,label=\"")
                .Append(Escape(lifetime.Name)).Append("\\n")
                .Append(lifetime.Kind).Append(" ")
                .Append(lifetime.FirstUsePassIndex).Append("-")
                .Append(lifetime.LastUsePassIndex).AppendLine("\"];");
        }

        foreach (string passName in declarationPlan.Diagnostics.CompiledPassOrder)
        {
            RenderGraphPassDesc pass = passByName[passName];
            AddEdges(builder, pass.Reads, pass.Name, resourceToPass: true);
            AddEdges(builder, pass.Writes, pass.Name, resourceToPass: false);
            AddEdges(builder, pass.ReadWrites, pass.Name, resourceToPass: true);
            AddEdges(builder, pass.ReadWrites, pass.Name, resourceToPass: false);
        }

        if (aliasPlan != null)
        {
            foreach (RenderGraphAliasGroup group in aliasPlan.Groups)
            {
                string groupName = $"alias_{group.GroupIndex}";
                builder.Append("  \"").Append(groupName).Append("\" [shape=note,label=\"Alias ")
                    .Append(group.GroupIndex).Append("\\nreserved=")
                    .Append(group.ReservedBytes).Append("\\nsaved=")
                    .Append(group.AliasedBytesSaved).AppendLine("\"];");
                foreach (RenderGraphImageAllocationRequest image in group.Images)
                {
                    builder.Append("  \"").Append(groupName).Append("\" -> \"")
                        .Append(ResourceNode(image.Handle)).AppendLine("\" [style=dashed];");
                }
            }
        }

        foreach (string culled in declarationPlan.Diagnostics.CulledPasses)
        {
            if (!compiledPasses.Contains(culled))
            {
                builder.Append("  \"").Append(Escape(culled)).Append("\" [shape=box,style=dashed,label=\"")
                    .Append(Escape(culled)).Append("\\nculled\"];\n");
            }
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AddEdges(StringBuilder builder, IReadOnlyList<RenderGraphResourceUse> uses, string passName, bool resourceToPass)
    {
        foreach (RenderGraphResourceUse use in uses)
        {
            string resource = ResourceNode(use.Handle);
            if (resourceToPass)
            {
                builder.Append("  \"").Append(resource).Append("\" -> \"")
                    .Append(Escape(passName)).AppendLine("\";");
            }
            else
            {
                builder.Append("  \"").Append(Escape(passName)).Append("\" -> \"")
                    .Append(resource).AppendLine("\";");
            }
        }
    }

    private static string ResourceNode(RenderGraphResourceHandle handle)
    {
        return $"{handle.Kind}_{handle.Index}_{handle.Generation}";
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
