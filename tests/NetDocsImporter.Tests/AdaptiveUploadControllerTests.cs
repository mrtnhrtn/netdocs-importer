using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class AdaptiveUploadControllerTests
{
    [Fact]
    public void RegisterOutcome_ThrottleResponse_ScalesDownAndBackoffIncreases()
    {
        var controller = new AdaptiveUploadController(minConcurrency: 1, maxConcurrency: 8, initialConcurrency: 4);

        var delay = controller.RegisterOutcome(429, succeeded: false);

        Assert.True(delay > TimeSpan.Zero);
        Assert.Equal(3, controller.CurrentConcurrency);
        Assert.True(controller.CurrentBackoffMs >= 750);
    }

    [Fact]
    public void RegisterOutcome_SustainedSuccess_ScalesUp()
    {
        var controller = new AdaptiveUploadController(minConcurrency: 1, maxConcurrency: 6, initialConcurrency: 2, successesForScaleUp: 3);

        controller.RegisterOutcome(200, succeeded: true);
        controller.RegisterOutcome(200, succeeded: true);
        controller.RegisterOutcome(200, succeeded: true);

        Assert.Equal(3, controller.CurrentConcurrency);
    }

    [Fact]
    public async Task WaitForSlotAsync_RespectsCurrentConcurrencyWindow()
    {
        var controller = new AdaptiveUploadController(minConcurrency: 1, maxConcurrency: 4, initialConcurrency: 1, successesForScaleUp: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var blockedTask = controller.WaitForSlotAsync(workerId: 2, cts.Token);
        await Task.Delay(150, cts.Token);
        Assert.False(blockedTask.IsCompleted);

        controller.RegisterOutcome(200, succeeded: true);
        controller.RegisterOutcome(200, succeeded: true);
        await blockedTask;
    }
}
