using System;

internal enum PendingQuitPhase
{
    Idle,
    PauseRequested,
    WaitingForWorkloadStop,
    QuietWindow,
    Closing,
    Canceling,
    Failed
}

internal struct PendingQuitSnapshot
{
    public readonly PendingQuitPhase Phase;
    public readonly string WorkloadId;
    public readonly string InstanceId;
    public readonly string Reason;
    public readonly bool PauseUntilIdleRequested;
    public readonly DateTimeOffset RequestedAt;
    public readonly string Error;

    public PendingQuitSnapshot(
        PendingQuitPhase phase,
        string workloadId,
        string instanceId,
        string reason,
        bool pauseUntilIdleRequested,
        DateTimeOffset requestedAt,
        string error)
    {
        Phase = phase;
        WorkloadId = workloadId ?? "";
        InstanceId = instanceId ?? "";
        Reason = reason ?? "";
        PauseUntilIdleRequested = pauseUntilIdleRequested;
        RequestedAt = requestedAt;
        Error = error ?? "";
    }

    public bool IsActive
    {
        get { return Phase != PendingQuitPhase.Idle && Phase != PendingQuitPhase.Failed; }
    }

    public string DisplayPhase
    {
        get
        {
            switch (Phase)
            {
                case PendingQuitPhase.PauseRequested: return "Pause requested";
                case PendingQuitPhase.WaitingForWorkloadStop: return "Waiting for workload";
                case PendingQuitPhase.QuietWindow: return "Confirming idle";
                case PendingQuitPhase.Closing: return "Closing Salad";
                case PendingQuitPhase.Canceling: return "Canceling stop";
                case PendingQuitPhase.Failed: return "Stop failed";
                default: return "";
            }
        }
    }
}

internal sealed class PendingQuitSession
{
    private readonly object gate = new object();
    private PendingQuitSnapshot value = new PendingQuitSnapshot(
        PendingQuitPhase.Idle, "", "", "", false, DateTimeOffset.MinValue, "");

    public PendingQuitSnapshot Snapshot()
    {
        lock (gate)
        {
            return value;
        }
    }

    public void BeginPauseRequest(DateTimeOffset requestedAt)
    {
        lock (gate)
        {
            value = new PendingQuitSnapshot(
                PendingQuitPhase.PauseRequested, "", "", "", true, requestedAt, "");
        }
    }

    public void Reserve(string workloadId, string instanceId, string reason)
    {
        lock (gate)
        {
            value = new PendingQuitSnapshot(
                PendingQuitPhase.WaitingForWorkloadStop,
                workloadId,
                instanceId,
                reason,
                true,
                value.RequestedAt == DateTimeOffset.MinValue ? DateTimeOffset.Now : value.RequestedAt,
                "");
        }
    }

    public void Restore(PendingQuitSnapshot snapshot)
    {
        lock (gate)
        {
            // A restarted manager must revalidate every in-flight phase instead
            // of resuming midway through QuietWindow, Closing, or Canceling.
            var phase = PendingQuitPhase.WaitingForWorkloadStop;
            value = new PendingQuitSnapshot(
                phase,
                snapshot.WorkloadId,
                snapshot.InstanceId,
                snapshot.Reason,
                snapshot.PauseUntilIdleRequested,
                snapshot.RequestedAt,
                snapshot.Error);
        }
    }

    public bool TryEnterQuietWindow()
    {
        lock (gate)
        {
            if (value.Phase != PendingQuitPhase.WaitingForWorkloadStop)
            {
                return false;
            }

            value = WithPhase(value, PendingQuitPhase.QuietWindow, "");
            return true;
        }
    }

    public void ReturnToWaiting()
    {
        lock (gate)
        {
            if (value.Phase == PendingQuitPhase.QuietWindow || value.Phase == PendingQuitPhase.Closing)
            {
                value = WithPhase(value, PendingQuitPhase.WaitingForWorkloadStop, "");
            }
        }
    }

    public bool TryEnterClosing()
    {
        lock (gate)
        {
            if (value.Phase != PendingQuitPhase.PauseRequested &&
                value.Phase != PendingQuitPhase.WaitingForWorkloadStop &&
                value.Phase != PendingQuitPhase.QuietWindow)
            {
                return false;
            }

            value = WithPhase(value, PendingQuitPhase.Closing, "");
            return true;
        }
    }

    public bool TryBeginCancel()
    {
        lock (gate)
        {
            if (!value.IsActive || value.Phase == PendingQuitPhase.Closing)
            {
                return false;
            }

            value = WithPhase(value, PendingQuitPhase.Canceling, "");
            return true;
        }
    }

    public void Fail(string error)
    {
        lock (gate)
        {
            value = WithPhase(value, PendingQuitPhase.Failed, error);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            value = new PendingQuitSnapshot(
                PendingQuitPhase.Idle, "", "", "", false, DateTimeOffset.MinValue, "");
        }
    }

    private static PendingQuitSnapshot WithPhase(
        PendingQuitSnapshot snapshot,
        PendingQuitPhase phase,
        string error)
    {
        return new PendingQuitSnapshot(
            phase,
            snapshot.WorkloadId,
            snapshot.InstanceId,
            snapshot.Reason,
            snapshot.PauseUntilIdleRequested,
            snapshot.RequestedAt,
            error);
    }
}
