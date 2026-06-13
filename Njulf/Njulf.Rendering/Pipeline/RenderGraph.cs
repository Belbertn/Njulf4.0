using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Njulf.Rendering.Core;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace Njulf.Rendering.Pipeline
{
    public sealed class RenderGraph : IDisposable
    {
        private readonly List<RenderPassBase> _passes = new List<RenderPassBase>();
        private bool _disposed;

        public IReadOnlyList<string> PassNames => _passes.ConvertAll(pass => pass.Name);
        
        public void AddPass(RenderPassBase pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            _passes.Add(pass);
            System.Diagnostics.Debug.WriteLine($"Render pass added: {pass.Name}");
        }
        
        public void Initialize()
        {
            foreach (var pass in _passes)
                pass.Initialize();
        }
        
        public void Execute(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps = null,
            CommandBufferManager? commandBuffers = null,
            bool useSecondaryCommandBuffers = false)
        {
            foreach (var pass in _passes)
            {
                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                if (useSecondaryCommandBuffers && commandBuffers != null && pass.SupportsSecondaryCommandBuffer)
                {
                    ExecuteSecondaryPass(commandBuffers, cmd, pass, frameIndex, sceneData, timestamps);
                    continue;
                }

                long passStart = Stopwatch.GetTimestamp();
                pass.Context.BeginDebugLabel(cmd, pass.Name);
                timestamps?.BeginPass(cmd, frameIndex, pass.Name);
                try
                {
                    pass.Execute(cmd, frameIndex, sceneData);
                }
                finally
                {
                    timestamps?.EndPass(cmd, frameIndex);
                    pass.Context.EndDebugLabel(cmd);
                    long elapsedMicroseconds = ElapsedMicroseconds(passStart);
                    sceneData.CpuPrimaryCommandRecordMicroseconds += elapsedMicroseconds;
                    SetPassRecordMicroseconds(sceneData, pass.Name, elapsedMicroseconds);
                }
            }
        }

        private static void ExecuteSecondaryPass(
            CommandBufferManager commandBuffers,
            CommandBuffer primary,
            RenderPassBase pass,
            int frameIndex,
            SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps)
        {
            long passStart = Stopwatch.GetTimestamp();
            CommandBuffer secondary = commandBuffers.BeginSecondaryGraphicsCommand(frameIndex, pass.Name);
            pass.Context.BeginDebugLabel(secondary, pass.Name);
            timestamps?.BeginPass(secondary, frameIndex, pass.Name);
            try
            {
                pass.Execute(secondary, frameIndex, sceneData);
            }
            finally
            {
                timestamps?.EndPass(secondary, frameIndex);
                pass.Context.EndDebugLabel(secondary);
                commandBuffers.EndCommandBuffer(secondary);
            }

            commandBuffers.ExecuteSecondaryGraphicsCommand(primary, secondary);
            long elapsedMicroseconds = ElapsedMicroseconds(passStart);
            sceneData.SecondaryCommandBufferPassCount++;
            sceneData.CpuSecondaryCommandRecordMicroseconds += elapsedMicroseconds;
            SetPassRecordMicroseconds(sceneData, pass.Name, elapsedMicroseconds);
        }

        private static void SetPassRecordMicroseconds(SceneRenderingData sceneData, string passName, long elapsedMicroseconds)
        {
            switch (passName)
            {
                case "DepthPrePass":
                    sceneData.CpuDepthPrePassRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "DirectionalShadowPass":
                    sceneData.CpuDirectionalShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "SpotShadowPass":
                    sceneData.CpuSpotShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "PointShadowPass":
                    sceneData.CpuPointShadowRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "HiZBuildPass":
                    sceneData.CpuHiZBuildRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AmbientOcclusionPass":
                    sceneData.CpuAmbientOcclusionRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AmbientOcclusionBlurPass":
                    sceneData.CpuAmbientOcclusionBlurRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "TiledLightCullingPass":
                    sceneData.CpuLightCullRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "ForwardPlusPass":
                    sceneData.CpuForwardOpaqueRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "TransparentForwardPass":
                    sceneData.CpuTransparentRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "ParticlePass":
                    sceneData.CpuParticleRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "FogPass":
                    sceneData.CpuFogRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "AutoExposurePass":
                    sceneData.CpuAutoExposureRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "ToneMapCompositePass":
                    sceneData.CpuCompositeRecordMicroseconds = elapsedMicroseconds;
                    break;
            }
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }
        
        public void OnSwapchainRecreated()
        {
            foreach (var pass in _passes)
                pass.OnSwapchainRecreated();
        }
        
        public void Cleanup()
        {
            foreach (var pass in _passes)
                pass.Cleanup();
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
            System.Diagnostics.Debug.WriteLine("Render graph disposed.");
        }
    }
}
