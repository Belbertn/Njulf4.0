using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public enum UploadBudgetCategory
    {
        Unknown,
        Scene,
        Materials,
        Lights,
        Meshes,
        Textures,
        Shadows,
        Environment,
        Reflections,
        Particles,
        Animation
    }

    public sealed record UploadBudgetEntry(UploadBudgetCategory Category, ulong Bytes);

    public sealed record UploadBudgetSnapshot(
        ulong TotalBytes,
        ulong BudgetBytes,
        ulong PeakBytesThisSession,
        int BudgetExceededFrameCount,
        IReadOnlyList<UploadBudgetEntry> Entries,
        RenderBudgetStatus Status);

    public sealed class UploadBudgetTracker
    {
        private readonly ulong[] _bytesByCategory = new ulong[Enum.GetValues<UploadBudgetCategory>().Length];
        private ulong _currentFrameBytes;
        private ulong _peakBytesThisSession;
        private int _budgetExceededFrameCount;

        public void BeginFrame()
        {
            Array.Clear(_bytesByCategory);
            _currentFrameBytes = 0;
        }

        public void AddBytes(UploadBudgetCategory category, ulong bytes)
        {
            if (bytes == 0)
                return;

            int index = (int)category;
            if ((uint)index >= (uint)_bytesByCategory.Length)
                index = (int)UploadBudgetCategory.Unknown;

            _bytesByCategory[index] = checked(_bytesByCategory[index] + bytes);
            _currentFrameBytes = checked(_currentFrameBytes + bytes);
            if (_currentFrameBytes > _peakBytesThisSession)
                _peakBytesThisSession = _currentFrameBytes;
        }

        public UploadBudgetSnapshot EndFrame(RenderBudgetProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            RenderBudgetStatus status = RenderBudgetEvaluator.Classify(_currentFrameBytes, profile.UploadBudgetBytesPerFrame);
            if (status == RenderBudgetStatus.OverBudget)
                _budgetExceededFrameCount++;

            return CreateSnapshot(profile, status);
        }

        public UploadBudgetSnapshot CreateSnapshot(RenderBudgetProfile profile, RenderBudgetStatus? status = null)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var entries = new List<UploadBudgetEntry>();
            for (int i = 0; i < _bytesByCategory.Length; i++)
            {
                ulong bytes = _bytesByCategory[i];
                if (bytes != 0)
                    entries.Add(new UploadBudgetEntry((UploadBudgetCategory)i, bytes));
            }

            return new UploadBudgetSnapshot(
                _currentFrameBytes,
                profile.UploadBudgetBytesPerFrame,
                _peakBytesThisSession,
                _budgetExceededFrameCount,
                entries,
                status ?? RenderBudgetEvaluator.Classify(_currentFrameBytes, profile.UploadBudgetBytesPerFrame));
        }
    }
}
