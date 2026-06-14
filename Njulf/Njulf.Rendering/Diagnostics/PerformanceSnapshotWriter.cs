using System;
using System.IO;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record PerformanceSnapshot(
        DateTimeOffset CapturedAt,
        RenderBudgetProfile Profile,
        RendererDiagnostics Diagnostics,
        RenderBudgetSnapshot Budget)
    {
        public int SchemaVersion { get; init; } = RendererDiagnosticsSchema.CurrentVersion;
        public RenderGraphResourceInventorySnapshot ResourceInventory { get; init; } = RenderGraphResourceInventorySnapshot.Empty;
        public RendererDiagnosticsSnapshot StructuredDiagnostics { get; init; } = RendererDiagnosticsSnapshot.Empty;
        public RendererDiagnosticsOverlaySnapshot OverlayData { get; init; } = RendererDiagnosticsOverlaySnapshot.Empty;
    }

    public sealed class PerformanceSnapshotWriter
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        public string Write(
            string directory,
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot budget,
            RenderGraphResourceInventorySnapshot? resourceInventory = null,
            RenderGraphDiagnosticSnapshot? graphDiagnostics = null,
            FrameOrderAudit? frameAudit = null,
            AsyncSchedulePlan? asyncSchedule = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Snapshot directory is required.", nameof(directory));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (budget == null)
                throw new ArgumentNullException(nameof(budget));

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"performance-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            var snapshot = new PerformanceSnapshot(DateTimeOffset.Now, budget.Profile, diagnostics, budget)
            {
                ResourceInventory = resourceInventory ?? RenderGraphResourceInventorySnapshot.Empty,
                StructuredDiagnostics = RendererDiagnosticsSchema.Build(
                    diagnostics,
                    budget,
                    resourceInventory ?? RenderGraphResourceInventorySnapshot.Empty,
                    graphDiagnostics,
                    frameAudit,
                    asyncSchedule),
                OverlayData = RendererDiagnosticsOverlayBuilder.Build(
                    diagnostics,
                    budget,
                    resourceInventory ?? RenderGraphResourceInventorySnapshot.Empty,
                    graphDiagnostics,
                    asyncSchedule)
            };
            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json);
            return path;
        }
    }
}
