using Njulf.Rendering.Core;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SwapchainPresentModeTests
{
    private static readonly PresentModeKHR[] AllModes =
    [
        PresentModeKHR.ImmediateKhr,
        PresentModeKHR.MailboxKhr,
        PresentModeKHR.FifoRelaxedKhr,
        PresentModeKHR.FifoKhr
    ];

    [Test]
    public void ChooseSwapPresentMode_VSyncUsesFifo()
    {
        PresentModeKHR selected =
            SwapchainManager.ChooseSwapPresentMode(AllModes, vSync: true);

        Assert.That(selected, Is.EqualTo(PresentModeKHR.FifoKhr));
    }

    [Test]
    public void ChooseSwapPresentMode_VSyncFallsBackToNonTearingMailbox()
    {
        PresentModeKHR selected = SwapchainManager.ChooseSwapPresentMode(
            [PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr],
            vSync: true);

        Assert.That(selected, Is.EqualTo(PresentModeKHR.MailboxKhr));
    }

    [Test]
    public void ChooseSwapPresentMode_UncappedPrefersImmediate()
    {
        PresentModeKHR selected =
            SwapchainManager.ChooseSwapPresentMode(AllModes, vSync: false);

        Assert.That(selected, Is.EqualTo(PresentModeKHR.ImmediateKhr));
    }

    [Test]
    public void ChooseSwapPresentMode_UncappedFallsBackToMailboxThenFifo()
    {
        PresentModeKHR mailbox = SwapchainManager.ChooseSwapPresentMode(
            [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr],
            vSync: false);
        PresentModeKHR fifo = SwapchainManager.ChooseSwapPresentMode(
            [PresentModeKHR.FifoKhr],
            vSync: false);

        Assert.Multiple(() =>
        {
            Assert.That(mailbox, Is.EqualTo(PresentModeKHR.MailboxKhr));
            Assert.That(fifo, Is.EqualTo(PresentModeKHR.FifoKhr));
        });
    }

    [Test]
    public void ChooseSwapPresentMode_RejectsEmptyCapabilityList()
    {
        Assert.Throws<ArgumentException>(() =>
            SwapchainManager.ChooseSwapPresentMode([], vSync: true));
    }
}
