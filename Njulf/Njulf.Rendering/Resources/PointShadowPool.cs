using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>Independent six-face maps; changing one light never resizes every other light.</summary>
public sealed class PointShadowPool : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BufferManager _buffers;
    private readonly PointShadowCubemapArray _metadata;
    private readonly Dictionary<uint, Map> _resident = new();
    private BindlessHeap? _heap;
    private readonly Dictionary<uint, (uint Resolution, long RetryFrame)> _failed = new();
    private long _frame;
    public sealed record Map(uint Identity, PointShadowCubemapArray Images, int TextureIndex);
    public Map[] Maps { get; private set; } = [];
    public uint MapSize => Maps.Length == 0 ? 0 : Maps.Max(x => x.Images.MapSize);
    public ulong EstimatedImageBytes => _resident.Values.Aggregate(0UL, (sum, x) => sum + x.Images.EstimatedImageBytes);
    public ulong EstimatedBytes => EstimatedImageBytes + _metadata.EstimatedBytes;

    public PointShadowPool(VulkanContext context, BufferManager buffers, ShadowSettings settings)
    { _context = context; _buffers = buffers; _metadata = new(context, buffers, settings); }

    public bool Ensure(ShadowSettings settings, LocalShadowAllocation[] requests)
    {
        if (_heap == null) throw new InvalidOperationException("Register point shadows before allocating maps.");
        _frame++;
        var wanted = requests.ToDictionary(x => x.Identity);
        foreach (uint id in _failed.Keys.Where(x => !wanted.ContainsKey(x)).ToArray()) _failed.Remove(id);
        var removed = _resident.Values.Where(x => !wanted.TryGetValue(x.Identity, out var request) ||
            request.Resolution != x.Images.MapSize).ToArray();
        // Reuse the established synchronous resource-retirement boundary. Only allocation changes
        // enter this path; cache hits and packed-index changes never wait for the device.
        if (removed.Length > 0) _context.WaitIdle();
        foreach (var map in removed)
        { _heap.FreeTextureIndex(map.TextureIndex); map.Images.Dispose(); _resident.Remove(map.Identity); }
        bool changed = removed.Length > 0;
        var maps = new List<Map>();
        foreach (var request in requests)
        {
            if (!_resident.TryGetValue(request.Identity, out var map))
            {
                if (_failed.TryGetValue(request.Identity, out var failure) &&
                    failure.Resolution == request.Resolution && _frame < failure.RetryFrame) continue;
                var images = new PointShadowCubemapArray(_context, _buffers, settings, createDataBuffer: false);
                int index = -1;
                try
                {
                    images.Recreate(request.Resolution, 1);
                    if (images.WorkingImage.Handle == 0)
                    { _failed[request.Identity] = (request.Resolution, _frame + 120); images.Dispose(); continue; }
                    index = _heap.AllocateTextureIndex(images.SampledView, images.MapSampler);
                    _heap.RegisterTexture(index, images.SampledView, images.MapSampler, ImageLayout.DepthStencilReadOnlyOptimal);
                    map = new(request.Identity, images, index);
                    _resident.Add(request.Identity, map);
                    _failed.Remove(request.Identity);
                }
                catch { if (index >= 0) _heap.FreeTextureIndex(index); images.Dispose(); throw; }
                changed = true;
            }
            maps.Add(map);
        }
        Maps = maps.ToArray();
        return changed;
    }

    public void Register(BindlessHeap heap, ImageView fallbackDepthView = default,
        ImageLayout fallbackDepthLayout = ImageLayout.DepthStencilReadOnlyOptimal)
    {
        _heap = heap;
        _metadata.Register(heap, fallbackDepthView, fallbackDepthLayout);
        foreach (var map in _resident.Values)
            heap.RegisterTexture(map.TextureIndex, map.Images.SampledView, map.Images.MapSampler, ImageLayout.DepthStencilReadOnlyOptimal);
    }
    public void Upload(StagingRing staging, CommandBuffer command, ReadOnlySpan<GPUPointShadow> shadows) =>
        _metadata.Upload(staging, command, shadows);

    public void Dispose()
    {
        foreach (var map in _resident.Values)
        { _heap?.FreeTextureIndex(map.TextureIndex); map.Images.Dispose(); }
        _resident.Clear(); Maps = []; _metadata.Dispose();
    }
}
