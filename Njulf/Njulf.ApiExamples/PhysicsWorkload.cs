using System.Diagnostics;
using System.Text.Json;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Physics;

namespace Njulf.ApiExamples;

/// <summary>A finite scene workload, run in a fresh process per mode; no window or renderer.</summary>
internal static class PhysicsWorkload
{
    public static int Run(string[] args)
    {
        if (args.Length != 4 || !Enum.TryParse<PhysicsMode>(args[1], out var mode) || !Enum.IsDefined(mode) ||
            args[2] is not ("static" or "moving"))
            throw new ArgumentException("--physics-workload Disabled|QueryOnly|Simulation static|moving output.json");
        bool moving = args[2] == "moving";
        using var process = Process.GetCurrentProcess();
        process.Refresh(); long memoryBefore = process.PrivateMemorySize64;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long startup = Stopwatch.GetTimestamp();
        using var scene = new Scene();
        using var physics = PhysicsScene.Create(mode, scene);
        var nodes = new SceneNode[256];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new SceneNode { Position = new(i % 16 * 2, 0, i / 16 * 2) };
            physics?.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One)], node: nodes[i],
                body: new BodySettings { Kind = moving && i < 32 && mode == PhysicsMode.Simulation ? BodyKind.Kinematic : BodyKind.Static });
        }
        double startupMs = Stopwatch.GetElapsedTime(startup).TotalMilliseconds;
        long startupAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var overlaps = new OverlapHit[16];
        int hitCount = 0;
        void Frame(int frame)
        {
            if (moving)
                for (int i = 0; i < 32; i++) nodes[i].Position = new(i % 16 * 2, MathF.Sin(frame * .01f + i) * .25f, i / 16 * 2);
            if (physics == null) return;
            physics.Synchronize();
            if (mode == PhysicsMode.Simulation) physics.Step(1f / 60);
            for (int i = 0; i < 32; i++)
            {
                var origin = new Vector3(i % 16 * 2, 4, i / 16 * 2);
                if (physics.Raycast(origin, -Vector3.UnitY, 8, out _)) hitCount++;
                if (physics.SweepSphere(origin, .25f, -Vector3.UnitY, 8, out _)) hitCount++;
                hitCount += physics.OverlapSphere(origin - Vector3.UnitY * 4, .75f, overlaps).Total;
            }
        }
        for (int i = 0; i < 2000; i++) Frame(i);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new double[4000];
        long allocations = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp(); Frame(i + 2000);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        allocations = GC.GetAllocatedBytesForCurrentThread() - allocations;
        process.Refresh(); long memoryDuring = process.PrivateMemorySize64;
        long workingSet = process.WorkingSet64;
        long unmanaged = physics?.UnmanagedBytes ?? 0;
        long syncs = physics?.TransformSynchronizations ?? 0;
        long steps = physics?.CompletedSteps ?? 0;
        physics?.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
        Array.Sort(samples);
        var report = new
        {
            DateUtc = DateTime.UtcNow, Mode = mode.ToString(), Workload = args[2], Colliders = 256,
            WarmupFrames = 2000, Samples = samples.Length, RaycastsPerFrame = 32, SphereSweepsPerFrame = 32, OverlapsPerFrame = 32,
            StartupMs = startupMs, StartupAllocatedBytes = startupAllocations,
            MedianUs = samples[samples.Length / 2], P95Us = samples[(int)(samples.Length * .95)], P99Us = samples[(int)(samples.Length * .99)],
            ManagedAllocatedBytes = allocations, PrivateBytesBefore = memoryBefore, PrivateBytesDuring = memoryDuring,
            PrivateBytesAfterDispose = process.PrivateMemorySize64, WorkingSetBytes = workingSet, JitterUnmanagedBytes = unmanaged,
            TransformSynchronizations = syncs, CompletedSteps = steps, QueryHitChecksum = hitCount,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ProcessorCount = Environment.ProcessorCount
        };
        string path = Path.GetFullPath(args[3]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{mode}/{args[2]} median={report.MedianUs:F2}us p95={report.P95Us:F2}us p99={report.P99Us:F2}us allocated={allocations}");
        return 0;
    }
}
