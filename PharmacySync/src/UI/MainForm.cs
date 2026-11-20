using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PharmacySync.UI
{
    public class MainForm : Form
    {
        private readonly FlowLayoutPanel _list;
        private readonly Panel _empty;
        private readonly Panel _banner;
        private readonly Label _statusText;
        private readonly System.Windows.Forms.Timer _refreshTimer;

        public MainForm()
        {
            Theme.ApplyForm(this);
            Text = AppInfo.Name + " — ربط برامج الصيدليات مع المتجر الإلكتروني";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1160, 760);
            MinimumSize = new Size(940, 560);

            // ---------- header ----------
            var header = new HeaderBar { Height = 76 };
            var logo = new PictureBox { Image = AppIcon.Draw(44), SizeMode = PictureBoxSizeMode.CenterImage, Width = 64, Dock = DockStyle.Right, BackColor = Color.Transparent };
            var titles = new Panel { Dock = DockStyle.Right, Width = 520, BackColor = Color.Transparent };
            titles.Controls.Add(new Label { Text = "ربط برامج الصيدليات مع المتجر الإلكتروني", Font = Theme.Small, ForeColor = Color.FromArgb(200, 235, 230), BackColor = Color.Transparent, Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.TopLeft });
            titles.Controls.Add(new Label { Text = AppInfo.Name, Font = Theme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.BottomLeft });
            var actions = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 480, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(14, 19, 8, 0), BackColor = Color.Transparent, RightToLeft = RightToLeft.No };
            var add = new FlatButton("+  ربط عميل جديد", ButtonKind.Secondary) { Height = 38 };
            add.AutoSizeToText();
            var refresh = new FlatButton("تحديث", ButtonKind.Ghost) { Height = 38 };
            refresh.AutoSizeToText();
            var dataFolder = new FlatButton("مجلد البيانات", ButtonKind.Ghost) { Height = 38 };
            dataFolder.AutoSizeToText();
            actions.Controls.AddRange(new Control[] { add, refresh, dataFolder });
            header.Controls.Add(titles);
            header.Controls.Add(logo);
            header.Controls.Add(actions);

            // ---------- legacy task banner ----------
            _banner = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.WarningSoft, Visible = false, Padding = new Padding(16, 8, 16, 8) };
            var bannerText = new Label
            {
                Text = "⚠ يوجد على هذا الجهاز مهمة مجدولة قديمة من السكربت (PharmacyInventorySync). لو سبتها مع البرنامج البيانات هتتبعت مرتين.",
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(146, 64, 14), TextAlign = ContentAlignment.MiddleLeft, Font = Theme.BodyBold, BackColor = Color.Transparent
            };
            var removeLegacy = new FlatButton("حذف المهمة القديمة", ButtonKind.Danger) { Dock = DockStyle.Left };
            removeLegacy.AutoSizeToText();
            _banner.Controls.Add(bannerText);
            _banner.Controls.Add(removeLegacy);

            // ---------- list ----------
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(20, 16, 20, 16), BackColor = Theme.Background, RightToLeft = RightToLeft.No
            };
            _list.Resize += (s, e) => ResizeCards();

            // ---------- empty state ----------
            _empty = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
            var emptyIcon = new PictureBox { Image = AppIcon.Draw(72), SizeMode = PictureBoxSizeMode.CenterImage, Size = new Size(96, 96), BackColor = Color.Transparent };
            var emptyTitle = new Label { Text = "لا يوجد أي ربط على هذا الجهاز حتى الآن", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = false, Size = new Size(700, 40), TextAlign = ContentAlignment.MiddleCenter };
            var emptySub = new Label { Text = "اضغط (ربط عميل جديد) واختر البرنامج ونوع الربط وأدخل البيانات — والباقي هيتظبط تلقائياً.", Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = false, Size = new Size(700, 28), TextAlign = ContentAlignment.MiddleCenter };
            var emptyBtn = new FlatButton("+  ربط عميل جديد", ButtonKind.Primary) { Height = 44, Width = 220 };
            _empty.Controls.AddRange(new Control[] { emptyIcon, emptyTitle, emptySub, emptyBtn });
            _empty.Resize += (s, e) =>
            {
                int cx = _empty.Width / 2, top = Math.Max(30, _empty.Height / 2 - 140);
                emptyIcon.Location = new Point(cx - 48, top);
                emptyTitle.Location = new Point(cx - 350, top + 110);
                emptySub.Location = new Point(cx - 350, top + 152);
                emptyBtn.Location = new Point(cx - 110, top + 200);
            };

            // ---------- status bar ----------
            var status = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Surface, Padding = new Padding(14, 0, 14, 0) };
            _statusText = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
            var ver = new Label { Dock = DockStyle.Left, Width = 140, ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleRight, Text = "v" + AppInfo.Version, RightToLeft = RightToLeft.No };
            status.Controls.Add(_statusText);
            status.Controls.Add(ver);

            Controls.Add(_list);
            Controls.Add(_empty);
            Controls.Add(_banner);
            Controls.Add(header);
            Controls.Add(status);

            add.Click += (s, e) => AddConnection();
            emptyBtn.Click += (s, e) => AddConnection();
            refresh.Click += (s, e) => ReloadAll();
            dataFolder.Click += (s, e) => { Storage.EnsureDataRoot(); Process.Start("explorer.exe", "\"" + Storage.DataRoot + "\""); };
            removeLegacy.Click += (s, e) => RemoveLegacy();

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _refreshTimer.Tick += (s, e) => { foreach (Control c in _list.Controls) { var card = c as ConnectionCard; if (card != null) card.RefreshStatus(); } };
            Shown += (s, e) => { ReloadAll(); _refreshTimer.Start(); };
        }

        private void ResizeCards()
        {
            var w = _list.ClientSize.Width - _list.Padding.Horizontal - 8;
            foreach (Control c in _list.Controls) c.Width = Math.Max(600, w);
        }

        public void ReloadAll()
        {
            List<ConnectionProfile> profiles;
            try { profiles = Storage.LoadProfiles(); }
            catch (Exception ex) { Ui.Error(this, "تعذر قراءة الإعدادات:\n" + ex.Message); profiles = new List<ConnectionProfile>(); }

            _list.SuspendLayout();
            while (_list.Controls.Count > 0) { var c = _list.Controls[0]; _list.Controls.RemoveAt(0); c.Dispose(); }
            foreach (var p in profiles)
            {
                var card = new ConnectionCard(p, this);
                _list.Controls.Add(card);
            }
            _list.ResumeLayout();
            ResizeCards();

            _empty.Visible = profiles.Count == 0;
            _list.Visible = profiles.Count > 0;
            _banner.Visible = TaskService.LegacyTaskExists();

            _statusText.Text = "عدد الربطات: " + profiles.Count + "   •   البيانات: " + Ui.Ltr(Storage.DataRoot) +
                (AppInstaller.IsRunningFromInstallDir ? "   •   البرنامج مثبت" : "   •   سيتم تثبيت البرنامج في " + Ui.Ltr(AppInstaller.InstallDir) + " عند تفعيل الجدولة");
        }

        private void AddConnection()
        {
            using (var f = new EditorForm(null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                ReloadAll();
                if (f.RunAfterSave) RunSync(f.Result, false);
            }
        }

        public void EditConnection(ConnectionProfile p)
        {
            using (var f = new EditorForm(p))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                ReloadAll();
                if (f.RunAfterSave) RunSync(f.Result, false);
            }
        }

        public void RunSync(ConnectionProfile p, bool dryRun)
        {
            using (var f = new RunForm(p, dryRun)) f.ShowDialog(this);
            ReloadAll();
        }

        public void DeleteConnection(ConnectionProfile p)
        {
            if (!Ui.Ask(this, "حذف الربط \"" + p.Name + "\"؟\nسيتم حذف الإعدادات والسجلات وإيقاف المزامنة التلقائية لهذا العميل.", "حذف", true)) return;
            try
            {
                TaskService.Remove(p.Id);
                Storage.DeleteProfile(p.Id);
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تعذر الحذف:\n" + ex.Message);
            }
            ReloadAll();
        }

        public void ToggleSchedule(ConnectionProfile p)
        {
            try
            {
                var info = TaskService.Query(p.Id);
                if (info.Exists)
                {
                    if (!Ui.Ask(this, "إيقاف المزامنة التلقائية للعميل \"" + p.Name + "\"؟")) return;
                    TaskService.Remove(p.Id);
                    p.ScheduleEnabled = false;
                }
                else
                {
                    var exe = AppInstaller.EnsureInstalled();
                    TaskService.Install(p, exe);
                    p.ScheduleEnabled = true;
                }
                Storage.SaveProfile(p);
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تعذر تعديل الجدولة:\n" + ex.Message + "\n\nتأكد أن البرنامج يعمل كمسؤول.");
            }
            ReloadAll();
        }

        public void RunScheduledTaskNow(ConnectionProfile p)
        {
            try
            {
                TaskService.RunNow(p.Id);
                Ui.Info(this, "تم تشغيل المهمة المجدولة الآن بحساب SYSTEM (في الخلفية).\nالحالة هتتحدث في الكارت تلقائياً خلال ثواني، وتقدر تتابع من (السجل).");
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تعذر تشغيل المهمة:\n" + ex.Message);
            }
        }

        public void ShowLog(ConnectionProfile p)
        {
            using (var f = new LogViewerForm(p)) f.ShowDialog(this);
        }

        private void RemoveLegacy()
        {
            if (!Ui.Ask(this, "حذف المهمة المجدولة القديمة (PharmacyInventorySync) الخاصة بالسكربت؟\nالمزامنة هتكمل من البرنامج الجديد بعد ما تضيف الربط فيه.", "حذف", true)) return;
            try { TaskService.RemoveLegacyTask(); }
            catch (Exception ex) { Ui.Error(this, "تعذر حذف المهمة:\n" + ex.Message); }
            ReloadAll();
        }
    }

    /// <summary>One connection on the dashboard.</summary>
    public class ConnectionCard : Card
    {
        private readonly ConnectionProfile _p;
        private readonly MainForm _main;
        private readonly Label _schedLine, _lastLine;
        private readonly Pill _statusPill;
        private readonly FlatButton _toggle, _runTask;

        public ConnectionCard(ConnectionProfile p, MainForm main)
        {
            _p = p;
            _main = main;
            Height = 226;
            Margin = new Padding(0, 0, 0, 14);
            AccentColor = Theme.Primary;
            Padding = new Padding(18, 14, 22, 12);

            // Row 1: name + pills
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.RightToLeft, RightToLeft = RightToLeft.No, WrapContents = false, BackColor = Theme.Surface };
            var name = new Label { Text = p.Name, Font = Theme.Font(13.5f, FontStyle.Bold), AutoSize = true, ForeColor = Theme.Text, RightToLeft = RightToLeft.Yes, Margin = new Padding(3, 4, 8, 0) };
            var soft = SoftwareInfo.Find(p.Software);
            var softPill = new Pill(soft != null ? soft.Title : p.Software, Theme.PrimarySoft, Theme.PrimaryDark);
            var modePill = new Pill(SyncModes.Title(p.Mode), Theme.InfoSoft, Theme.Info);
            _statusPill = new Pill("...", Theme.Background, Theme.Muted);
            row1.Controls.AddRange(new Control[] { name, softPill, modePill, _statusPill });

            // Row 2: details
            var details = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 2, RowCount = 3, RightToLeft = RightToLeft.Yes, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            string host = p.ApiUrl;
            try { host = new Uri(p.ApiUrl).Host; } catch { }
            details.Controls.Add(Detail("قاعدة البيانات:", p.SqlServer + " / " + p.Database + (p.UsesWindowsAuth ? "  (Windows Auth)" : "  (user: " + p.Username + ")")), 0, 0);
            details.Controls.Add(Detail("المتجر:", host), 1, 0);
            _schedLine = DetailValue("");
            _lastLine = DetailValue("");
            details.Controls.Add(Wrap("الجدولة:", _schedLine), 0, 1);
            var lastWrap = Wrap("آخر مزامنة:", _lastLine);
            details.Controls.Add(lastWrap, 0, 2);
            details.SetColumnSpan(lastWrap, 2);

            // Row 3: actions
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, RightToLeft = RightToLeft.No, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0) };
            var run = new FlatButton("▶  مزامنة الآن", ButtonKind.Primary);
            var dry = new FlatButton("تجربة بدون إرسال", ButtonKind.Secondary);
            _toggle = new FlatButton("تفعيل الجدولة", ButtonKind.Secondary);
            _runTask = new FlatButton("تشغيل المهمة المجدولة", ButtonKind.Secondary);
            var log = new FlatButton("السجل", ButtonKind.Secondary);
            var edit = new FlatButton("تعديل", ButtonKind.Secondary);
            var del = new FlatButton("حذف", ButtonKind.Danger);
            foreach (var b in new[] { run, dry, _toggle, _runTask, log, edit, del }) { b.AutoSizeToText(); b.Height = 36; }
            _toggle.Width = 140;
            actions.Controls.AddRange(new Control[] { run, dry, _toggle, _runTask, log, edit, del });

            Controls.Add(details);
            Controls.Add(row1);
            Controls.Add(actions);

            run.Click += (s, e) =>
            {
                if (Ui.Ask(main, "تشغيل مزامنة فعلية الآن وإرسال البيانات إلى المتجر؟\n" + p.Name)) main.RunSync(_p, false);
            };
            dry.Click += (s, e) => main.RunSync(_p, true);
            _toggle.Click += (s, e) => main.ToggleSchedule(_p);
            _runTask.Click += (s, e) => main.RunScheduledTaskNow(_p);
            log.Click += (s, e) => main.ShowLog(_p);
            edit.Click += (s, e) => main.EditConnection(_p);
            del.Click += (s, e) => main.DeleteConnection(_p);

            RefreshStatus();
        }

        private static Label DetailValue(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Body, RightToLeft = RightToLeft.Yes, Margin = new Padding(2, 4, 2, 0) };
        }

        private static Control Detail(string caption, string value)
        {
            var v = DetailValue(value);
            v.RightToLeft = RightToLeft.No; // technical values (server, host) are left-to-right
            return Wrap(caption, v);
        }

        private static Control Wrap(string caption, Label value)
        {
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, RightToLeft = RightToLeft.No, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            var c = new Label { Text = caption, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body, RightToLeft = RightToLeft.Yes, Margin = new Padding(2, 4, 2, 0) };
            f.Controls.Add(c);
            f.Controls.Add(value);
            return f;
        }

        public void RefreshStatus()
        {
            var task = TaskService.Query(_p.Id);
            var st = Storage.LoadStatus(_p.Id);

            if (task.Exists)
            {
                var text = "كل " + _p.SyncIntervalMinutes + " دقيقة";
                if (task.Running) text += "  •  تعمل الآن...";
                else if (task.NextRun.HasValue) text += "  •  القادمة: " + Ui.Ltr(task.NextRun.Value.ToString("yyyy-MM-dd HH:mm"));
                if (!task.Enabled) text += "  •  (المهمة معطلة في ويندوز)";
                _schedLine.Text = text;
                _schedLine.ForeColor = task.Enabled ? Theme.Text : Theme.Warning;
                _toggle.Text = "إيقاف الجدولة";
                _runTask.Enabled = true;
            }
            else
            {
                _schedLine.Text = "غير مفعلة";
                _schedLine.ForeColor = Theme.Warning;
                _toggle.Text = "تفعيل الجدولة";
                _runTask.Enabled = false;
            }

            if (st == null)
            {
                _lastLine.Text = "لم تتم أي مزامنة بعد";
                _lastLine.ForeColor = Theme.Muted;
                _statusPill.Set("لم تتم مزامنة", Theme.Background, Theme.Muted);
                AccentColor = Theme.Border;
            }
            else
            {
                var trig = st.Trigger == "Scheduled" ? "تلقائي" : "يدوي";
                _lastLine.Text = Ui.Ltr(st.FinishedAt) + " (" + trig + ")  —  " + st.Message;
                _lastLine.ForeColor = Theme.Text;
                switch (st.Result)
                {
                    case RunResults.Success: _statusPill.Set("✔ ناجحة", Theme.SuccessSoft, Theme.Success); AccentColor = Theme.Success; break;
                    case RunResults.Partial: _statusPill.Set("⚠ جزئية", Theme.WarningSoft, Theme.Warning); AccentColor = Theme.Warning; break;
                    case RunResults.Skipped: _statusPill.Set("⚠ تم التخطي", Theme.WarningSoft, Theme.Warning); AccentColor = Theme.Warning; break;
                    default: _statusPill.Set("✖ فشلت", Theme.DangerSoft, Theme.Danger); AccentColor = Theme.Danger; break;
                }
            }
            if (task.Running) _statusPill.Set("⟳ تعمل الآن", Theme.InfoSoft, Theme.Info);
            Invalidate();
        }
    }
}
