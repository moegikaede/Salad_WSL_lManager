using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

internal static partial class Program
{
    private static readonly System.Drawing.Color StatusWindowBackgroundColor = System.Drawing.Color.FromArgb(24, 27, 29);
    private static readonly System.Drawing.Color StatusWindowPanelColor = System.Drawing.Color.FromArgb(31, 36, 37);
    private static readonly System.Drawing.Color StatusWindowTextColor = System.Drawing.Color.FromArgb(224, 238, 218);
    private static readonly System.Drawing.Color StatusWindowMutedTextColor = System.Drawing.Color.FromArgb(166, 184, 158);
    private static readonly System.Drawing.Color StatusWindowAccentColor = System.Drawing.Color.FromArgb(140, 195, 42);
    private static readonly System.Drawing.Color StatusWindowGridColor = System.Drawing.Color.FromArgb(70, 78, 70);
    private static readonly System.Drawing.Color StatusWindowButtonColor = System.Drawing.Color.FromArgb(45, 53, 51);
    private static readonly System.Drawing.Color StatusWindowButtonBorderColor = System.Drawing.Color.FromArgb(116, 151, 55);
    private static readonly object CopyableStatusCellTag = new object();
    private static int statusWindowLocalLogRefreshRunning;
    private static System.Windows.Forms.Timer statusWindowLeftDragTimer;
    private static Control statusWindowLeftDragControl;
    private static System.Drawing.Point statusWindowLeftDragLastScreenPoint;
    private static bool statusWindowLeftDragPending;
    private static bool statusWindowLeftDragActive;

    private sealed class StatusWindowForm : Form
    {
        private const int WindowMessageMouseActivate = 0x0021;
        private const int MouseActivateAndEat = 2;

        protected override void WndProc(ref Message message)
        {
            // Consume only the click that activates a background status cell so inspecting
            // the window cannot overwrite the clipboard; active-window clicks remain normal.
            var consumeActivationClick =
                message.Msg == WindowMessageMouseActivate &&
                GetForegroundWindow() != Handle &&
                IsCursorOverCopyableStatusCell();

            base.WndProc(ref message);

            if (consumeActivationClick)
            {
                message.Result = new IntPtr(MouseActivateAndEat);
            }
        }

        private bool IsCursorOverCopyableStatusCell()
        {
            Control current = this;
            while (current != null)
            {
                if (ReferenceEquals(current.Tag, CopyableStatusCellTag))
                {
                    return true;
                }

                var cursorPoint = current.PointToClient(System.Windows.Forms.Cursor.Position);
                current = current.GetChildAtPoint(
                    cursorPoint,
                    GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled);
            }

            return false;
        }
    }

    private static void ShowStatusWindow()
    {
        if (ShouldPostToUi())
        {
            PostToUi(ShowStatusWindow);
            return;
        }

        if (statusWindow == null || statusWindow.IsDisposed)
        {
            CreateStatusWindow();
        }

        if (statusWindow.WindowState == FormWindowState.Minimized)
        {
            statusWindow.WindowState = FormWindowState.Normal;
        }

        QueueStatusWindowLocalLogRefresh();
        UpdateStatusWindowCells(lastStatus);
        statusWindow.Show();
        EnsureStatusWindowOnVisibleScreen();
        ForceStatusWindowToFront();
        StartStatusRefreshProgressTimer();
        statusWindow.BeginInvoke((Action)delegate
        {
            if (statusWindow == null || statusWindow.IsDisposed)
            {
                return;
            }

            statusWindow.WindowState = FormWindowState.Normal;
            EnsureStatusWindowOnVisibleScreen();
            earningsHistoryEnd = RoundUpToDay(DateTimeOffset.Now);
            UpdateStatusWindow(true);
            ForceStatusWindowToFront();
        });
        QueueEstimatedEarningsRefreshIfStaleForStatusWindow(IsSaladTrayProcessRunning());
        Log("status_window_shown visible=" + statusWindow.Visible + " state=" + statusWindow.WindowState + " bounds=" + statusWindow.Bounds + " handle=" + statusWindow.Handle);
    }

