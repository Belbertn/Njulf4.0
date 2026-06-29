using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Debug
{
    public sealed unsafe class GpuTimestampRecorder : IDisposable
    {
        private const int MaxPassesPerFrame = 96;
        private const int QueriesPerPass = 2;
        private const int QueryCount = MaxPassesPerFrame * QueriesPerPass;

        private readonly VulkanContext _context;
        private readonly QueryPool[] _queryPools = new QueryPool[FramesInFlight];
        private readonly List<PassQuery>[] _passQueries = new List<PassQuery>[FramesInFlight];
        private readonly FrameTimingSnapshot[] _completedSnapshots = new FrameTimingSnapshot[FramesInFlight];
        private readonly bool[] _framePending = new bool[FramesInFlight];
        private bool _disposed;

        public GpuTimestampRecorder(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Supported = context.TimestampComputeAndGraphicsSupported && context.TimestampPeriodNanoseconds > 0.0f;
            UnsupportedReason = Supported
                ? string.Empty
                : "Physical device does not support graphics/compute timestamps or reports an invalid timestamp period.";

            for (int i = 0; i < FramesInFlight; i++)
            {
                _passQueries[i] = new List<PassQuery>(MaxPassesPerFrame);
                _completedSnapshots[i] = FrameTimingSnapshot.Empty;
            }

            if (!Supported)
                return;

            for (int i = 0; i < FramesInFlight; i++)
            {
                var createInfo = new QueryPoolCreateInfo
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = QueryCount
                };

                Result result = _context.Api.CreateQueryPool(_context.Device, &createInfo, null, out _queryPools[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GPU timestamp query pool.", result);

                _context.SetDebugName(_queryPools[i].Handle, ObjectType.QueryPool, $"GPU Timestamp Query Pool Frame {i}");
            }
        }

        public bool Supported { get; }
        public bool EnabledThisFrame { get; private set; }
        public bool PendingThisFrame { get; private set; }
        public string UnsupportedReason { get; }
        public FrameTimingSnapshot LastCompletedSnapshot { get; private set; } = FrameTimingSnapshot.Empty;

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            PendingThisFrame = false;

            if (!Supported || !_framePending[frameIndex])
            {
                LastCompletedSnapshot = _completedSnapshots[frameIndex];
                return;
            }

            ulong* timestamps = stackalloc ulong[QueryCount];
            Result result = _context.Api.GetQueryPoolResults(
                _context.Device,
                _queryPools[frameIndex],
                0,
                QueryCount,
                (nuint)(QueryCount * sizeof(ulong)),
                timestamps,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);

            if (result != Result.Success)
            {
                LastCompletedSnapshot = FrameTimingSnapshot.Empty;
                _completedSnapshots[frameIndex] = FrameTimingSnapshot.Empty;
                _framePending[frameIndex] = false;
                return;
            }

            var timings = new List<PassTiming>(_passQueries[frameIndex].Count);
            foreach (PassQuery passQuery in _passQueries[frameIndex])
            {
                ulong start = timestamps[passQuery.StartQuery];
                ulong end = timestamps[passQuery.EndQuery];
                long gpuMicroseconds = FrameTimingSnapshot.ConvertTimestampDeltaToMicroseconds(
                    start,
                    end,
                    _context.TimestampPeriodNanoseconds);
                timings.Add(new PassTiming(passQuery.Name, 0, gpuMicroseconds, gpuMicroseconds > 0));
            }

            _completedSnapshots[frameIndex] = new FrameTimingSnapshot(timings);
            LastCompletedSnapshot = _completedSnapshots[frameIndex];
            _framePending[frameIndex] = false;
        }

        public void BeginFrame(CommandBuffer commandBuffer, int frameIndex, bool enabled)
        {
            ValidateFrameIndex(frameIndex);
            EnabledThisFrame = Supported && enabled;
            PendingThisFrame = false;
            _passQueries[frameIndex].Clear();

            if (!EnabledThisFrame)
                return;

            _context.Api.CmdResetQueryPool(commandBuffer, _queryPools[frameIndex], 0, QueryCount);
            PendingThisFrame = true;
            _framePending[frameIndex] = true;
        }

        public void BeginPass(CommandBuffer commandBuffer, int frameIndex, string passName)
        {
            if (!EnabledThisFrame)
                return;
            ValidateFrameIndex(frameIndex);
            if (_passQueries[frameIndex].Count >= MaxPassesPerFrame)
                return;

            uint query = checked((uint)(_passQueries[frameIndex].Count * QueriesPerPass));
            _passQueries[frameIndex].Add(new PassQuery(passName, query, query + 1));
            _context.Api.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.TopOfPipeBit, _queryPools[frameIndex], query);
        }

        public void EndPass(CommandBuffer commandBuffer, int frameIndex)
        {
            if (!EnabledThisFrame)
                return;
            ValidateFrameIndex(frameIndex);
            if (_passQueries[frameIndex].Count == 0)
                return;

            PassQuery passQuery = _passQueries[frameIndex][^1];
            _context.Api.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.BottomOfPipeBit, _queryPools[frameIndex], passQuery.EndQuery);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!Supported)
                return;

            for (int i = 0; i < _queryPools.Length; i++)
            {
                if (_queryPools[i].Handle != 0)
                    _context.Api.DestroyQueryPool(_context.Device, _queryPools[i], null);
            }
        }

        private static void ValidateFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        private readonly record struct PassQuery(string Name, uint StartQuery, uint EndQuery);
    }
}
