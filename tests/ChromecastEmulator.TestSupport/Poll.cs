namespace ChromecastEmulator.TestSupport;

/// Generic condition polling for state that changes on another thread or process
/// (a spawned stub writing a file, a background continuation posting a command).
public static class Poll
{
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var step = interval ?? TimeSpan.FromMilliseconds(25);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(step);
        }

        return condition();
    }
}
