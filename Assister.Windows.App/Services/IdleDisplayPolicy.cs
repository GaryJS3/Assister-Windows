namespace Assister.Windows.App.Services;

internal static class IdleDisplayPolicy
{
    public static bool ShouldShow(TimeSpan idleTime, int timeoutSeconds, bool busy, bool hasDraft) =>
        timeoutSeconds > 0 && !busy && !hasDraft && idleTime >= TimeSpan.FromSeconds(timeoutSeconds);
}
