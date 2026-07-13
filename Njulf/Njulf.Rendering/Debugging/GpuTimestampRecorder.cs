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
        private readonly QueryPool[] _graphicsQueryPools = new QueryPool[FramesInFlight];
        private readonly QueryPool[] _computeQueryPools = new QueryPool[FramesInFlight];
        private readonly List<PassQuery>[] _passQueries = new List<PassQuery>[FramesInFlight];
        private readonly List<int>[] _activePassQueries = new List<int>[FramesInFlight];
        private readonly int[] _graphicsPassQueryCounts = new int[FramesInFlight];
        private readonly int[] _computePassQueryCounts = new int[FramesInFlight];
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
                _activePassQueries[i] = new List<int>(8);
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

                Result result = _context.Api.CreateQueryPool(_context.Device, &createInfo, null, out _graphicsQueryPools[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GPU timestamp query pool.", result);

                _context.SetDebugName(_graphicsQueryPools[i].Handle, ObjectType.QueryPool, $"GPU Graphics Timestamp Query Pool Frame {i}");

                result = _context.Api.CreateQueryPool(_context.Device, &createInfo, null, out _computeQueryPools[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GPU compute timestamp query pool.", result);

                _context.SetDebugName(_computeQueryPools[i].Handle, ObjectType.QueryPool, $"GPU Compute Timestamp Query Pool Frame {i}");
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

            int graphicsUsedQueryCount = _graphicsPassQueryCounts[frameIndex] * QueriesPerPass;
            int computeUsedQueryCount = _computePassQueryCounts[frameIndex] * QueriesPerPass;
            if (graphicsUsedQueryCount == 0 && computeUsedQueryCount == 0)
            {
                _completedSnapshots[frameIndex] = FrameTimingSnapshot.Empty;
                LastCompletedSnapshot = FrameTimingSnapshot.Empty;
                _framePending[frameIndex] = false;
                return;
            }

            ulong* graphicsTimestamps = stackalloc ulong[QueryCount];
            ulong* computeTimestamps = stackalloc ulong[QueryCount];
            if (!TryReadQueryPool(_graphicsQueryPools[frameIndex], graphicsUsedQueryCount, graphicsTimestamps) ||
                !TryReadQueryPool(_computeQueryPools[frameIndex], computeUsedQueryCount, computeTimestamps))
            {
                LastCompletedSnapshot = FrameTimingSnapshot.Empty;
                _completedSnapshots[frameIndex] = FrameTimingSnapshot.Empty;
                _framePending[frameIndex] = false;
                return;
            }

            var timings = new List<PassTiming>(_passQueries[frameIndex].Count);
            foreach (PassQuery passQuery in _passQueries[frameIndex])
            {
                ulong* timestamps = passQuery.Queue == TimestampQueue.Compute ? computeTimestamps : graphicsTimestamps;
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
            _activePassQueries[frameIndex].Clear();
            _graphicsPassQueryCounts[frameIndex] = 0;
            _computePassQueryCounts[frameIndex] = 0;

            if (!EnabledThisFrame)
                return;

            _context.Api.CmdResetQueryPool(commandBuffer, _graphicsQueryPools[frameIndex], 0, QueryCount);
            _context.Api.CmdResetQueryPool(commandBuffer, _computeQueryPools[frameIndex], 0, QueryCount);
            PendingThisFrame = true;
            _framePending[frameIndex] = true;
        }

        public void BeginPass(CommandBuffer commandBuffer, int frameIndex, string passName)
        {
            BeginPass(commandBuffer, frameIndex, passName, TimestampQueue.Graphics);
        }

        public void BeginComputePass(CommandBuffer commandBuffer, int frameIndex, string passName)
        {
            BeginPass(commandBuffer, frameIndex, passName, TimestampQueue.Compute);
        }

        private void BeginPass(CommandBuffer commandBuffer, int frameIndex, string passName, TimestampQueue queue)
        {
            if (!EnabledThisFrame)
                return;
            ValidateFrameIndex(frameIndex);
            int passQueryIndex = queue == TimestampQueue.Compute
                ? _computePassQueryCounts[frameIndex]
                : _graphicsPassQueryCounts[frameIndex];
            if (passQueryIndex >= MaxPassesPerFrame)
                return;

            uint query = checked((uint)(passQueryIndex * QueriesPerPass));
            if (queue == TimestampQueue.Compute)
                _computePassQueryCounts[frameIndex]++;
            else
                _graphicsPassQueryCounts[frameIndex]++;

            _passQueries[frameIndex].Add(new PassQuery(passName, query, query + 1, queue));
            _activePassQueries[frameIndex].Add(_passQueries[frameIndex].Count - 1);
            QueryPool queryPool = queue == TimestampQueue.Compute ? _computeQueryPools[frameIndex] : _graphicsQueryPools[frameIndex];
            _context.Api.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.TopOfPipeBit, queryPool, query);
        }

        public void EndPass(CommandBuffer commandBuffer, int frameIndex)
        {
            if (!EnabledThisFrame)
                return;
            ValidateFrameIndex(frameIndex);
            if (_activePassQueries[frameIndex].Count == 0)
                return;

            int stackIndex = _activePassQueries[frameIndex].Count - 1;
            PassQuery passQuery = _passQueries[frameIndex][_activePassQueries[frameIndex][stackIndex]];
            _activePassQueries[frameIndex].RemoveAt(stackIndex);
            QueryPool queryPool = passQuery.Queue == TimestampQueue.Compute ? _computeQueryPools[frameIndex] : _graphicsQueryPools[frameIndex];
            _context.Api.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.BottomOfPipeBit, queryPool, passQuery.EndQuery);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!Supported)
                return;

            for (int i = 0; i < _graphicsQueryPools.Length; i++)
            {
                if (_graphicsQueryPools[i].Handle != 0)
                    _context.Api.DestroyQueryPool(_context.Device, _graphicsQueryPools[i], null);
                if (_computeQueryPools[i].Handle != 0)
                    _context.Api.DestroyQueryPool(_context.Device, _computeQueryPools[i], null);
            }
        }

        private bool TryReadQueryPool(QueryPool queryPool, int usedQueryCount, ulong* timestamps)
        {
            if (usedQueryCount == 0)
                return true;

            Result result = _context.Api.GetQueryPoolResults(
                _context.Device,
                queryPool,
                0,
                checked((uint)usedQueryCount),
                (nuint)(usedQueryCount * sizeof(ulong)),
                timestamps,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);
            return result == Result.Success;
        }

        private static void ValidateFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        private enum TimestampQueue
        {
            Graphics,
            Compute
        }

        private readonly record struct PassQuery(string Name, uint StartQuery, uint EndQuery, TimestampQueue Queue);
    }
}
