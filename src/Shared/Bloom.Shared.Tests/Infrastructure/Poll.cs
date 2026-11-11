namespace Bloom.Shared.Tests;

public static class Poll
{
    public static Task UntilAsync(Func<Task<bool>> condition, int seconds, string what) => UntilAsync(condition, seconds, () => what);

    /// <summary><paramref name="what"/> is read when the wait times out, so it can name what was still missing.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, int seconds, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out after {seconds} s waiting for: {what()}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public static Task UntilAsync(Func<bool> condition, int seconds, string what) =>
        UntilAsync(() => Task.FromResult(condition()), seconds, what);
}
