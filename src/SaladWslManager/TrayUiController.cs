using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

internal static partial class Program
{
    private static void InitializeTrayIcon()
    {
        var menu = new ContextMenuStrip();
        // Keep the platform menu font so DPI and accessibility scaling follow
        // the Windows Forms defaults instead of applying a fixed size offset.
        var startupItem = new ToolStripMenuItem("スタートアップ登録");
        startupItem.CheckOnClick = false;
        startupItem.Checked = IsStartupRegistered();
        startupItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Settings, "toggle_startup_registration", ToggleStartupRegistration);
        };
        startupRegistrationMenuItem = startupItem;
        menu.Items.Add(startupItem);
        // Runtime status belongs to the left-click window. Keeping the context
        // menu command-only avoids duplicate stale status and update work.
        menu.Items.Add(new ToolStripSeparator());

        var startSaladAppItem = new ToolStripMenuItem("Start Salad app");
        startSaladAppItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Salad, "start_salad_app", RequestStartSaladApp);
        };
        startSaladAppMenuItem = startSaladAppItem;
        menu.Items.Add(startSaladAppItem);

        var quitSaladAppItem = new ToolStripMenuItem("Stop Salad app");
        quitSaladAppItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Salad, "quit_salad_app", RequestQuitSaladApp);
        };
        quitSaladAppMenuItem = quitSaladAppItem;
        menu.Items.Add(quitSaladAppItem);
        menu.Items.Add(new ToolStripSeparator());

        var chopNowItem = new ToolStripMenuItem("Chop now");
        chopNowItem.CheckOnClick = false;
        chopNowItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Salad, "chop_now", RequestChopNow);
        };
        chopNowMenuItem = chopNowItem;
        menu.Items.Add(chopNowItem);

        var pauseUntilIdleItem = new ToolStripMenuItem("Pause until idle");
        pauseUntilIdleItem.CheckOnClick = false;
        pauseUntilIdleItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Salad, "pause_until_idle", RequestPauseUntilIdle);
        };
        pauseUntilIdleMenuItem = pauseUntilIdleItem;

        var pauseUntilSetTimeItem = new ToolStripMenuItem("Pause until a set time");
        pauseUntilSetTimeItem.CheckOnClick = false;
        pauseUntilSetTimeItem.Enabled = false;
        pauseUntilSetTimeItem.ToolTipText = "Use Salad app to choose a time";
        pauseUntilSetTimeMenuItem = pauseUntilSetTimeItem;

        var pauseIndefinitelyItem = new ToolStripMenuItem("Pause indefinitely");
        pauseIndefinitelyItem.CheckOnClick = false;
        pauseIndefinitelyItem.Click += delegate
        {
            BeginTrayAction(TrayActionDomain.Salad, "pause_indefinitely", RequestPauseIndefinitely);
        };
        pauseIndefinitelyMenuItem = pauseIndefinitelyItem;
        menu.Items.Add(new ToolStripSeparator());

        var openLogItem = new ToolStripMenuItem("Open log");
        openLogItem.Click += delegate
        {
            try
            {
                Process.Start("notepad.exe", GetReadableLogPath());
            }
            catch (Exception ex)
            {
                Log("open_log_error " + ex.Message);
            }
        };
        menu.Items.Add(openLogItem);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += delegate
        {
            Log("tray_exit_clicked");
            RequestManagerShutdown("tray_exit");
            Application.Exit();
        };
        menu.Items.Add(exitItem);
        menu.Opening += delegate
        {
            UpdateStartupRegistrationMenuCheck();
            UpdateTrayActionChecks(IsSaladTrayProcessRunning(), GetRecentSaladWorkloadState());
        };

        notifyIcon = new NotifyIcon();
        notifyIcon.Text = "SaladWslManager starting";
        currentStaticTrayIcon = GetApplicationWindowIcon();
        notifyIcon.Icon = currentStaticTrayIcon;
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.Visible = true;
        notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowStatusWindowFromTray();
            }
        };
        stoppedTrayIcon = CreateStopStateIcon(System.Drawing.Color.FromArgb(220, 35, 35));
        loggingSpinnerIcons = CreateSpinnerIcons(
            System.Drawing.Color.FromArgb(255, 70, 210, 70),
            System.Drawing.Color.FromArgb(255, 165, 255, 125));
        pullingSpinnerIcons = CreateSpinnerIcons(
            System.Drawing.Color.FromArgb(255, 45, 145, 220),
            System.Drawing.Color.FromArgb(255, 145, 210, 255));
        stopPendingSpinnerIcons = CreateSpinnerIcons(
            System.Drawing.Color.FromArgb(255, 245, 150, 25),
            System.Drawing.Color.FromArgb(255, 255, 215, 120));
        trayAnimationTimer = new System.Windows.Forms.Timer();
        trayAnimationTimer.Interval = 150;
        trayAnimationTimer.Tick += delegate { AdvanceTrayAnimationFrame(); };
    }

    private static void UpdateTrayActionChecks(bool saladRunning, string workloadState)
    {
        if (ShouldPostToUi())
        {
            PostToUi(delegate { UpdateTrayActionChecks(saladRunning, workloadState); });
            return;
        }

        var isPausedIndefinitely = IsSaladConfigFlagTrue("SALAD_CHOPPING_INDEFINITELY_PAUSED");
        var hasSetTimePause = false;
        var isChopNowSelected = saladRunning && !isPausedIndefinitely && !hasSetTimePause &&
            IsGpuWorkloadState(workloadState);
        var isPauseUntilIdleSelected = saladRunning && !isChopNowSelected && !isPausedIndefinitely && !hasSetTimePause;

        var saladActionRunning = trayActions.IsRunning(TrayActionDomain.Salad);
        var settingsActionRunning = trayActions.IsRunning(TrayActionDomain.Settings);

        if (startupRegistrationMenuItem != null)
        {
            startupRegistrationMenuItem.Enabled = !settingsActionRunning;
        }

        if (startSaladAppMenuItem != null)
        {
            startSaladAppMenuItem.Enabled = !saladRunning && !saladActionRunning;
        }

        if (quitSaladAppMenuItem != null)
        {
            quitSaladAppMenuItem.Checked = pendingQuitSession.Snapshot().IsActive;
            quitSaladAppMenuItem.Enabled = saladRunning && !saladActionRunning;
        }

        if (chopNowMenuItem != null)
        {
            chopNowMenuItem.Checked = isChopNowSelected;
            chopNowMenuItem.Enabled = saladRunning && !saladActionRunning;
        }

        if (pauseUntilIdleMenuItem != null)
        {
            pauseUntilIdleMenuItem.Checked = isPauseUntilIdleSelected;
            pauseUntilIdleMenuItem.Enabled = false;
        }

        if (pauseUntilSetTimeMenuItem != null)
        {
            pauseUntilSetTimeMenuItem.Checked = hasSetTimePause;
            pauseUntilSetTimeMenuItem.Enabled = false;
        }

        if (pauseIndefinitelyMenuItem != null)
        {
            pauseIndefinitelyMenuItem.Checked = saladRunning && isPausedIndefinitely;
            pauseIndefinitelyMenuItem.Enabled = false;
        }
    }

    private static void ShowStatusWindowFromTray()
    {
        if (ShouldPostToUi())
        {
            PostToUi(ShowStatusWindowFromTray);
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - lastStatusWindowTrayToggleAt < TimeSpan.FromMilliseconds(500))
        {
            Log("status_window_tray_toggle_ignored duplicate_event=true");
            return;
        }

        lastStatusWindowTrayToggleAt = now;

        if (statusWindow != null && !statusWindow.IsDisposed && statusWindow.Visible)
        {
            if (!ShouldHideStatusWindowFromTrayClick(now))
            {
                ForceStatusWindowToFront();
                Log("status_window_activated_from_tray");
                return;
            }

            StopStatusRefreshProgressTimer();
            statusWindow.Hide();
            statusWindowWasActive = false;
            Log("status_window_hidden_from_tray");
            return;
        }

        Log("status_window_show_from_tray");
        ShowStatusWindow();
    }

    private static bool ShouldHideStatusWindowFromTrayClick(DateTimeOffset now)
    {
        if (statusWindow == null || statusWindow.IsDisposed || !statusWindow.Visible)
        {
            return false;
        }

        if (GetForegroundWindow() == statusWindow.Handle || statusWindowWasActive)
        {
            return true;
        }

        return lastStatusWindowDeactivatedAt != DateTimeOffset.MinValue &&
            now - lastStatusWindowDeactivatedAt <= TimeSpan.FromMilliseconds(750);
    }

    private static bool BeginTrayAction(TrayActionDomain domain, string actionName, ThreadStart action)
    {
        if (!trayActions.TryBegin(domain))
        {
            SetTrayStatus(domain + " action already running", System.Drawing.SystemIcons.Warning);
            return false;
        }

        Log("tray_action_begin domain=" + domain + " action=" + actionName);
        UpdateTrayActionChecks(IsSaladTrayProcessRunning(), GetRecentSaladWorkloadState());
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log(actionName + "_action_error " + ex);
                SetTrayStatus(actionName + " failed", System.Drawing.SystemIcons.Error);
            }
            finally
            {
                trayActions.End(domain);
                PostToUi(delegate
                {
                    SafeTick();
                });
                Log("tray_action_end domain=" + domain + " action=" + actionName);
            }
        });
        return true;
    }

    private static void SetAppStateStatus(AppStateSnapshot snapshot, System.Drawing.Icon icon)
    {
        if (ShouldPostToUi())
        {
            PostToUi(delegate { SetAppStateStatus(snapshot, icon); });
            return;
        }

        lastAppStateSnapshot = snapshot;
        lastAppStateSnapshotAvailable = true;
        SetTrayStatus(snapshot.ToStatusString(), icon);
    }

    private static void SetTrayStatus(string status, System.Drawing.Icon icon)
    {
        if (ShouldPostToUi())
        {
            PostToUi(delegate { SetTrayStatus(status, icon); });
            return;
        }

        var previousStatus = lastStatus;
        lastStatus = status;
        if (notifyIcon == null)
        {
            return;
        }

        var text = "SaladWslManager - " + status;
        notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        currentStaticTrayIcon = icon;
        UpdateTrayAnimationTimer();
        notifyIcon.Icon = GetEffectiveTrayIcon();

        if (statusWindow != null && !statusWindow.IsDisposed && statusWindow.Visible)
        {
            UpdateStatusWindow(!string.Equals(previousStatus, status, StringComparison.Ordinal));
        }
    }

    private static void UpdateLoggingSpinnerState(bool saladRunning, string workloadState)
    {
        var healthy = saladRunning &&
            IsGpuWorkloadState(workloadState) &&
            lastSuccessfulEarningsLog != DateTimeOffset.MinValue &&
            DateTimeOffset.Now - lastSuccessfulEarningsLog <= LoggingHealthyIconGrace;
        SetLoggingSpinnerActive(healthy);
    }

    private static void UpdatePullingSpinnerState(bool saladRunning)
    {
        // Workload assigned also includes Starting and desired-state gaps.
        // The parsed Pull text is the narrow signal that an image transfer is
        // currently active, so only that state receives the blue animation.
        var active = saladRunning &&
            !string.IsNullOrEmpty(pullHealthText) &&
            pullHealthText.StartsWith("Pull: pulling", StringComparison.OrdinalIgnoreCase);
        SetPullingSpinnerActive(active);
    }

    private static void SetPullingSpinnerActive(bool active)
    {
        if (ShouldPostToUi())
        {
            PostToUi(delegate { SetPullingSpinnerActive(active); });
            return;
        }

        if (pullingSpinnerActive == active)
        {
            return;
        }

        pullingSpinnerActive = active;
        traySpinnerFrame = 0;
        UpdateTrayAnimationTimer();
        if (notifyIcon != null)
        {
            notifyIcon.Icon = GetEffectiveTrayIcon();
        }
    }

    private static void SetLoggingSpinnerActive(bool active)
    {
        if (ShouldPostToUi())
        {
            PostToUi(delegate { SetLoggingSpinnerActive(active); });
            return;
        }

        if (loggingSpinnerActive == active)
        {
            return;
        }

        loggingSpinnerActive = active;
        traySpinnerFrame = 0;

        UpdateTrayAnimationTimer();

        if (notifyIcon != null)
        {
            notifyIcon.Icon = GetEffectiveTrayIcon();
        }
    }

    private static void AdvanceTrayAnimationFrame()
    {
        if (!ShouldAnimateTrayIcon() || notifyIcon == null)
        {
            return;
        }

        traySpinnerFrame = (traySpinnerFrame + 1) % GetActiveSpinnerFrameCount();
        notifyIcon.Icon = GetEffectiveTrayIcon();
    }

    private static int GetActiveSpinnerFrameCount()
    {
        if (pendingQuitSession.Snapshot().IsActive && stopPendingSpinnerIcons != null && stopPendingSpinnerIcons.Length > 0)
        {
            return stopPendingSpinnerIcons.Length;
        }

        if (pullingSpinnerActive && pullingSpinnerIcons != null && pullingSpinnerIcons.Length > 0)
        {
            return pullingSpinnerIcons.Length;
        }

        return loggingSpinnerIcons != null && loggingSpinnerIcons.Length > 0
            ? loggingSpinnerIcons.Length
            : 1;
    }

    private static void UpdateTrayAnimationTimer()
    {
        if (trayAnimationTimer == null)
        {
            return;
        }

        if (ShouldAnimateTrayIcon())
        {
            trayAnimationTimer.Start();
        }
        else
        {
            trayAnimationTimer.Stop();
        }
    }

    private static bool ShouldAnimateTrayIcon()
    {
        return pendingQuitSession.Snapshot().IsActive ||
            (pullingSpinnerActive && pullingSpinnerIcons != null && pullingSpinnerIcons.Length > 0) ||
            (loggingSpinnerActive && loggingSpinnerIcons != null && loggingSpinnerIcons.Length > 0);
    }

    private static System.Drawing.Icon GetEffectiveTrayIcon()
    {
        if (pendingQuitSession.Snapshot().IsActive && stopPendingSpinnerIcons != null && stopPendingSpinnerIcons.Length > 0)
        {
            return stopPendingSpinnerIcons[traySpinnerFrame % stopPendingSpinnerIcons.Length];
        }

        if (!traySaladRunning && stoppedTrayIcon != null)
        {
            return stoppedTrayIcon;
        }

        if (IsPullHealthWarning())
        {
            return System.Drawing.SystemIcons.Warning;
        }

        if (pullingSpinnerActive && pullingSpinnerIcons != null && pullingSpinnerIcons.Length > 0)
        {
            // Pulling is visually distinct from green healthy earnings logging;
            // pending quit still wins above as the user-requested stop state.
            return pullingSpinnerIcons[traySpinnerFrame % pullingSpinnerIcons.Length];
        }

        if (loggingSpinnerActive && loggingSpinnerIcons != null && loggingSpinnerIcons.Length > 0)
        {
            return loggingSpinnerIcons[traySpinnerFrame % loggingSpinnerIcons.Length];
        }

        return currentStaticTrayIcon ?? System.Drawing.SystemIcons.Application;
    }

    private static bool IsPullHealthWarning()
    {
        return !string.IsNullOrEmpty(pullHealthText) &&
            (pullHealthText.StartsWith("Pull: rollback", StringComparison.OrdinalIgnoreCase) ||
                pullHealthText.StartsWith("Pull: stuck", StringComparison.OrdinalIgnoreCase));
    }

    private static System.Drawing.Icon CreateStopStateIcon(System.Drawing.Color fillColor)
    {
        using (var bitmap = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var fill = new System.Drawing.SolidBrush(fillColor))
        using (var border = new System.Drawing.Pen(System.Drawing.Color.White, 1.4f))
        using (var mark = new System.Drawing.Pen(System.Drawing.Color.White, 2.0f))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillEllipse(fill, 1, 1, 14, 14);
            graphics.DrawEllipse(border, 1, 1, 14, 14);
            graphics.DrawLine(mark, 5, 5, 11, 11);
            graphics.DrawLine(mark, 11, 5, 5, 11);

            var handle = bitmap.GetHicon();
            try
            {
                using (var icon = System.Drawing.Icon.FromHandle(handle))
                {
                    return (System.Drawing.Icon)icon.Clone();
                }
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    private static System.Drawing.Icon[] CreateSpinnerIcons(System.Drawing.Color arcColor, System.Drawing.Color dotColor)
    {
        const int frameCount = 12;
        var icons = new System.Drawing.Icon[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            using (var bitmap = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var pen = new System.Drawing.Pen(arcColor, 2.4f))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

                var startAngle = frame * 30;
                graphics.DrawArc(pen, 3, 3, 10, 10, startAngle, 250);

                using (var dotBrush = new System.Drawing.SolidBrush(dotColor))
                {
                    var radians = startAngle * Math.PI / 180.0;
                    var x = 8.0 + Math.Cos(radians) * 5.0;
                    var y = 8.0 + Math.Sin(radians) * 5.0;
                    graphics.FillEllipse(dotBrush, (float)x - 1.5f, (float)y - 1.5f, 3.0f, 3.0f);
                }

                var handle = bitmap.GetHicon();
                try
                {
                    using (var icon = System.Drawing.Icon.FromHandle(handle))
                    {
                        icons[frame] = (System.Drawing.Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        return icons;
    }

    private static bool ShouldPostToUi()
    {
        return uiContext != null &&
            uiThreadId != 0 &&
            Thread.CurrentThread.ManagedThreadId != uiThreadId;
    }

    private static void PostToUi(Action action)
    {
        var context = uiContext;
        if (context != null)
        {
            context.Post(delegate { action(); }, null);
        }
        else
        {
            action();
        }
    }

    private static void CleanupTrayIcon()
    {
        if (pollTimer != null)
        {
            pollTimer.Stop();
            pollTimer.Dispose();
            pollTimer = null;
        }

        SetLoggingSpinnerActive(false);
        SetPullingSpinnerActive(false);

        if (trayAnimationTimer != null)
        {
            trayAnimationTimer.Stop();
            trayAnimationTimer.Dispose();
            trayAnimationTimer = null;
        }

        if (notifyIcon != null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }

        StopPendingQuitLogWatcher();
        StopIncrementalWorkloadTracker();

        if (statusWindow != null)
        {
            statusWindow.Dispose();
            statusWindow = null;
            statusWindowHeader = null;
            statusWindowTable = null;
            statusWindowCells = null;
            statusRefreshProgress = null;
            saladBowlRepairButton = null;
            if (saladBowlRepairToolTip != null)
            {
                saladBowlRepairToolTip.Dispose();
                saladBowlRepairToolTip = null;
            }
            if (statusRefreshProgressTimer != null)
            {
                statusRefreshProgressTimer.Stop();
                statusRefreshProgressTimer.Dispose();
                statusRefreshProgressTimer = null;
            }
            earningsHistoryChart = null;
            earningsHistoryRangeLabel = null;
        }

        if (loggingSpinnerIcons != null)
        {
            foreach (var icon in loggingSpinnerIcons)
            {
                if (icon != null)
                {
                    icon.Dispose();
                }
            }

            loggingSpinnerIcons = null;
        }

        if (stopPendingSpinnerIcons != null)
        {
            foreach (var icon in stopPendingSpinnerIcons)
            {
                if (icon != null)
                {
                    icon.Dispose();
                }
            }

            stopPendingSpinnerIcons = null;
        }

        if (pullingSpinnerIcons != null)
        {
            foreach (var icon in pullingSpinnerIcons)
            {
                if (icon != null)
                {
                    icon.Dispose();
                }
            }

            pullingSpinnerIcons = null;
        }

        if (stoppedTrayIcon != null)
        {
            stoppedTrayIcon.Dispose();
            stoppedTrayIcon = null;
        }

        if (appWindowIcon != null)
        {
            appWindowIcon.Dispose();
            appWindowIcon = null;
        }

        chopNowMenuItem = null;
        startupRegistrationMenuItem = null;
        startSaladAppMenuItem = null;
        quitSaladAppMenuItem = null;
        pauseUntilIdleMenuItem = null;
        pauseUntilSetTimeMenuItem = null;
        pauseIndefinitelyMenuItem = null;
    }
}
