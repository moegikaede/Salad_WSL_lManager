internal static partial class Program
{
    private static void RequestManagerShutdown(string reason)
    {
        if (managerShutdownRequested)
        {
            return;
        }

        // Stop new polling work before the WinForms loop unwinds so shutdown
        // remains deterministic and cannot race a final state refresh.
        managerShutdownRequested = true;
        if (pollTimer != null)
        {
            pollTimer.Stop();
        }

        Log("manager_shutdown_requested reason=" + reason);
    }
}
