namespace Bloom.Notification.Tests;

public static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> condition, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out after {seconds} s waiting for: {what}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
