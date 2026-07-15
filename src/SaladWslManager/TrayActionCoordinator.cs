using System.Threading;

internal enum TrayActionDomain
{
    Settings,
    Salad
}

internal sealed class TrayActionCoordinator
{
    private int settingsRunning;
    private int saladRunning;

    public bool TryBegin(TrayActionDomain domain)
    {
        switch (domain)
        {
            case TrayActionDomain.Settings:
                return Interlocked.CompareExchange(ref settingsRunning, 1, 0) == 0;
            default:
                return Interlocked.CompareExchange(ref saladRunning, 1, 0) == 0;
        }
    }

    public void End(TrayActionDomain domain)
    {
        switch (domain)
        {
            case TrayActionDomain.Settings:
                Interlocked.Exchange(ref settingsRunning, 0);
                break;
            default:
                Interlocked.Exchange(ref saladRunning, 0);
                break;
        }
    }

    public bool IsRunning(TrayActionDomain domain)
    {
        switch (domain)
        {
            case TrayActionDomain.Settings:
                return Volatile.Read(ref settingsRunning) != 0;
            default:
                return Volatile.Read(ref saladRunning) != 0;
        }
    }
}
