using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    public sealed class RenderGraph : IDisposable
    {
        private readonly List<RenderPassBase> _passes = new List<RenderPassBase>();
        private bool _disposed;
        
        public void AddPass(RenderPassBase pass)
        {
            if (pass == null)
                throw new ArgumentNullException(nameof(pass));
            _passes.Add(pass);
            Console.WriteLine("Render pass added");
        }
        
        public void Initialize()
        {
            foreach (var pass in _passes)
                pass.Initialize();
        }
        
        public void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            foreach (var pass in _passes)
            {
                var barriers = pass.GetBarriers(frameIndex);
                foreach (var barrier in barriers)
                    BarrierBuilder.ExecuteBarrier(cmd, barrier);

                long passStart = Stopwatch.GetTimestamp();
                pass.Execute(cmd, frameIndex, sceneData);
                SetPassRecordMicroseconds(sceneData, pass.Name, ElapsedMicroseconds(passStart));
            }
        }

        private static void SetPassRecordMicroseconds(SceneRenderingData sceneData, string passName, long elapsedMicroseconds)
        {
            switch (passName)
            {
                case "DepthPrePass":
                    sceneData.CpuDepthPrePassRecordMicroseconds = elapsedMicroseconds;
                    break;
                case "HiZBuildPass":
                    sceneData.CpuHiZBuildRecordMicroseconds = elapsedMicroseconds;
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
            Console.WriteLine("Render graph disposed.");
        }
        
        ~RenderGraph()
        {
            Dispose(false);
        }
    }
}
