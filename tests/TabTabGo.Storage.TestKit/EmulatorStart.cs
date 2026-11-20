namespace TabTabGo.Storage.TestKit;

/// <summary>
/// Starts an emulator container with a time limit. A Docker or Ryuk start that never completes used to hang the whole test
/// run (observed: a single test blocked for more than ten minutes); now it fails fast with a message that names the causes.
/// </summary>
public static class EmulatorStart
{
    /// <summary>Environment variable that overrides the start timeout, in seconds.</summary>
    public const string TimeoutVariable = "TTG_EMULATOR_START_TIMEOUT_SECONDS";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Runs <paramref name="start"/> and fails with an <see cref="InvalidOperationException"/> when it does not finish in time or throws.</summary>
    /// <param name="name">Emulator name for the message (for example "Azurite").</param>
    /// <param name="start">The start action; it receives a token that is cancelled at the deadline.</param>
    /// <param name="timeout">Time limit; defaults to <see cref="TimeoutVariable"/> or three minutes.</param>
    /// <returns>A task that completes when the emulator has started.</returns>
    public static async Task RunAsync(string name, Func<CancellationToken, Task> start, TimeSpan? timeout = null)
    {
        var limit = timeout ?? ParseTimeout(Environment.GetEnvironmentVariable(TimeoutVariable));
        using var cts = new CancellationTokenSource(limit);
        try
        {
            await start(cts.Token).WaitAsync(limit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"{name} did not start within {limit.TotalSeconds:0} s. Check that Docker is running and can pull the image; if the " +
                $"Ryuk container cannot start (CI runners do not need it), set TESTCONTAINERS_RYUK_DISABLED=true. " +
                $"Set {TimeoutVariable} to wait longer, or TTG_SKIP_EMULATORS=true to skip the emulator tests.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name} could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>Parses the timeout variable (seconds); anything that is not a positive number gives the three-minute default.</summary>
    /// <param name="value">The variable's value, or <c>null</c>.</param>
    /// <returns>The timeout.</returns>
    public static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;
}
