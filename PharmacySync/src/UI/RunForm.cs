using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PharmacySync.UI
{
    /// <summary>Dark console-like colored log box.</summary>
    public class LogBox : RichTextBox
    {
        public LogBox()
        {
            ReadOnly = true;
            BackColor = Color.FromArgb(17, 24, 39);
            ForeColor = Color.FromArgb(209, 213, 219);
            Font = Theme.Mono;
            BorderStyle = BorderStyle.None;
            RightToLeft = RightToLeft.No;
            WordWrap = false;
            DetectUrls = false;
            Dock = DockStyle.Fill;
            ScrollBars = RichTextBoxScrollBars.Both;
        }

        public static Color ColorFor(string line)
        {
            if (line.Contains("[ERROR]")) return Color.FromArgb(248, 113, 113);
            if (line.Contains("[WARNING]")) return Color.FromArgb(251, 191, 36);
            if (line.Contains("SUCCESS") || line.Contains("completed successfully")) return Color.FromArgb(74, 222, 128);
            return Color.FromArgb(209, 213, 219);
        }

        public void AppendLines(IList<string> lines)
        {
            if (lines.Count == 0) return;
            SuspendDrawing();
            foreach (var line in lines)
            {
                SelectionStart = TextLength;
                SelectionLength = 0;
                SelectionColor = ColorFor(line);
                AppendText(line + "\n");
            }
            ResumeDrawing();
            SelectionStart = TextLength;
            ScrollToCaret();
        }

        public void LoadLog(string path)
        {
            Clear();
            if (!File.Exists(path))
            {
                SelectionColor = Color.FromArgb(156, 163, 175);
                AppendText("(لا يوجد سجل بعد)\n");
                return;
            }
            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                lines = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
            var list = new List<string>();
            foreach (var l in lines) if (l.Length > 0) list.Add(l);
            AppendLines(list);
        }

        private const int WM_SETREDRAW = 0x0B;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        private void SuspendDrawing() { if (IsHandleCreated) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero); }
        private void ResumeDrawing() { if (IsHandleCreated) { SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero); Invalidate(); } }
    }

    /// <summary>Runs a sync (real or dry run) with a live log.</summary>
    public class RunForm : Form
    {
        private readonly ConnectionProfile _profile;
        private readonly bool _dryRun;
        private readonly LogBox _log;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly FlatButton _cancel, _close, _openLog;
        private readonly List<string> _pending = new List<string>();
        private readonly System.Windows.Forms.Timer _flush;
        private volatile bool _cancelRequested;
        private bool _running;
        public SyncOutcome Outcome { get; private set; }

        public RunForm(ConnectionProfile profile, bool dryRun)
        {
            _profile = profile;
            _dryRun = dryRun;
            Theme.ApplyForm(this);
            Text = (dryRun ? "تجربة بدون إرسال - " : "مزامنة - ") + profile.Name;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 640);
            MinimumSize = new Size(700, 420);

            var header = new HeaderBar { Height = 74 };
            var title = new Label
            {
                Text = (dryRun ? "تجربة بدون إرسال (قراءة البيانات فقط)" : "مزامنة فعلية مع المتجر") + " — " + profile.Name,
                Font = Theme.Subtitle, ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = false,
                Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 6, 16, 0)
            };
            _status = new Label
            {
                Text = "جاري التشغيل...", Font = Theme.Body, ForeColor = Color.FromArgb(210, 240, 236), BackColor = Color.Transparent,
                AutoSize = false, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 16, 0)
            };
            header.Controls.Add(_status);
            header.Controls.Add(title);

            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 4, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };

            _log = new LogBox();
            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Theme.Background };
            var logBorder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(17, 24, 39) };
            logBorder.Controls.Add(_log);
            logHost.Controls.Add(logBorder);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8), BackColor = Theme.Surface, RightToLeft = RightToLeft.No };
            _close = new FlatButton("إغلاق", ButtonKind.Secondary) { Enabled = false };
            _cancel = new FlatButton("إيقاف", ButtonKind.Danger);
            _openLog = new FlatButton("فتح ملف السجل", ButtonKind.Secondary);
            footer.Controls.AddRange(new Control[] { _close, _cancel, _openLog });

            Controls.Add(logHost);
            Controls.Add(_progress);
            Controls.Add(header);
            Controls.Add(footer);

            _close.Click += (s, e) => Close();
            _cancel.Click += (s, e) =>
            {
                if (!_running) return;
                if (Ui.Ask(this, "إيقاف المزامنة بعد الدفعة الحالية؟")) { _cancelRequested = true; _cancel.Enabled = false; _status.Text = "جاري الإيقاف..."; }
            };
            _openLog.Click += (s, e) =>
            {
                var f = _dryRun ? Storage.DryRunLogFile(_profile.Id) : Storage.SyncLogFile(_profile.Id);
                if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\"");
            };

            _flush = new System.Windows.Forms.Timer { Interval = 150 };
            _flush.Tick += (s, e) => FlushLines();
            Shown += (s, e) => Start();
            FormClosing += (s, e) =>
            {
                if (_running)
                {
                    e.Cancel = true;
                    Ui.Warn(this, "المزامنة ما زالت تعمل. استخدم زر (إيقاف) أولاً.");
                }
            };
        }

        private void FlushLines()
        {
            List<string> copy;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                copy = new List<string>(_pending);
                _pending.Clear();
            }
            _log.AppendLines(copy);
        }

        private void Start()
        {
            _running = true;
            _flush.Start();
            var opt = new SyncOptions { DryRun = _dryRun, Trigger = "Manual", CancelRequested = () => _cancelRequested };
            var t = new Thread(() =>
            {
                SyncOutcome outcome;
                try
                {
                    outcome = SyncRunner.Run(_profile, opt, (level, line) => { lock (_pending) _pending.Add(line); });
                }
                catch (Exception ex)
                {
                    outcome = new SyncOutcome { Result = RunResults.Failed, Message = ex.Message };
                    lock (_pending) _pending.Add("[ERROR] " + ex);
                }
                try { BeginInvoke(new Action(() => Finish(outcome))); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Finish(SyncOutcome outcome)
        {
            _running = false;
            FlushLines();
            _flush.Stop();
            Outcome = outcome;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            _cancel.Enabled = false;
            _close.Enabled = true;
            _close.Kind = ButtonKind.Primary;
            _close.Invalidate();
            _close.Focus();

            string prefix;
            switch (outcome.Result)
            {
                case RunResults.Success: prefix = "✔ "; break;
                case RunResults.Partial: prefix = "⚠ "; break;
                case RunResults.Skipped: prefix = "⚠ "; break;
                default: prefix = "✖ "; break;
            }
            _status.Text = prefix + outcome.Message;
            _status.ForeColor = Color.White;
            _status.Font = Theme.BodyBold;
        }
    }

    /// <summary>Shows saved log files of a connection.</summary>
    public class LogViewerForm : Form
    {
        private readonly ConnectionProfile _profile;
        private readonly LogBox _log;
        private readonly ComboBox _which;

        public LogViewerForm(ConnectionProfile profile)
        {
            _profile = profile;
            Theme.ApplyForm(this);
            Text = "السجل - " + profile.Name;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 640);
            MinimumSize = new Size(700, 400);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 9, 10, 6), BackColor = Theme.Surface, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes };
            _which = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Font = Theme.Body, Margin = new Padding(4, 6, 4, 4), RightToLeft = RightToLeft.Yes };
            _which.Items.AddRange(new object[] { "آخر مزامنة", "المزامنة السابقة", "آخر تجربة (بدون إرسال)" });
            _which.SelectedIndex = 0;
            var refresh = new FlatButton("تحديث", ButtonKind.Secondary);
            var notepad = new FlatButton("فتح في Notepad", ButtonKind.Secondary);
            var folder = new FlatButton("فتح مجلد السجلات", ButtonKind.Secondary);
            top.Controls.AddRange(new Control[] { Theme.Label("الملف:"), _which, refresh, notepad, folder });

            _log = new LogBox();
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(17, 24, 39) };
            host.Controls.Add(_log);
            Controls.Add(host);
            Controls.Add(top);

            _which.SelectedIndexChanged += (s, e) => Reload();
            refresh.Click += (s, e) => Reload();
            notepad.Click += (s, e) => { var f = CurrentFile(); if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\""); };
            folder.Click += (s, e) => { var d = Storage.LogDir(_profile.Id); Directory.CreateDirectory(d); Process.Start("explorer.exe", "\"" + d + "\""); };
            Shown += (s, e) => Reload();
        }

        private string CurrentFile()
        {
            switch (_which.SelectedIndex)
            {
                case 1: return Storage.PrevSyncLogFile(_profile.Id);
                case 2: return Storage.DryRunLogFile(_profile.Id);
                default: return Storage.SyncLogFile(_profile.Id);
            }
        }

        private void Reload()
        {
            try { _log.LoadLog(CurrentFile()); }
            catch (Exception ex) { _log.Clear(); _log.AppendText(ex.Message); }
        }
    }
}
