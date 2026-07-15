using System;
using System.IO;

internal static class ReplayTests
{
    private static int failures;

    private static int Main(string[] args)
    {
        var fixtureDir = args.Length > 0 ? args[0] : "fixtures";
        AssertDecision(fixtureDir, "safe-matrix-before-stopped.txt", PendingQuitLogDecision.SafeToQuit);
        AssertDecision(fixtureDir, "safe-stopped-before-matrix.txt", PendingQuitLogDecision.SafeToQuit);
        AssertDecision(fixtureDir, "wait-missing-stopped.txt", PendingQuitLogDecision.KeepWaiting);
        AssertDecision(fixtureDir, "wait-resumed.txt", PendingQuitLogDecision.KeepWaiting);
        AssertDecision(fixtureDir, "wait-successor.txt", PendingQuitLogDecision.KeepWaiting);
        AssertDecision(fixtureDir, "wait-active-state-block.txt", PendingQuitLogDecision.KeepWaiting);
        TestPendingQuitSession();
        TestActionDomains();
        TestNavigationPolicy();

        Console.WriteLine(failures == 0 ? "PASS: all replay tests" : "FAIL: " + failures + " test(s)");
        return failures == 0 ? 0 : 1;
    }

    private static void AssertDecision(string fixtureDir, string name, PendingQuitLogDecision expected)
    {
        var lines = File.ReadAllLines(Path.Combine(fixtureDir, name));
        var actual = PendingQuitLogAnalyzer.Evaluate(lines, "a1b2c3d4", "");
        AssertEqual(name, expected, actual);
    }

    private static void TestPendingQuitSession()
    {
        var session = new PendingQuitSession();
        AssertEqual("session starts idle", PendingQuitPhase.Idle, session.Snapshot().Phase);
        session.BeginPauseRequest(DateTimeOffset.Parse("2026-07-15T05:48:08+09:00"));
        AssertEqual("session pause requested", PendingQuitPhase.PauseRequested, session.Snapshot().Phase);
        session.Reserve("a1b2c3d4", "55667788", "test");
        AssertEqual("session waits", PendingQuitPhase.WaitingForWorkloadStop, session.Snapshot().Phase);
        AssertTrue("session enters quiet", session.TryEnterQuietWindow());
        AssertEqual("session quiet", PendingQuitPhase.QuietWindow, session.Snapshot().Phase);
        AssertTrue("session enters closing", session.TryEnterClosing());
        AssertTrue("closing cannot cancel", !session.TryBeginCancel());
        session.ReturnToWaiting();
        AssertEqual("aborted close returns to waiting", PendingQuitPhase.WaitingForWorkloadStop, session.Snapshot().Phase);
        AssertTrue("waiting can cancel", session.TryBeginCancel());
        session.Clear();
        AssertEqual("session clears", PendingQuitPhase.Idle, session.Snapshot().Phase);

        session.Restore(new PendingQuitSnapshot(
            PendingQuitPhase.Closing,
            "a1b2c3d4",
            "55667788",
            "test",
            true,
            DateTimeOffset.Parse("2026-07-15T05:48:08+09:00"),
            ""));
        AssertEqual(
            "restart revalidates in-flight close",
            PendingQuitPhase.WaitingForWorkloadStop,
            session.Snapshot().Phase);
    }

    private static void TestActionDomains()
    {
        var actions = new TrayActionCoordinator();
        AssertTrue("begin Salad", actions.TryBegin(TrayActionDomain.Salad));
        AssertTrue("same Salad blocked", !actions.TryBegin(TrayActionDomain.Salad));
        AssertTrue("Settings remains independent", actions.TryBegin(TrayActionDomain.Settings));
        actions.End(TrayActionDomain.Salad);
        AssertTrue("Salad released", actions.TryBegin(TrayActionDomain.Salad));
    }

    private static void TestNavigationPolicy()
    {
        var live = WorkloadNavigationPolicy.Resolve(10, 8, 9, 1);
        AssertEqual("live target", 9, live.TargetIndex);
        AssertTrue("live mode", live.UseLiveMode);
        AssertTrue("live aligns date", live.AlignTargetRow);

        var virtualLive = WorkloadNavigationPolicy.Resolve(10, 9, -1, 1);
        AssertEqual("virtual target", 10, virtualLive.TargetIndex);
        AssertTrue("virtual live mode", virtualLive.UseLiveMode);
        AssertTrue("virtual has no row alignment", !virtualLive.AlignTargetRow);
    }

    private static void AssertTrue(string name, bool condition)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine("FAIL: " + name);
        }
    }

    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (!object.Equals(expected, actual))
        {
            failures++;
            Console.WriteLine("FAIL: " + name + " expected=" + expected + " actual=" + actual);
        }
    }
}