    private static void QueueStatusWindowLocalLogRefresh()
    {
        if (Interlocked.Exchange(ref statusWindowLocalLogRefreshRunning, 1) != 0)
        {
            return;
        }

        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var saladLog = GetRecentSaladLogSnapshot();
                var workload = GetRecentSaladWorkloadSnapshot(saladLog);
                var workloadTiming = GetRecentWorkloadTimingStatus(saladLog, workload);
                var pull = GetRecentPullHealthStatus(saladLog);

                PostToUi(delegate
                {
                    if (!lastAppStateSnapshotAvailable || statusWindow == null || statusWindow.IsDisposed)
                    {
                        return;
                    }

                    var workloadId = string.IsNullOrEmpty(workload.DisplayId) ? "?" : workload.DisplayId;
                    var refreshed = lastAppStateSnapshot.WithLocalLogState(
                        workload.State,
                        workloadId,
                        pull,
                        workloadTiming.RuntimeText,
                        workloadTiming.PastAverageText);
                    SetAppStateStatus(refreshed, currentStaticTrayIcon ?? GetApplicationWindowIcon());
                });
            }
            catch (Exception ex)
            {
                Log("status_window_local_log_refresh_error " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref statusWindowLocalLogRefreshRunning, 0);
            }
        });
    }

    private static void EnsureStatusWindowOnVisibleScreen()
    {
        if (statusWindow == null || statusWindow.IsDisposed)
        {
            return;
        }

        var bounds = statusWindow.Bounds;
        var visible = false;
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(bounds))
            {
                visible = true;
                break;
            }
        }
        if (visible && bounds.Width >= statusWindow.MinimumSize.Width && bounds.Height >= statusWindow.MinimumSize.Height)
        {
            return;
        }

        var area = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var width = Math.Max(statusWindow.Width, statusWindow.MinimumSize.Width);
        var height = Math.Max(statusWindow.Height, statusWindow.MinimumSize.Height);
        statusWindow.Bounds = new System.Drawing.Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            Math.Min(width, area.Width),
            Math.Min(height, area.Height));
    }

    private static void ForceStatusWindowToFront()
    {
        if (statusWindow == null || statusWindow.IsDisposed)
        {
            return;
        }

        var handle = statusWindow.Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, ShowWindowRestore);
            SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            SetForegroundWindow(handle);
        }

        statusWindow.Show();
        statusWindow.BringToFront();
        statusWindow.Activate();
        statusWindow.Invalidate(true);
        statusWindow.Refresh();
    }

    private static void CreateStatusWindow()
    {
        statusWindow = new StatusWindowForm();
        statusWindow.Text = "Salad WSL Manager";
        statusWindow.StartPosition = FormStartPosition.Manual;
        statusWindow.MinimumSize = new System.Drawing.Size(700, 420);
        statusWindow.Size = statusWindow.MinimumSize;
        RestoreStatusWindowBounds();
        statusWindow.ShowInTaskbar = true;
        statusWindow.Icon = GetApplicationWindowIcon();
        statusWindow.BackColor = StatusWindowBackgroundColor;
        statusWindow.ForeColor = StatusWindowTextColor;
        statusWindow.FormBorderStyle = FormBorderStyle.None;
        EnableDoubleBuffering(statusWindow);

        statusWindowHeader = new Panel();
        statusWindowHeader.Dock = DockStyle.Top;
        statusWindowHeader.Height = 150;
        statusWindowHeader.BackColor = StatusWindowPanelColor;
        statusWindowHeader.ForeColor = StatusWindowTextColor;
        EnableDoubleBuffering(statusWindowHeader);

        statusWindowTable = new TableLayoutPanel();
        statusWindowTable.Dock = DockStyle.Fill;
        EnableDoubleBuffering(statusWindowTable);
        statusWindowTable.Padding = new Padding(12, 10, 12, 6);
        statusWindowTable.ColumnCount = 3;
        statusWindowTable.RowCount = 4;
        statusWindowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statusWindowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statusWindowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        statusWindowTable.BackColor = StatusWindowPanelColor;
        statusWindowTable.ForeColor = StatusWindowTextColor;
        statusWindowTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0f));
        statusWindowTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0f));
        statusWindowTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0f));
        statusWindowTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25.0f));
        statusWindowCells = new Label[11];
        var cellPositions = new[]
        {
            new System.Drawing.Point(0, 0), new System.Drawing.Point(1, 0), new System.Drawing.Point(2, 0),
            new System.Drawing.Point(0, 1), new System.Drawing.Point(1, 1), new System.Drawing.Point(2, 1),
            new System.Drawing.Point(0, 2), new System.Drawing.Point(1, 2),
            new System.Drawing.Point(0, 3), new System.Drawing.Point(1, 3), new System.Drawing.Point(2, 3)
        };
        for (var i = 0; i < statusWindowCells.Length; i++)
        {
            var cell = new Label();
            cell.Dock = DockStyle.Fill;
            cell.AutoSize = false;
            cell.Font = new System.Drawing.Font("Consolas", 10.5f);
            cell.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            cell.Margin = new Padding(0, 0, 14, 0);
            cell.BackColor = StatusWindowPanelColor;
            cell.ForeColor = StatusWindowTextColor;
            cell.Cursor = Cursors.Hand;
            cell.Tag = CopyableStatusCellTag;
            cell.Click += delegate(object sender, EventArgs e)
            {
                CopyStatusWindowCellValue(sender as Label);
            };
            statusWindowCells[i] = cell;
            statusWindowTable.Controls.Add(cell, cellPositions[i].X, cellPositions[i].Y);
        }

        statusRefreshProgress = new ProgressBar();
        statusRefreshProgress.Minimum = 0;
        statusRefreshProgress.Maximum = 100;
        statusRefreshProgress.Value = 0;
        statusRefreshProgress.Style = ProgressBarStyle.Continuous;
        statusRefreshProgress.Width = 92;
        statusRefreshProgress.Height = 6;
        statusRefreshProgress.BackColor = StatusWindowPanelColor;
        statusRefreshProgress.ForeColor = StatusWindowAccentColor;
        statusRefreshProgress.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        LayoutStatusRefreshProgressBar();
        statusWindowHeader.Resize += delegate { LayoutStatusRefreshProgressBar(); };
        statusWindowHeader.Controls.Add(statusWindowTable);
        statusWindowHeader.Controls.Add(statusRefreshProgress);

        statusRefreshProgressTimer = new System.Windows.Forms.Timer();
        statusRefreshProgressTimer.Interval = 250;
        statusRefreshProgressTimer.Tick += delegate { UpdateStatusRefreshProgress(); };

        var toolbar = new TableLayoutPanel();
        toolbar.Dock = DockStyle.Top;
        toolbar.Height = 38;
        toolbar.Padding = new Padding(8, 4, 8, 4);
        toolbar.ColumnCount = 1;
        toolbar.RowCount = 1;
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f));
        toolbar.BackColor = StatusWindowBackgroundColor;
        toolbar.ForeColor = StatusWindowTextColor;

        var historyControls = new FlowLayoutPanel();
        historyControls.Dock = DockStyle.Fill;
        historyControls.Margin = new Padding(0);
        historyControls.Padding = new Padding(0);
        historyControls.FlowDirection = FlowDirection.LeftToRight;
        historyControls.WrapContents = false;
        historyControls.BackColor = StatusWindowBackgroundColor;
        historyControls.ForeColor = StatusWindowTextColor;

        var previousButton = CreateStatusWindowButton("< Day", 72);
        previousButton.Click += delegate
        {
            earningsHistoryEnd = EnsureHistoryEnd().AddHours(-24);
            UpdateEarningsHistoryChart();
        };

        var nextButton = CreateStatusWindowButton("Day >", 72);
        nextButton.Click += delegate
        {
            var next = EnsureHistoryEnd().AddHours(24);
            var latest = RoundUpToDay(DateTimeOffset.Now);
            earningsHistoryEnd = next > latest ? latest : next;
            UpdateEarningsHistoryChart();
        };

        var latestButton = CreateStatusWindowButton("Latest", 72);
        latestButton.Click += delegate
        {
            earningsHistoryEnd = RoundUpToDay(DateTimeOffset.Now);
            // Latest resets both navigation axes so the status and blue series follow the live workload again.
            selectedWorkloadHistoryKey = "";
            UpdateStatusWindowCells(lastAppStateSnapshotAvailable ? lastAppStateSnapshot.ToStatusString() : "");
            UpdateEarningsHistoryChart();
        };

        // Keep navigation out of the compact status cell so the workload ID remains readable.
        previousWorkloadButton = CreateStatusWindowButton("< WL", 58);
        previousWorkloadButton.Click += delegate { NavigateSelectedWorkload(-1); };
        nextWorkloadButton = CreateStatusWindowButton("WL >", 58);
        nextWorkloadButton.Click += delegate { NavigateSelectedWorkload(1); };

        earningsHistoryRangeLabel = new Label();
        earningsHistoryRangeLabel.AutoSize = true;
        earningsHistoryRangeLabel.Padding = new Padding(8, 7, 0, 0);
        earningsHistoryRangeLabel.ForeColor = StatusWindowMutedTextColor;
        earningsHistoryRangeLabel.BackColor = StatusWindowBackgroundColor;

        historyControls.Controls.Add(previousButton);
        historyControls.Controls.Add(nextButton);
        historyControls.Controls.Add(latestButton);
        historyControls.Controls.Add(previousWorkloadButton);
        historyControls.Controls.Add(nextWorkloadButton);
        historyControls.Controls.Add(earningsHistoryRangeLabel);
        // Repair remains diagnostic code only. Omitting its control also avoids
        // reserving an empty toolbar column and suppresses visible-only probes.
        toolbar.Controls.Add(historyControls, 0, 0);

        earningsHistoryChart = new Chart();
        earningsHistoryChart.Dock = DockStyle.Fill;
        earningsHistoryChart.BackColor = StatusWindowBackgroundColor;
        earningsHistoryChart.ForeColor = StatusWindowTextColor;
        EnableDoubleBuffering(earningsHistoryChart);
        var area = new ChartArea("Earnings");
        area.BackColor = StatusWindowBackgroundColor;
        area.AxisX.Interval = 1;
        area.AxisX.MajorGrid.Enabled = false;
        area.AxisX.LabelStyle.ForeColor = StatusWindowMutedTextColor;
        area.AxisX.LineColor = StatusWindowGridColor;
        area.AxisX.MajorTickMark.LineColor = StatusWindowGridColor;
        area.AxisY.Title = "USD / hour";
        area.AxisY.TitleForeColor = StatusWindowMutedTextColor;
        area.AxisY.LabelStyle.ForeColor = StatusWindowMutedTextColor;
        area.AxisY.LineColor = StatusWindowGridColor;
        area.AxisY.MajorGrid.LineColor = StatusWindowGridColor;
        area.AxisY.MajorTickMark.LineColor = StatusWindowGridColor;
        earningsHistoryChart.ChartAreas.Add(area);
        var series = new Series("Earnings");
        series.ChartType = SeriesChartType.StackedColumn;
        series.ChartArea = "Earnings";
        series.Color = StatusWindowAccentColor;
        earningsHistoryChart.Series.Add(series);
        var selectedSeries = new Series("Selected workload");
        selectedSeries.ChartType = SeriesChartType.StackedColumn;
        selectedSeries.ChartArea = "Earnings";
        selectedSeries.Color = System.Drawing.Color.FromArgb(67, 142, 219);
        earningsHistoryChart.Series.Add(selectedSeries);

        ApplyDarkTitleBar(statusWindow);

        statusWindow.Controls.Add(earningsHistoryChart);
        statusWindow.Controls.Add(toolbar);
        statusWindow.Controls.Add(statusWindowHeader);
        AttachStatusWindowLeftDrag(statusWindow);
        statusWindowLeftDragTimer = new System.Windows.Forms.Timer();
        statusWindowLeftDragTimer.Interval = 100;
        statusWindowLeftDragTimer.Tick += delegate { ActivateStatusWindowLeftDrag(); };

        statusWindow.Activated += delegate
        {
            statusWindowWasActive = true;
        };
        statusWindow.Deactivate += delegate
        {
            statusWindowWasActive = false;
            lastStatusWindowDeactivatedAt = DateTimeOffset.Now;
        };
        statusWindow.FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                SaveStatusWindowBounds();
                e.Cancel = true;
                StopStatusRefreshProgressTimer();
                statusWindow.Hide();
                statusWindowWasActive = false;
            }
        };
        statusWindow.Resize += delegate
        {
            if (statusWindow.WindowState == FormWindowState.Minimized)
            {
                StopStatusRefreshProgressTimer();
                statusWindow.Hide();
                statusWindow.WindowState = FormWindowState.Normal;
                statusWindowWasActive = false;
            }
        };
        statusWindow.ResizeEnd += delegate { SaveStatusWindowBounds(); };
    }

    private static void AttachStatusWindowLeftDrag(Control control)
    {
        if (control == null)
        {
            return;
        }

        control.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || statusWindow == null || statusWindow.IsDisposed)
            {
                return;
            }

            statusWindowLeftDragControl = sender as Control;
            if (statusWindowLeftDragControl == null)
            {
                return;
            }

            statusWindowLeftDragLastScreenPoint = statusWindowLeftDragControl.PointToScreen(e.Location);
            statusWindowLeftDragPending = true;
            statusWindowLeftDragActive = false;
            if (statusWindowLeftDragTimer != null)
            {
                statusWindowLeftDragTimer.Start();
            }
        };
        control.MouseMove += delegate(object sender, MouseEventArgs e)
        {
            if (!statusWindowLeftDragActive || statusWindowLeftDragControl == null ||
                (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
            {
                return;
            }

            var current = statusWindowLeftDragControl.PointToScreen(e.Location);
            var deltaX = current.X - statusWindowLeftDragLastScreenPoint.X;
            var deltaY = current.Y - statusWindowLeftDragLastScreenPoint.Y;
            if (deltaX != 0 || deltaY != 0)
            {
                statusWindow.Location = new System.Drawing.Point(
                    statusWindow.Left + deltaX,
                    statusWindow.Top + deltaY);
                statusWindowLeftDragLastScreenPoint = current;
            }
        };
        control.MouseUp += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                StopStatusWindowLeftDrag();
                SaveStatusWindowBounds();
            }
        };

        foreach (Control child in control.Controls)
        {
            AttachStatusWindowLeftDrag(child);
        }
    }

    private static void ActivateStatusWindowLeftDrag()
    {
        if (!statusWindowLeftDragPending || statusWindowLeftDragControl == null ||
            (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
        {
            StopStatusWindowLeftDrag();
            return;
        }

        statusWindowLeftDragPending = false;
        statusWindowLeftDragActive = true;
        statusWindowLeftDragControl.Capture = true;
        if (statusWindowLeftDragTimer != null)
        {
            statusWindowLeftDragTimer.Stop();
        }
    }

    private static void StopStatusWindowLeftDrag()
    {
        statusWindowLeftDragPending = false;
        statusWindowLeftDragActive = false;
        if (statusWindowLeftDragTimer != null)
        {
            statusWindowLeftDragTimer.Stop();
        }

        if (statusWindowLeftDragControl != null)
        {
            statusWindowLeftDragControl.Capture = false;
        }

        statusWindowLeftDragControl = null;
    }

    private static void RestoreStatusWindowBounds()
    {
        try
        {
            if (!File.Exists(StatusWindowBoundsPath))
            {
                return;
            }

            var values = File.ReadAllText(StatusWindowBoundsPath, Encoding.UTF8).Trim().Split(',');
            int x;
            int y;
            int width;
            int height;
            if (values.Length != 4 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            {
                return;
            }

            // MinimumSize and the visible-screen correction guard against stale monitor layouts.
            statusWindow.Bounds = new System.Drawing.Rectangle(
                x,
                y,
                Math.Max(width, statusWindow.MinimumSize.Width),
                Math.Max(height, statusWindow.MinimumSize.Height));
        }
        catch (Exception ex)
        {
            Log("status_window_bounds_restore_error " + ex.Message);
        }
    }

    private static void SaveStatusWindowBounds()
    {
        try
        {
            if (statusWindow == null || statusWindow.IsDisposed || statusWindow.WindowState != FormWindowState.Normal)
            {
                return;
            }

            Directory.CreateDirectory(AppDir);
            var bounds = statusWindow.Bounds;
            File.WriteAllText(
                StatusWindowBoundsPath,
                string.Join(",", new[]
                {
                    bounds.X.ToString(CultureInfo.InvariantCulture),
                    bounds.Y.ToString(CultureInfo.InvariantCulture),
                    bounds.Width.ToString(CultureInfo.InvariantCulture),
                    bounds.Height.ToString(CultureInfo.InvariantCulture)
                }),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("status_window_bounds_save_error " + ex.Message);
        }
    }

    private static Button CreateStatusWindowButton(string text, int width)
    {
        var button = new Button();
        button.Text = text;
        button.Width = width;
        button.Height = 28;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = StatusWindowButtonBorderColor;
        button.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(59, 71, 57);
        button.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(75, 91, 58);
        button.BackColor = StatusWindowButtonColor;
        button.ForeColor = StatusWindowTextColor;
        button.UseVisualStyleBackColor = false;
        return button;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, uint valueSize);

    private static void ApplyDarkTitleBar(Form form)
    {
        if (form == null)
        {
            return;
        }

        try
        {
            var enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private static void UpdateStatusWindow()
    {
        UpdateStatusWindow(false);
    }

    private static void UpdateStatusWindow(bool force)
    {
        if (statusWindow == null || statusWindow.IsDisposed)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!force &&
            lastStatusWindowRefreshAt != DateTimeOffset.MinValue &&
            now - lastStatusWindowRefreshAt < StatusWindowRefreshInterval)
        {
            return;
        }

        UpdateStatusWindowCells(lastStatus);
        lastStatusWindowRefreshAt = now;
        UpdateStatusRefreshProgress();

        if (now - lastStatusWindowChartUpdate >= TimeSpan.FromMinutes(5))
        {
            UpdateEarningsHistoryChart();
        }
    }

    private static void LayoutStatusRefreshProgressBar()
    {
        if (statusWindowHeader == null || statusRefreshProgress == null)
        {
            return;
        }

        statusRefreshProgress.Left = Math.Max(0, statusWindowHeader.ClientSize.Width - statusRefreshProgress.Width - 12);
        statusRefreshProgress.Top = 6;
    }

    private static void StartStatusRefreshProgressTimer()
    {
        if (statusRefreshProgressTimer != null)
        {
            statusRefreshProgressTimer.Start();
            UpdateStatusRefreshProgress();
        }
    }

    private static void StopStatusRefreshProgressTimer()
    {
        if (statusRefreshProgressTimer != null)
        {
            statusRefreshProgressTimer.Stop();
        }
    }

    private static void UpdateStatusRefreshProgress()
    {
        if (statusRefreshProgress == null || statusRefreshProgress.IsDisposed)
        {
            return;
        }

        if (lastStatusWindowRefreshAt == DateTimeOffset.MinValue)
        {
            statusRefreshProgress.Value = 0;
            return;
        }

        var elapsed = DateTimeOffset.Now - lastStatusWindowRefreshAt;
        var percent = (int)Math.Round(100.0 * elapsed.TotalMilliseconds / StatusWindowRefreshInterval.TotalMilliseconds);
        if (percent < statusRefreshProgress.Minimum)
        {
            percent = statusRefreshProgress.Minimum;
        }
        else if (percent > statusRefreshProgress.Maximum)
        {
            percent = statusRefreshProgress.Maximum;
        }

        if (statusRefreshProgress.Value != percent)
        {
            statusRefreshProgress.Value = percent;
        }
    }

    private static System.Drawing.Icon GetApplicationWindowIcon()
    {
        if (appWindowIcon != null)
        {
            return appWindowIcon;
        }

        try
        {
            appWindowIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex)
        {
            Log("app_window_icon_load_error " + ex.Message);
        }

        return appWindowIcon ?? System.Drawing.SystemIcons.Application;
    }

    private static void UpdateStatusWindowCells(string status)
    {
        if (statusWindowCells == null)
        {
            return;
        }

        System.Collections.Generic.Dictionary<string, string> values;
        if (lastAppStateSnapshotAvailable &&
            string.Equals(status, lastAppStateSnapshot.ToStatusString(), StringComparison.Ordinal))
        {
            values = lastAppStateSnapshot.ToStatusParts();
        }
        else
        {
            values = ParseStatusParts(status);
        }
        SetStatusWindowCell(0, values, "Salad");
        SetStatusWindowCell(1, values, "Bowl");
        SetStatusWindowCell(2, values, "WSL");
        SetStatusWindowCell(3, values, "State");
        SetStatusWindowCell(4, values, "Workload");
        UpdateWorkloadNavigationButtons();
        SetStatusWindowCell(5, values, "Pull");
        SetStatusWindowCell(6, values, "Runtime");
        SetStatusWindowCell(7, values, "Past avg");
        SetStatusWindowCell(8, values, "Est/hr");
        SetStatusWindowCell(9, values, "Last24h");
        SetStatusWindowCell(10, values, "Balance");
    }

    private static System.Collections.Generic.Dictionary<string, string> ParseStatusParts(string status)
    {
        var values = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Parse fallback text locally so the status window does not depend on
        // context-menu presentation code, which is intentionally command-only.
        var lines = (status ?? "")
            .Split(new[] { " | " }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim());
        foreach (var line in lines)
        {
            var index = line.IndexOf(':');
            if (index > 0)
            {
                var key = line.Substring(0, index).Trim();
                var value = line.Substring(index + 1).Trim();
                values[key] = value;
            }
            else if (line.StartsWith("Est/hr", StringComparison.OrdinalIgnoreCase))
            {
                values["Est/hr"] = line.Substring("Est/hr".Length).Trim();
            }
            else if (line.StartsWith("Pull", StringComparison.OrdinalIgnoreCase))
            {
                values["Pull"] = line.Substring("Pull".Length).Trim().TrimStart(':').Trim();
            }
        }

        return values;
    }

    private static void SetStatusWindowCell(
        int index,
        System.Collections.Generic.Dictionary<string, string> values,
        string key)
    {
        if (index < 0 || statusWindowCells == null || index >= statusWindowCells.Length)
        {
            return;
        }

        string value;
        if (!values.TryGetValue(key, out value) || string.IsNullOrEmpty(value))
        {
            value = "?";
        }

        if (string.Equals(key, "Workload", StringComparison.OrdinalIgnoreCase))
        {
            var selected = GetSelectedWorkloadHistoryRow();
            if (selected.HasValue)
            {
                value = FormatWorkloadHistoryDisplayId(selected.Value);
            }
        }

        var text = key + ": " + value;
        if (!string.Equals(statusWindowCells[index].Text, text, StringComparison.Ordinal))
        {
            statusWindowCells[index].Text = text;
        }
    }

    private static void CopyStatusWindowCellValue(Label cell)
    {
        if (cell == null || string.IsNullOrWhiteSpace(cell.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(GetCopyableStatusValue(cell.Text));
        }
        catch (Exception ex)
        {
            Log("copy_status_window_value_error " + ex.Message);
        }
    }

    private static string GetStatusLabelName(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "status";
        }

        var index = line.IndexOf(':');
        return index > 0 ? line.Substring(0, index).Trim() : "status";
    }

    private static string GetCopyableStatusValue(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "";
        }

        var index = line.IndexOf(':');
        if (index >= 0 && index + 1 < line.Length)
        {
            return line.Substring(index + 1).Trim();
        }

        return line.Trim();
    }
}
