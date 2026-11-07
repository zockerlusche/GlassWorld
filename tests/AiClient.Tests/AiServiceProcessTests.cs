namespace GlassWorld.AiClient.Tests;

public sealed class AiServiceProcessTests
{
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    [Theory]
    [InlineData("AI_SERVICE_READY port=51234", 51234)]
    [InlineData("AI_SERVICE_READY port=1 ", 1)]
    [InlineData("INFO: started", null)]
    [InlineData("AI_SERVICE_READY port=abc", null)]
    [InlineData(null, null)]
    public void ReadyLineIsParsedStrictly(string? line, int? port) =>
        Assert.Equal(port, AiServiceProcess.ParseReadyLine(line));

    [Fact]
    public async Task StartsWaitsForTheReadyLineAndStopsWithoutOrphans()
    {
        // Stand-in for the service: some log output, then the ready line, then it just waits.
        using var service = AiServiceProcess.Start(
            Python,
            ["-c", "import sys, time; print('starting'); print('AI_SERVICE_READY port=4321', flush=True); time.sleep(120)"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(30));

        Assert.Equal(4321, service.Port);
        Assert.Equal(64, service.Token.Length);
        Assert.True(service.IsRunning);

        // No /shutdown endpoint here: StopAsync falls back to killing after the timeout.
        await service.StopAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void TokenGoesIntoTheEnvironmentNotTheCommandLine()
    {
        using var service = AiServiceProcess.Start(
            Python,
            ["-c", "import os, sys, time; print('AI_SERVICE_READY port=' + ('7' if len(os.environ.get('GW_AI_TOKEN', '')) == 64 and os.environ['GW_AI_TOKEN'] not in ' '.join(sys.argv) else '9'), flush=True); time.sleep(60)"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(30));
        Assert.Equal(7, service.Port);
    }

    [Fact]
    public void ServiceThatEndsBeforeItsReadyLineReportsItsError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AiServiceProcess.Start(
            Python,
            ["-c", "import sys; sys.stderr.write('error: unrecognized arguments: --parent-pid\\n'); sys.exit(2)"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(30)));
        Assert.Contains("exited before its ready line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingReadyLineIsATimeoutAndTheProcessIsGone()
    {
        var ex = Assert.Throws<TimeoutException>(() => AiServiceProcess.Start(
            Python,
            ["-c", "import time; time.sleep(60)"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(2)));
        Assert.Contains("did not announce", ex.Message, StringComparison.Ordinal);
    }
}
