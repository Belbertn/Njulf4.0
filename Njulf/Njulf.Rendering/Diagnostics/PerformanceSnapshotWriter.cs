using System;
using System.IO;
using System.Text.Json;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record PerformanceSnapshot(
        DateTimeOffset CapturedAt,
        RenderBudgetProfile Profile,
        RendererDiagnostics Diagnostics,
        RenderBudgetSnapshot Budget)
    {
        public RenderGraphResourceInventorySnapshot ResourceInventory { get; init; } = RenderGraphResourceInventorySnapshot.Empty;
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
            RenderGraphResourceInventorySnapshot? resourceInventory = null)
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
                ResourceInventory = resourceInventory ?? RenderGraphResourceInventorySnapshot.Empty
            };
            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json);
            return path;
        }
    }
}
