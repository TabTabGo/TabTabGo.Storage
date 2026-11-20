using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Tests;

// A Docker / Ryuk start that never completes used to hang a whole test run for 10+ minutes; the emulator fixtures now fail fast.
public class EmulatorStartTests
{
    [Fact]
    public async Task RunAsync_AStartThatNeverCompletes_FailsFast_WithAMessageThatNamesTheCauses()
    {
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EmulatorStart.RunAsync("Azurite", ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(300)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Contains("Azurite", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Docker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TESTCONTAINERS_RYUK_DISABLED", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AStartThatThrows_IsWrapped_WithTheOriginalAsInnerException()
    {
        var original = new IOException("docker daemon not reachable");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EmulatorStart.RunAsync("LocalStack", _ => throw original, TimeSpan.FromSeconds(5)));

        Assert.Same(original, ex.InnerException);
        Assert.Contains("LocalStack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ACompletedStart_Returns()
    {
        var ran = false;

        await EmulatorStart.RunAsync("Azurite", _ => { ran = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(5));

        Assert.True(ran);
    }

    [Fact]
    public void DefaultTimeout_IsThreeMinutes_AndCanBeSetFromTheEnvironment()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), EmulatorStart.ParseTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(45), EmulatorStart.ParseTimeout("45"));
        Assert.Equal(TimeSpan.FromMinutes(3), EmulatorStart.ParseTimeout("not a number"));
        Assert.Equal(TimeSpan.FromMinutes(3), EmulatorStart.ParseTimeout("0"));
    }
}
