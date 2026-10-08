namespace Assister.Windows.App.Services;

internal static class ConversationIdlePolicy
{
    public static bool ShouldStartNew(bool hasMessages, DateTimeOffset? lastActivity, DateTimeOffset now, int timeoutSeconds)
        => hasMessages && timeoutSeconds > 0 && lastActivity is { } activity &&
            now - activity > TimeSpan.FromSeconds(timeoutSeconds);
}
