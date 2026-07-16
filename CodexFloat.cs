using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexFloatWindows
{
    internal sealed class QuotaSnapshot
    {
        public double? Remaining;
        public DateTime? ResetsAt;
        public string Plan;
        public int? ResetCredits;
        public string Error;
        public DateTime FetchedAt;
    }

    internal sealed class CodexQuotaReader
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public QuotaSnapshot Read()
        {
            Process process = null;
            try
            {
                process = StartCodex();
                Write(process, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"CodexFloatWindows\",\"version\":\"1.0.0\"}}}");
                WaitForResponse(process, 1, 12000);
                Write(process, "{\"method\":\"initialized\"}");
                Write(process, "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":null}");
                IDictionary response = WaitForResponse(process, 2, 12000);
                IDictionary result = GetDictionary(response, "result") ?? response;
                return Map(result);
            }
            catch (Exception ex)
            {
                return new QuotaSnapshot { Error = FriendlyError(ex), FetchedAt = DateTime.Now };
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                }
            }
        }

        private static Process StartCodex()
        {
            string executable = FindCodexExecutable();
            ProcessStartInfo start = new ProcessStartInfo(executable, "app-server --stdio");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.WorkingDirectory = Path.GetTempPath();
            ConfigureCodexHome(start);
            Process process = new Process();
            process.StartInfo = start;
            if (!process.Start()) throw new InvalidOperationException("无法启动 Codex CLI");
            return process;
        }

        private static void ConfigureCodexHome(ProcessStartInfo start)
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                start.EnvironmentVariables["CODEX_HOME"] = configured;
                return;
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string home = Path.Combine(profile, ".codex");
            // Codex Desktop normally signs in here. Setting it explicitly avoids
            // inheriting a service/sandbox profile when launched from another host.
            if (File.Exists(Path.Combine(home, "auth.json")))
                start.EnvironmentVariables["CODEX_HOME"] = home;
        }

        private static string FindCodexExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_FLOAT_CODEX_PATH");
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            // Codex Desktop keeps versioned CLI copies here. Pick the newest copy so
            // Microsoft Store/Desktop updates do not require reconfiguring this app.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string desktopBin = Path.Combine(local, "OpenAI", "Codex", "bin");
            string newest = FindNewestCodex(desktopBin);
            if (newest != null) return newest;

            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string raw in path.Split(Path.PathSeparator))
            {
                string directory = raw.Trim().Trim('"');
                if (directory.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(directory, "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            throw new System.ComponentModel.Win32Exception(2, "未找到 Codex CLI");
        }

        private static string FindNewestCodex(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                string selected = null;
                DateTime selectedTime = DateTime.MinValue;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    string candidate = Path.Combine(directory, "codex.exe");
                    if (!File.Exists(candidate)) continue;
                    DateTime time = File.GetLastWriteTimeUtc(candidate);
                    if (selected == null || time > selectedTime)
                    {
                        selected = candidate;
                        selectedTime = time;
                    }
                }
                return selected;
            }
            catch { return null; }
        }

        private static void Write(Process process, string line)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }

        private IDictionary WaitForResponse(Process process, int wantedId, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string line = ReadLineWithTimeout(process, Math.Max(100, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (line == null) throw new TimeoutException();
                object parsed;
                try { parsed = json.DeserializeObject(line); } catch { continue; }
                IDictionary message = parsed as IDictionary;
                if (message == null || !message.Contains("id")) continue;
                int id;
                if (!Int32.TryParse(Convert.ToString(message["id"], CultureInfo.InvariantCulture), out id) || id != wantedId) continue;
                if (message.Contains("error")) throw ProtocolException(message["error"] as IDictionary);
                return message;
            }
            throw new TimeoutException();
        }

        private static Exception ProtocolException(IDictionary error)
        {
            string message = GetString(error, "message") ?? "Codex 返回未知错误";
            string lower = message.ToLowerInvariant();
            if (lower.Contains("authentication required") || lower.Contains("not logged") || lower.Contains("unauthorized"))
                return new InvalidOperationException("需要先在 Codex Desktop 或 Codex CLI 中登录");
            if (lower.Contains("method not found") || lower.Contains("unsupported"))
                return new InvalidOperationException("当前 Codex 版本不支持额度读取，请更新 Codex");
            return new InvalidOperationException("Codex 读取失败：" + message);
        }

        private static string ReadLineWithTimeout(Process process, int timeoutMs)
        {
            string value = null;
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread thread = new Thread(delegate()
            {
                try { value = process.StandardOutput.ReadLine(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            thread.IsBackground = true;
            thread.Start();
            if (!done.WaitOne(timeoutMs)) return null;
            done.Dispose();
            if (error != null) throw error;
            return value;
        }

        private QuotaSnapshot Map(IDictionary root)
        {
            IDictionary selected = null;
            IDictionary byId = GetDictionary(root, "rateLimitsByLimitId");
            if (byId != null) selected = GetDictionary(byId, "codex");
            if (selected == null) selected = GetDictionary(root, "rateLimits");
            if (selected == null) throw new InvalidOperationException("当前 Codex 版本未返回额度数据");

            IDictionary weekly = FindWeekly(selected);
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.FetchedAt = DateTime.Now;
            snapshot.Plan = FormatPlan(GetString(selected, "planType"));
            if (weekly != null)
            {
                double used = GetDouble(weekly, "usedPercent") ?? 0;
                snapshot.Remaining = Math.Max(0, Math.Min(100, 100 - used));
                double? unix = GetDouble(weekly, "resetsAt");
                if (unix.HasValue) snapshot.ResetsAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix.Value).ToLocalTime();
            }
            IDictionary credits = GetDictionary(root, "rateLimitResetCredits");
            double? count = GetDouble(credits, "availableCount");
            if (count.HasValue && count >= 0 && count <= 100) snapshot.ResetCredits = (int)count.Value;
            if (!snapshot.Remaining.HasValue) snapshot.Error = "未找到每周额度窗口";
            return snapshot;
        }

        private static IDictionary FindWeekly(IDictionary selected)
        {
            IDictionary primary = GetDictionary(selected, "primary");
            IDictionary secondary = GetDictionary(selected, "secondary");
            if (GetDouble(primary, "windowDurationMins") == 10080) return primary;
            if (GetDouble(secondary, "windowDurationMins") == 10080) return secondary;
            return null;
        }

        private static IDictionary GetDictionary(IDictionary source, string key)
        {
            if (source == null || !source.Contains(key)) return null;
            return source[key] as IDictionary;
        }

        private static string GetString(IDictionary source, string key)
        {
            if (source == null || !source.Contains(key) || source[key] == null) return null;
            return Convert.ToString(source[key], CultureInfo.InvariantCulture);
        }

        private static double? GetDouble(IDictionary source, string key)
        {
            string text = GetString(source, key);
            double value;
            return Double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }

        private static string FormatPlan(string plan)
        {
            if (String.IsNullOrWhiteSpace(plan)) return "未知";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace("_", " ").ToLowerInvariant());
        }

        private static string FriendlyError(Exception ex)
        {
            if (ex is System.ComponentModel.Win32Exception) return "未找到 Codex CLI，请先安装并登录";
            if (ex is TimeoutException) return "读取超时，将自动重试";
            if (ex is UnauthorizedAccessException) return "无权访问 Codex 登录目录，请重新登录 Codex";
            return ex.Message;
        }
    }

    internal sealed class FloatForm : Form
    {
        private readonly Label percent = new Label();
        private readonly Label title = new Label();
        private readonly Label status = new Label();
        private readonly Label reset = new Label();
        private readonly Label plan = new Label();
        private readonly Label credits = new Label();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private QuotaSnapshot current = new QuotaSnapshot { Error = "正在读取额度…" };
        private bool expanded;
        private bool refreshing;
        private Point dragOrigin;
        private Point formOrigin;

        public FloatForm()
        {
            Text = "Codex Float for Windows";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(26, 28, 34);
            ForeColor = Color.White;
            Size = new Size(122, 44);
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 20, Screen.PrimaryScreen.WorkingArea.Top + 80);
            Padding = new Padding(12);
            Region = RoundedRegion(ClientRectangle, 18);

            title.Text = "CODEX FLOAT";
            title.Font = new Font("Segoe UI Semibold", 8f);
            title.ForeColor = Color.FromArgb(170, 178, 192);
            title.Location = new Point(18, 18);
            title.AutoSize = true;

            percent.Text = "…";
            percent.Font = new Font("Segoe UI Semibold", 15f);
            percent.TextAlign = ContentAlignment.MiddleCenter;
            percent.Dock = DockStyle.Fill;

            status.Font = new Font("Microsoft YaHei UI", 9f);
            status.ForeColor = Color.FromArgb(180, 188, 202);
            status.Location = new Point(18, 126);
            status.Size = new Size(304, 46);

            reset.Font = new Font("Microsoft YaHei UI", 10f);
            reset.Location = new Point(18, 194);
            reset.Size = new Size(304, 45);

            plan.Font = new Font("Microsoft YaHei UI", 10f);
            plan.Location = new Point(18, 252);
            plan.Size = new Size(304, 28);

            credits.Font = new Font("Microsoft YaHei UI", 10f);
            credits.Location = new Point(18, 292);
            credits.Size = new Size(304, 28);

            Controls.Add(percent);
            Controls.Add(title);
            Controls.Add(status);
            Controls.Add(reset);
            Controls.Add(plan);
            Controls.Add(credits);
            SetDetailVisibility(false);

            MouseDown += BeginDrag;
            MouseMove += ContinueDrag;
            percent.MouseDown += BeginDrag;
            percent.MouseMove += ContinueDrag;
            Click += ToggleExpanded;
            percent.Click += ToggleExpanded;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示悬浮窗", null, delegate { Show(); Activate(); });
            menu.Items.Add("立即刷新", null, delegate { RefreshQuota(); });
            menu.Items.Add("开机启动", null, ToggleStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { tray.Visible = false; Application.Exit(); });
            tray.Icon = MakeIcon();
            tray.Text = "Codex Float";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { if (Visible) Hide(); else Show(); };

            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { RefreshQuota(); };
            refreshTimer.Start();
            Shown += delegate { RefreshQuota(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!current.Remaining.HasValue || expanded) return;
            double ratio = current.Remaining.Value / 100.0;
            Color color = QuotaColor(current.Remaining.Value);
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(55, color)))
                e.Graphics.FillRectangle(fill, 0, 0, (int)(ClientSize.Width * ratio), ClientSize.Height);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0) Region = RoundedRegion(ClientRectangle, expanded ? 22 : 18);
        }

        private void ToggleExpanded(object sender, EventArgs e)
        {
            expanded = !expanded;
            SuspendLayout();
            Size = expanded ? new Size(340, 348) : new Size(122, 44);
            percent.Dock = expanded ? DockStyle.None : DockStyle.Fill;
            if (expanded)
            {
                percent.Location = new Point(18, 48);
                percent.Size = new Size(304, 66);
                percent.Font = new Font("Segoe UI Semibold", 36f);
            }
            else percent.Font = new Font("Segoe UI Semibold", 15f);
            SetDetailVisibility(expanded);
            ResumeLayout();
            UpdateView();
        }

        private void SetDetailVisibility(bool visible)
        {
            title.Visible = visible;
            status.Visible = visible;
            reset.Visible = visible;
            plan.Visible = visible;
            credits.Visible = visible;
        }

        private void RefreshQuota()
        {
            if (refreshing) return;
            refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                QuotaSnapshot snapshot = new CodexQuotaReader().Read();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        current = snapshot;
                        refreshing = false;
                        UpdateView();
                    });
                }
                catch { refreshing = false; }
            });
        }

        private void UpdateView()
        {
            percent.Text = current.Remaining.HasValue ? Math.Round(current.Remaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            percent.ForeColor = current.Remaining.HasValue ? QuotaColor(current.Remaining.Value) : Color.FromArgb(165, 174, 190);
            status.Text = current.Error == null ? "每周剩余额度 · 更新于 " + current.FetchedAt.ToString("HH:mm") : current.Error;
            reset.Text = "下次重置\n" + (current.ResetsAt.HasValue ? current.ResetsAt.Value.ToString("M 月 d 日 HH:mm") + "  ·  " + Relative(current.ResetsAt.Value) : "暂不可用");
            plan.Text = "当前套餐    " + (current.Plan ?? "未知");
            credits.Text = "重置机会    " + (current.ResetCredits.HasValue ? current.ResetCredits.Value + " 次可用" : "暂不可用");
            tray.Text = current.Remaining.HasValue ? "Codex Float · " + percent.Text + " 剩余" : "Codex Float · " + current.Error;
            Invalidate();
        }

        private static string Relative(DateTime date)
        {
            TimeSpan span = date - DateTime.Now;
            if (span.TotalMinutes <= 0) return "即将重置";
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + " 天 " + span.Hours + " 小时后";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + " 小时后";
            return Math.Max(1, (int)span.TotalMinutes) + " 分钟后";
        }

        private static Color QuotaColor(double remaining)
        {
            if (remaining > 50) return Color.FromArgb(70, 210, 142);
            if (remaining > 20) return Color.FromArgb(255, 172, 64);
            return Color.FromArgb(255, 92, 92);
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragOrigin = Cursor.Position;
            formOrigin = Location;
        }

        private void ContinueDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Point delta = new Point(Cursor.Position.X - dragOrigin.X, Cursor.Position.Y - dragOrigin.Y);
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) Location = new Point(formOrigin.X + delta.X, formOrigin.Y + delta.Y);
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyName, true))
            {
                if (key.GetValue("CodexFloatWindows") == null)
                    key.SetValue("CodexFloatWindows", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("CodexFloatWindows", false);
            }
        }

        private static Region RoundedRegion(Rectangle bounds, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return new Region(path);
        }

        private static Icon MakeIcon()
        {
            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(70, 210, 142))) g.FillEllipse(brush, 2, 2, 28, 28);
                using (Font font = new Font("Segoe UI", 15, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(20, 24, 28))) g.DrawString("C", font, brush, 7, 4);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }

    internal sealed class RefinedFloatForm : Form
    {
        private const string AppVersion = "v1.0.0";
        private const int WsExLayered = 0x00080000;
        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcAlpha = 0x01;

        private static readonly Color Ink = Color.FromArgb(22, 23, 29);
        private static readonly Color Surface = Color.FromArgb(28, 29, 36);
        private static readonly Color Border = Color.FromArgb(54, 56, 68);
        private static readonly Color BorderHover = Color.FromArgb(78, 81, 96);
        private static readonly Color TextPrimary = Color.FromArgb(239, 238, 232);
        private static readonly Color TextSecondary = Color.FromArgb(166, 167, 178);
        private static readonly Color TextTertiary = Color.FromArgb(112, 114, 128);
        private static readonly Color Healthy = Color.FromArgb(111, 206, 151);
        private static readonly Color Warning = Color.FromArgb(228, 176, 91);
        private static readonly Color Critical = Color.FromArgb(229, 111, 105);
        private static readonly Color Accent = Color.FromArgb(143, 151, 205);

        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer loadingTimer = new System.Windows.Forms.Timer();
        private QuotaSnapshot current = new QuotaSnapshot { Error = "正在读取本周额度…", FetchedAt = DateTime.Now };
        private bool expanded;
        private bool refreshing;
        private bool hovered;
        private bool dragging;
        private bool moved;
        private int loadingPhase;
        private Point dragOrigin;
        private Point formOrigin;

        public RefinedFloatForm()
        {
            Text = "Codex Float for Windows";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Ink;
            ForeColor = TextPrimary;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(154, 52);
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Left + (Screen.PrimaryScreen.WorkingArea.Width - Width) / 2, Screen.PrimaryScreen.WorkingArea.Top + 48);
            Cursor = Cursors.Hand;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.Font = new Font("Microsoft YaHei UI", 10f);
            menu.BackColor = Surface;
            menu.ForeColor = TextPrimary;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(6);
            ToolStripMenuItem versionItem = new ToolStripMenuItem("Codex Float  ·  " + AppVersion);
            versionItem.Enabled = false;
            menu.Items.Add(versionItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("在屏幕中央显示", null, delegate { ShowInCenter(); });
            menu.Items.Add("立即刷新", null, delegate { RefreshQuota(); });
            menu.Items.Add("开机启动", null, ToggleStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 Codex Float", null, delegate { tray.Visible = false; Application.Exit(); });
            foreach (ToolStripItem item in menu.Items)
            {
                item.ForeColor = item is ToolStripSeparator ? Border : TextPrimary;
                item.BackColor = Surface;
                if (!(item is ToolStripSeparator)) item.Padding = new Padding(8, 5, 8, 5);
            }

            tray.Icon = MakeIcon();
            tray.Text = "Codex Float · 正在读取";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowInCenter(); };
            tray.DoubleClick += delegate { ShowInCenter(); };

            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { RefreshQuota(); };
            refreshTimer.Start();

            loadingTimer.Interval = 120;
            loadingTimer.Tick += delegate { loadingPhase = (loadingPhase + 1) % 12; PresentSurface(); };
            loadingTimer.Start();

            Shown += delegate
            {
                CenterOnPrimary();
                PresentSurface();
                tray.ShowBalloonTip(2500, "Codex Float", "悬浮胶囊已显示在屏幕顶部中央。", ToolTipIcon.Info);
                RefreshQuota();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExLayered;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Bitmap bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                DrawSurface(g);
                UpdateLayeredSurface(bitmap);
                e.Graphics.DrawImageUnscaled(bitmap, 0, 0);
            }
        }

        private void PresentSurface()
        {
            if (!IsHandleCreated || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (Bitmap bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                DrawSurface(g);
                UpdateLayeredSurface(bitmap);
            }
        }

        private void CenterOnPrimary()
        {
            Rectangle working = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(working.Left + (working.Width - Width) / 2, working.Top + 48);
        }

        private void ShowInCenter()
        {
            CenterOnPrimary();
            Show();
            BringToFront();
            PresentSurface();
        }

        private void DrawSurface(Graphics g)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            int radius = expanded ? 24 : 25;
            Rectangle surfaceBounds = new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            using (System.Drawing.Drawing2D.GraphicsPath surfacePath = RoundedPath(surfaceBounds, radius))
            using (SolidBrush background = new SolidBrush(expanded ? Ink : Surface))
            {
                g.FillPath(background, surfacePath);
                if (!expanded && current.Remaining.HasValue)
                {
                    Color state = QuotaColor(current.Remaining.Value);
                    Color capacity = Blend(Surface, state, 0.38);
                    System.Drawing.Drawing2D.GraphicsState saved = g.Save();
                    g.SetClip(surfacePath);
                    int fillWidth = (int)Math.Round(surfaceBounds.Width * Math.Max(0, Math.Min(100, current.Remaining.Value)) / 100.0);
                    using (SolidBrush fill = new SolidBrush(capacity))
                        g.FillRectangle(fill, surfaceBounds.Left, surfaceBounds.Top, fillWidth, surfaceBounds.Height);
                    g.Restore(saved);
                }
            }

            using (Pen edge = new Pen(hovered ? BorderHover : Border, 1f))
                g.DrawPath(edge, RoundedPath(surfaceBounds, radius));

            if (expanded) DrawExpanded(g); else DrawCompact(g);
        }

        private void DrawCompact(Graphics g)
        {
            Color state = current.Remaining.HasValue ? QuotaColor(current.Remaining.Value) : Accent;
            DrawOrbitMark(g, new PointF(26, 26), state);

            string value = current.Remaining.HasValue ? Math.Round(current.Remaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            using (Font number = new Font("Bahnschrift", 26f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush text = new SolidBrush(TextPrimary))
                g.DrawString(value, number, text, new PointF(48, 13));

            Color dotColor = current.Error == null ? Blend(state, TextPrimary, 0.55) : TextTertiary;
            using (SolidBrush dot = new SolidBrush(dotColor))
                g.FillEllipse(dot, 132, 23, 7, 7);
        }

        private void DrawExpanded(Graphics g)
        {
            DrawHeader(g);
            if (!current.Remaining.HasValue)
            {
                DrawUnavailable(g);
                return;
            }

            Color state = QuotaColor(current.Remaining.Value);
            using (Font number = new Font("Bahnschrift", 64f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush primary = new SolidBrush(TextPrimary))
                g.DrawString(Math.Round(current.Remaining.Value).ToString("0", CultureInfo.InvariantCulture), number, primary, new PointF(22, 61));
            using (Font suffix = new Font("Bahnschrift", 20f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush secondary = new SolidBrush(TextSecondary))
                g.DrawString("%", suffix, secondary, new PointF(121, 92));

            DrawLabel(g, "本周剩余", 24, 137, TextSecondary);
            DrawCapacityRail(g, 24, 169, 324, current.Remaining.Value, state);
            DrawLabelRight(g, "更新于 " + current.FetchedAt.ToString("HH:mm"), 348, 181, TextTertiary);

            DrawDivider(g, 24, 211, 348);
            DrawDetailRow(g, "下次重置", ResetPrimary(), ResetSecondary(), 235);
            DrawDivider(g, 24, 290, 348);
            DrawDetailRow(g, "当前套餐", current.Plan ?? "暂不可用", null, 314);
            DrawDetailRow(g, "重置机会", current.ResetCredits.HasValue ? current.ResetCredits.Value + " 次可用" : "暂不可用", null, 354);

            using (SolidBrush dot = new SolidBrush(state)) g.FillEllipse(dot, 24, 401, 6, 6);
            DrawLabel(g, "每 60 秒自动刷新", 39, 395, TextTertiary);
        }

        private void DrawHeader(Graphics g)
        {
            DrawOrbitMark(g, new PointF(28, 30), Accent);
            using (Font label = new Font("Bahnschrift", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush text = new SolidBrush(TextSecondary))
                g.DrawString("CODEX  /  WEEKLY", label, text, new PointF(48, 22));

            Rectangle collapse = new Rectangle(ClientSize.Width - 50, 13, 36, 36);
            if (hovered && collapse.Contains(PointToClient(Cursor.Position)))
                using (SolidBrush hover = new SolidBrush(Surface)) g.FillEllipse(hover, collapse);
            using (Pen pen = new Pen(TextSecondary, 1.5f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pen, collapse.Left + 12, collapse.Top + 16, collapse.Left + 18, collapse.Top + 22);
                g.DrawLine(pen, collapse.Left + 18, collapse.Top + 22, collapse.Left + 24, collapse.Top + 16);
            }
        }

        private void DrawUnavailable(Graphics g)
        {
            int x = 24;
            int y = 92;
            if (refreshing)
            {
                DrawLoadingArc(g, new PointF(36, 108));
                using (Font heading = new Font("Microsoft YaHei UI", 20f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush primary = new SolidBrush(TextPrimary))
                    g.DrawString("正在读取本周额度", heading, primary, new PointF(62, 94));
            }
            else
            {
                using (SolidBrush state = new SolidBrush(Critical)) g.FillEllipse(state, x, y + 8, 8, 8);
                using (Font heading = new Font("Microsoft YaHei UI", 20f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush primary = new SolidBrush(TextPrimary))
                    g.DrawString("暂时无法读取额度", heading, primary, new PointF(46, y));
            }

            string message = current.Error ?? "正在连接 Codex…";
            using (Font body = new Font("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush secondary = new SolidBrush(TextSecondary))
            {
                RectangleF box = new RectangleF(x, 142, 324, 72);
                g.DrawString(message, body, secondary, box);
            }

            DrawDivider(g, 24, 234, 348);
            DrawLabel(g, "恢复方式", 24, 258, TextTertiary);
            using (Font body = new Font("Microsoft YaHei UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush primary = new SolidBrush(TextPrimary))
                g.DrawString("保持 Codex 已登录，工具将在后台自动重试。", body, primary, new RectangleF(24, 282, 324, 54));

            using (SolidBrush dot = new SolidBrush(Accent)) g.FillEllipse(dot, 24, 401, 6, 6);
            DrawLabel(g, "每 60 秒自动刷新", 39, 395, TextTertiary);
        }

        private static void DrawOrbitMark(Graphics g, PointF center, Color color)
        {
            using (Pen orbit = new Pen(Color.FromArgb(130, color), 1.5f))
                g.DrawEllipse(orbit, center.X - 9, center.Y - 6, 18, 12);
            using (SolidBrush core = new SolidBrush(color)) g.FillEllipse(core, center.X - 3, center.Y - 3, 6, 6);
            using (SolidBrush satellite = new SolidBrush(TextPrimary)) g.FillEllipse(satellite, center.X + 6, center.Y - 5, 3, 3);
        }

        private static void DrawCapacityRail(Graphics g, int left, int top, int width, double value, Color color)
        {
            using (Pen track = new Pen(Border, 4f))
            using (Pen fill = new Pen(color, 4f))
            {
                track.StartCap = track.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                fill.StartCap = fill.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(track, left, top, left + width, top);
                float end = left + (float)(width * Math.Max(0, Math.Min(100, value)) / 100.0);
                if (end > left) g.DrawLine(fill, left, top, end, top);
            }
        }

        private static void DrawDetailRow(Graphics g, string label, string primary, string secondary, int top)
        {
            DrawLabel(g, label, 24, top, TextTertiary);
            using (Font value = new Font("Microsoft YaHei UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush text = new SolidBrush(TextPrimary))
                g.DrawString(primary, value, text, new PointF(129, top - 4));
            if (!String.IsNullOrEmpty(secondary))
                DrawLabel(g, secondary, 129, top + 22, TextSecondary);
        }

        private static void DrawLabel(Graphics g, string text, int left, int top, Color color)
        {
            using (Font font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color)) g.DrawString(text, font, brush, new PointF(left, top));
        }

        private static void DrawLabelRight(Graphics g, string text, int right, int top, Color color)
        {
            using (Font font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Far;
                g.DrawString(text, font, brush, new PointF(right, top), format);
            }
        }

        private static void DrawDivider(Graphics g, int left, int top, int right)
        {
            using (Pen line = new Pen(Border, 1f)) g.DrawLine(line, left, top, right, top);
        }

        private void DrawLoadingArc(Graphics g, PointF center)
        {
            using (Pen track = new Pen(Border, 2f)) g.DrawEllipse(track, center.X - 10, center.Y - 10, 20, 20);
            using (Pen active = new Pen(Accent, 2f))
            {
                active.StartCap = active.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(active, center.X - 10, center.Y - 10, 20, 20, loadingPhase * 30, 90);
            }
        }

        private string ResetPrimary()
        {
            return current.ResetsAt.HasValue ? current.ResetsAt.Value.ToString("M 月 d 日  HH:mm") : "暂不可用";
        }

        private string ResetSecondary()
        {
            return current.ResetsAt.HasValue ? Relative(current.ResetsAt.Value) : null;
        }

        private void ToggleExpanded()
        {
            expanded = !expanded;
            ClientSize = expanded ? new Size(372, 432) : new Size(154, 52);
            Cursor = expanded ? Cursors.Default : Cursors.Hand;
            PresentSurface();
        }

        internal void RenderDesignPreview(string path, bool compact)
        {
            current = new QuotaSnapshot
            {
                Remaining = compact ? 95 : 68,
                Plan = "Plus",
                ResetsAt = DateTime.Now.AddDays(4).AddHours(7),
                ResetCredits = 2,
                FetchedAt = DateTime.Now
            };
            expanded = !compact;
            ClientSize = compact ? new Size(154, 52) : new Size(372, 432);
            using (Bitmap bitmap = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, ClientSize));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (expanded && CollapseBounds().Contains(e.Location)) { ToggleExpanded(); return; }
            dragging = true;
            moved = false;
            dragOrigin = Cursor.Position;
            formOrigin = Location;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point delta = new Point(Cursor.Position.X - dragOrigin.X, Cursor.Position.Y - dragOrigin.Y);
                if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
                {
                    moved = true;
                    Location = new Point(formOrigin.X + delta.X, formOrigin.Y + delta.Y);
                }
            }
            PresentSurface();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            Capture = false;
            if (!moved) ToggleExpanded();
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; PresentSurface(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; PresentSurface(); base.OnMouseLeave(e); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PresentSurface();
        }

        private Rectangle CollapseBounds() { return new Rectangle(ClientSize.Width - 50, 13, 36, 36); }

        private void RefreshQuota()
        {
            if (refreshing) return;
            refreshing = true;
            PresentSurface();
            ThreadPool.QueueUserWorkItem(delegate
            {
                QuotaSnapshot snapshot = new CodexQuotaReader().Read();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        current = snapshot;
                        refreshing = false;
                        tray.Text = current.Remaining.HasValue
                            ? "Codex Float · " + Math.Round(current.Remaining.Value).ToString("0") + "% 剩余"
                            : "Codex Float · 暂不可用";
                        PresentSurface();
                    });
                }
                catch { refreshing = false; }
            });
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyName, true))
            {
                if (key.GetValue("CodexFloatWindows") == null) key.SetValue("CodexFloatWindows", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("CodexFloatWindows", false);
            }
        }

        private static string Relative(DateTime date)
        {
            TimeSpan span = date - DateTime.Now;
            if (span.TotalMinutes <= 0) return "即将重置";
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + " 天 " + span.Hours + " 小时后";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + " 小时后";
            return Math.Max(1, (int)span.TotalMinutes) + " 分钟后";
        }

        private static Color QuotaColor(double remaining)
        {
            if (remaining > 50) return Healthy;
            if (remaining > 20) return Warning;
            return Critical;
        }

        private static Color Blend(Color background, Color foreground, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                255,
                (int)Math.Round(background.R + (foreground.R - background.R) * amount),
                (int)Math.Round(background.G + (foreground.G - background.G) * amount),
                (int)Math.Round(background.B + (foreground.B - background.B) * amount));
        }

        private void UpdateLayeredSurface(Bitmap bitmap)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memory, bitmapHandle);
                NativePoint destination = new NativePoint(Left, Top);
                NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
                NativePoint source = new NativePoint(0, 0);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;
                UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha);
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; public NativePoint(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int CX; public int CY; public NativeSize(int width, int height) { CX = width; CY = height; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, int flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);

        private static Icon MakeIcon()
        {
            Bitmap bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush surface = new SolidBrush(Surface)) g.FillEllipse(surface, 1, 1, 30, 30);
                using (Pen orbit = new Pen(Accent, 2f)) g.DrawEllipse(orbit, 7, 10, 18, 12);
                using (SolidBrush core = new SolidBrush(Accent)) g.FillEllipse(core, 13, 13, 6, 6);
                using (SolidBrush satellite = new SolidBrush(TextPrimary)) g.FillEllipse(satellite, 22, 10, 4, 4);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(43, 45, 55); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(43, 45, 55); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(43, 45, 55); } }
            public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(43, 45, 55); } }
            public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(43, 45, 55); } }
            public override Color MenuItemBorder { get { return Border; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && (args[0] == "--render-preview" || args[0] == "--render-compact"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (RefinedFloatForm preview = new RefinedFloatForm()) preview.RenderDesignPreview(args[1], args[0] == "--render-compact");
                return;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "CodexFloatWindows.SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RefinedFloatForm());
            }
        }
    }
}
