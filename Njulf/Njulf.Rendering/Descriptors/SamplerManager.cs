using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Descriptors
{
    public class SamplerManager : IDisposable
    {
        private readonly VulkanContext _context;
        private Sampler _defaultSampler;
        private Sampler _linearRepeatSampler;
        private Sampler _linearClampSampler;
        private Sampler _nearestRepeatSampler;
        private Sampler _nearestClampSampler;

        public Sampler DefaultSampler => _defaultSampler;
        public Sampler LinearRepeatSampler => _linearRepeatSampler;
        public Sampler LinearClampSampler => _linearClampSampler;
        public Sampler NearestRepeatSampler => _nearestRepeatSampler;
        public Sampler NearestClampSampler => _nearestClampSampler;

        public SamplerManager(VulkanContext context)
        {
            _context = context;
            CreateDefaultSampler();
        }

        private void CreateDefaultSampler()
        {
            // Linear filtering, Repeat wrap mode
            var createInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.Repeat,
                AddressModeV = SamplerAddressMode.Repeat,
                AddressModeW = SamplerAddressMode.Repeat,
                AnisotropyEnable = true,
                MaxAnisotropy = 16f,
                BorderColor = BorderColor.FloatTransparentBlack,
                UnnormalizedCoordinates = false,
                CompareEnable = false,
                CompareOp = CompareOp.Always,
                MipLodBias = 0f,
                MinLod = 0f,
                MaxLod = float.MaxValue
            };

            if (_context.Api.CreateSampler(_context.Device, &createInfo, null, out _defaultSampler) != Result.Success)
                throw new Exception("Failed to create default sampler");

            _linearRepeatSampler = _defaultSampler;

            // Linear filtering, Clamp to edge
            createInfo.AddressModeU = SamplerAddressMode.ClampToEdge;
            createInfo.AddressModeV = SamplerAddressMode.ClampToEdge;
            createInfo.AddressModeW = SamplerAddressMode.ClampToEdge;
            if (_context.Api.CreateSampler(_context.Device, &createInfo, null, out _linearClampSampler) != Result.Success)
                throw new Exception("Failed to create linear clamp sampler");

            // Nearest filtering, Repeat
            createInfo.MagFilter = Filter.Nearest;
            createInfo.MinFilter = Filter.Nearest;
            createInfo.AddressModeU = SamplerAddressMode.Repeat;
            createInfo.AddressModeV = SamplerAddressMode.Repeat;
            createInfo.AddressModeW = SamplerAddressMode.Repeat;
            if (_context.Api.CreateSampler(_context.Device, &createInfo, null, out _nearestRepeatSampler) != Result.Success)
                throw new Exception("Failed to create nearest repeat sampler");

            // Nearest filtering, Clamp
            createInfo.AddressModeU = SamplerAddressMode.ClampToEdge;
            createInfo.AddressModeV = SamplerAddressMode.ClampToEdge;
            createInfo.AddressModeW = SamplerAddressMode.ClampToEdge;
            if (_context.Api.CreateSampler(_context.Device, &createInfo, null, out _nearestClampSampler) != Result.Success)
                throw new Exception("Failed to create nearest clamp sampler");
        }

        public void Dispose()
        {
            if (_defaultSampler != null)
            {
                _context.Api.DestroySampler(_context.Device, _defaultSampler, null);
                _defaultSampler = null;
            }
            if (_linearRepeatSampler != null && _linearRepeatSampler != _defaultSampler)
            {
                _context.Api.DestroySampler(_context.Device, _linearRepeatSampler, null);
                _linearRepeatSampler = null;
            }
            if (_linearClampSampler != null)
            {
                _context.Api.DestroySampler(_context.Device, _linearClampSampler, null);
                _linearClampSampler = null;
            }
            if (_nearestRepeatSampler != null)
            {
                _context.Api.DestroySampler(_context.Device, _nearestRepeatSampler, null);
                _nearestRepeatSampler = null;
            }
            if (_nearestClampSampler != null)
            {
                _context.Api.DestroySampler(_context.Device, _nearestClampSampler, null);
                _nearestClampSampler = null;
            }
        }
    }
}
