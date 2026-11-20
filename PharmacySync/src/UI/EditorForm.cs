using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PharmacySync.UI
{
    /// <summary>3-step wizard: program & mode → database → e-commerce API & schedule.</summary>
    public class EditorForm : Form
    {
        private readonly ConnectionProfile _p;
        private readonly bool _isNew;
        private int _step;

        private readonly Panel _body;
        private readonly Panel[] _pages = new Panel[3];
        private readonly StepIndicator _steps;
        private readonly FlatButton _back, _next, _save, _cancel, _import;

        // step 1
        private TextBox _name;
        private readonly List<OptionTile> _softTiles = new List<OptionTile>();
        private readonly List<OptionTile> _modeTiles = new List<OptionTile>();
        // step 2
        private ComboBox _server, _database;
        private RadioButton _winAuth, _sqlAuth;
        private TextBox _user, _pass;
        private Label _dbResult;
        private CheckBox _remoteBranches;
        private bool _remoteAllowed;
        private OptionTile _multiTile;
        private bool _dbTestedOk;
        // step 3
        private TextBox _apiUrl, _apiKey;
        private NumericUpDown _interval, _timeout;
        private CheckBox _schedule, _runAfter, _showKey;
        private Label _apiResult;

        public bool RunAfterSave { get; private set; }
        public ConnectionProfile Result { get { return _p; } }

        public EditorForm(ConnectionProfile profile)
        {
            _isNew = profile == null;
            _p = profile == null ? new ConnectionProfile() : profile.Clone();

            Theme.ApplyForm(this);
            Text = _isNew ? "ربط عميل جديد" : "تعديل الربط - " + _p.Name;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(840, 720);

            var header = new HeaderBar { Height = 64 };
            header.Controls.Add(new Label
            {
                Text = _isNew ? "ربط عميل جديد" : "تعديل الربط",
                Font = Theme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 20, 0)
            });

            _steps = new StepIndicator(new[] { "البرنامج ونوع الربط", "قاعدة البيانات", "المتجر والجدولة" }) { Dock = DockStyle.Top, Height = 64 };

            _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 8), BackColor = Theme.Background };

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Surface };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 420, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 11, 12, 8), RightToLeft = RightToLeft.No };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 360, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(12, 11, 12, 8), RightToLeft = RightToLeft.No };
            _save = new FlatButton("حفظ", ButtonKind.Success) { Width = 120 };
            _next = new FlatButton("التالي  ◄", ButtonKind.Primary) { Width = 120 };
            _back = new FlatButton("►  السابق", ButtonKind.Secondary) { Width = 110 };
            _cancel = new FlatButton("إلغاء", ButtonKind.Secondary);
            _import = new FlatButton("استيراد config.json قديم", ButtonKind.Secondary);
            // left side (RTL "forward"): [next|save] [back]
            left.Controls.AddRange(new Control[] { _next, _save, _back });
            right.Controls.AddRange(new Control[] { _cancel, _import });
            footer.Controls.Add(left);
            footer.Controls.Add(right);

            Controls.Add(_body);
            Controls.Add(_steps);
            Controls.Add(header);
            Controls.Add(footer);

            _pages[0] = BuildStep1();
            _pages[1] = BuildStep2();
            _pages[2] = BuildStep3();
            foreach (var pg in _pages) { pg.Dock = DockStyle.Fill; pg.Visible = false; _body.Controls.Add(pg); }

            _back.Click += (s, e) => Go(_step - 1);
            _next.Click += (s, e) => { if (ValidateStep(_step)) Go(_step + 1); };
            _save.Click += (s, e) => DoSave();
            _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _import.Click += (s, e) => ImportConfig();

            LoadFromProfile();
            Go(0);
        }

        // =================================================================
        // Steps
        // =================================================================

        private TableLayoutPanel Grid(int rows)
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = 3, RowCount = rows, Dock = DockStyle.Top, AutoSize = true,
                RightToLeft = RightToLeft.Yes, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 4)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return t;
        }

        private static Label FieldLabel(string text)
        {
            var l = Theme.Label(text, Theme.BodyBold);
            l.Anchor = AnchorStyles.Right;
            l.Margin = new Padding(3, 9, 3, 3);
            return l;
        }

        private static Label SectionTitle(string text)
        {
            return new Label { Text = text, Font = Theme.Subtitle, ForeColor = Theme.Text, Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.BottomLeft, RightToLeft = RightToLeft.Yes };
        }

        private static Label Hint(string text)
        {
            return new Label { Text = text, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleLeft, RightToLeft = RightToLeft.Yes };
        }

        private Panel BuildStep1()
        {
            var page = new Panel { AutoScroll = true };

            var nameGrid = Grid(1);
            _name = Theme.TextBox(false);
            nameGrid.Controls.Add(FieldLabel("اسم العميل"), 0, 0);
            nameGrid.Controls.Add(_name, 1, 0);

            var softFlow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 118, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes, WrapContents = false };
            foreach (var s in SoftwareInfo.All)
            {
                var tile = new OptionTile(s.Title, s.Available ? "متاح" : "سيتم إضافته قريباً", s.Id) { Available = s.Available, Badge = s.Available ? null : "قريباً", Size = new Size(240, 100) };
                if (s.Available) tile.Description = s.Subtitle + " — قاعدة بيانات SQL Server";
                tile.Selected += (o, e) => { SelectTile(_softTiles, (OptionTile)o); OnSoftwareChanged(); };
                _softTiles.Add(tile);
                softFlow.Controls.Add(tile);
            }

            var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 130, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes, WrapContents = false };
            var single = new OptionTile("فرع واحد (Single Branch)", "يجمع كمية كل منتج من كل المخازن ويرسل المنتجات للمتجر في دفعات (500 منتج).", SyncModes.Single) { Size = new Size(366, 112) };
            var multi = _multiTile = new OptionTile("متعدد الفروع (Multi Branch)", "يرسل مخزون كل فرع على حدة (الفرع الرئيسي + الفروع) في دفعات (1000 منتج لكل فرع).", SyncModes.Multi) { Size = new Size(366, 112) };
            foreach (var t in new[] { single, multi })
            {
                t.Selected += (o, e) => { SelectTile(_modeTiles, (OptionTile)o); _dbTestedOk = false; UpdateRemoteOption(); };
                _modeTiles.Add(t);
                modeFlow.Controls.Add(t);
            }

            // Added in reverse order because Dock=Top stacks upward.
            page.Controls.Add(modeFlow);
            page.Controls.Add(SectionTitle("نوع الربط"));
            page.Controls.Add(softFlow);
            page.Controls.Add(SectionTitle("برنامج الصيدلية"));
            page.Controls.Add(nameGrid);
            page.Controls.Add(SectionTitle("بيانات العميل"));
            return page;
        }

        private Panel BuildStep2()
        {
            var page = new Panel { AutoScroll = true };
            var g = Grid(6);

            _server = new ComboBox { Dock = DockStyle.Fill, Font = Theme.Body, RightToLeft = RightToLeft.No, Margin = new Padding(3, 4, 3, 4) };
            foreach (var s in SqlHelper.LocalInstances()) _server.Items.Add(s);
            _server.TextChanged += (s, e) => _dbTestedOk = false;
            g.Controls.Add(FieldLabel("SQL Server"), 0, 0);
            g.Controls.Add(_server, 1, 0);
            g.Controls.Add(new Label { Text = @"e.g.  .\SQLEXPRESS", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Right, RightToLeft = RightToLeft.No }, 2, 0);

            var authFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes, Margin = new Padding(0) };
            _winAuth = new RadioButton { Text = "Windows Authentication", AutoSize = true, RightToLeft = RightToLeft.Yes, Margin = new Padding(3, 7, 20, 3) };
            _sqlAuth = new RadioButton { Text = "SQL Server (مستخدم وكلمة مرور)", AutoSize = true, RightToLeft = RightToLeft.Yes, Margin = new Padding(3, 7, 3, 3) };
            authFlow.Controls.AddRange(new Control[] { _winAuth, _sqlAuth });
            _winAuth.CheckedChanged += (s, e) => { UpdateAuthFields(); _dbTestedOk = false; };
            g.Controls.Add(FieldLabel("طريقة الدخول"), 0, 1);
            g.Controls.Add(authFlow, 1, 1);
            g.SetColumnSpan(authFlow, 2);

            _user = Theme.TextBox();
            _user.TextChanged += (s, e) => _dbTestedOk = false;
            g.Controls.Add(FieldLabel("اسم المستخدم"), 0, 2);
            g.Controls.Add(_user, 1, 2);

            _pass = Theme.TextBox();
            _pass.UseSystemPasswordChar = true;
            _pass.TextChanged += (s, e) => _dbTestedOk = false;
            g.Controls.Add(FieldLabel("كلمة المرور"), 0, 3);
            g.Controls.Add(_pass, 1, 3);

            _database = new ComboBox { Dock = DockStyle.Fill, Font = Theme.Body, RightToLeft = RightToLeft.No, Margin = new Padding(3, 4, 3, 4) };
            _database.TextChanged += (s, e) => _dbTestedOk = false;
            var loadDbs = new FlatButton("عرض القواعد", ButtonKind.Secondary) { Width = 160, Height = 32, Margin = new Padding(3, 2, 3, 2) };
            loadDbs.Click += (s, e) => LoadDatabases();
            g.Controls.Add(FieldLabel("قاعدة البيانات"), 0, 4);
            g.Controls.Add(_database, 1, 4);
            g.Controls.Add(loadDbs, 2, 4);

            var test = new FlatButton("اختبار الاتصال وفحص الجداول", ButtonKind.Primary) { Height = 38, Margin = new Padding(3, 10, 3, 4) };
            test.Click += (s, e) => TestDatabase(null);
            g.Controls.Add(test, 1, 5);

            _dbResult = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 170, Font = Theme.Body, Padding = new Padding(14, 10, 14, 10), BackColor = Theme.Surface, ForeColor = Theme.Muted, RightToLeft = RightToLeft.Yes, TextAlign = ContentAlignment.TopLeft, Text = "اضغط (اختبار الاتصال) للتأكد من بيانات قاعدة البيانات ووجود جداول البرنامج." };
            var resultWrap = new Panel { Dock = DockStyle.Top, Height = 186, Padding = new Padding(0, 8, 0, 8) };
            resultWrap.Controls.Add(_dbResult);

            _remoteBranches = new CheckBox
            {
                Text = "قراءة مخزون باقي الفروع من سيرفراتها (بيانات الاتصال المسجلة في جدول Branches في Pharma Fly)",
                Dock = DockStyle.Top, Height = 30, RightToLeft = RightToLeft.Yes, Font = Theme.Body, Visible = false
            };
            _remoteBranches.CheckedChanged += (s2, e2) => _dbTestedOk = false;

            page.Controls.Add(resultWrap);
            page.Controls.Add(_remoteBranches);
            page.Controls.Add(g);
            page.Controls.Add(Hint("لو اسم المستخدم فاضي هيتم الدخول بـ Windows Authentication (نفس سلوك السكربت). المهمة المجدولة بتشتغل بحساب SYSTEM."));
            page.Controls.Add(SectionTitle("بيانات قاعدة البيانات (SQL Server)"));
            return page;
        }

        private Panel BuildStep3()
        {
            var page = new Panel { AutoScroll = true };
            var g = Grid(5);

            _apiUrl = Theme.TextBox();
            g.Controls.Add(FieldLabel("رابط الـ API"), 0, 0);
            g.Controls.Add(_apiUrl, 1, 0);
            g.SetColumnSpan(_apiUrl, 2);

            _apiKey = Theme.TextBox();
            _apiKey.UseSystemPasswordChar = true;
            _showKey = new CheckBox { Text = "إظهار", AutoSize = true, RightToLeft = RightToLeft.Yes, Anchor = AnchorStyles.Right, Margin = new Padding(3, 8, 3, 3) };
            _showKey.CheckedChanged += (s, e) => _apiKey.UseSystemPasswordChar = !_showKey.Checked;
            g.Controls.Add(FieldLabel("مفتاح الـ API"), 0, 1);
            g.Controls.Add(_apiKey, 1, 1);
            g.Controls.Add(_showKey, 2, 1);

            _interval = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 120, Width = 120, Font = Theme.Body, RightToLeft = RightToLeft.No, Anchor = AnchorStyles.Right, Margin = new Padding(3, 4, 3, 4) };
            g.Controls.Add(FieldLabel("المزامنة كل (دقيقة)"), 0, 2);
            g.Controls.Add(_interval, 1, 2);

            _timeout = new NumericUpDown { Minimum = 10, Maximum = 3600, Value = 300, Width = 120, Font = Theme.Body, RightToLeft = RightToLeft.No, Anchor = AnchorStyles.Right, Margin = new Padding(3, 4, 3, 4) };
            g.Controls.Add(FieldLabel("مهلة الطلب (ثانية)"), 0, 3);
            g.Controls.Add(_timeout, 1, 3);

            var probe = new FlatButton("فحص الوصول للسيرفر", ButtonKind.Secondary) { Height = 36, Margin = new Padding(3, 8, 3, 4) };
            probe.Click += (s, e) => ProbeApi();
            _apiResult = new Label { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Theme.Muted, RightToLeft = RightToLeft.Yes, Margin = new Padding(3, 14, 3, 3), MaximumSize = new Size(300, 0) };
            g.Controls.Add(probe, 1, 4);
            g.Controls.Add(_apiResult, 2, 4);

            var opts = new Card { Dock = DockStyle.Top, Height = 132, Padding = new Padding(16, 12, 16, 12) };
            _schedule = new CheckBox { Text = "تفعيل المزامنة التلقائية (مهمة مجدولة في ويندوز تعمل حتى بدون فتح البرنامج)", AutoSize = true, Checked = true, RightToLeft = RightToLeft.Yes, Font = Theme.BodyBold, BackColor = Theme.Surface, Dock = DockStyle.Top };
            _runAfter = new CheckBox { Text = "تشغيل أول مزامنة فعلية مباشرة بعد الحفظ", AutoSize = true, Checked = _isNew, RightToLeft = RightToLeft.Yes, BackColor = Theme.Surface, Dock = DockStyle.Top, Padding = new Padding(0, 10, 0, 0) };
            var note = new Label { Text = "ملاحظة: فحص الوصول لا يرسل أي بيانات للمتجر. للتأكد من البيانات قبل الإرسال استخدم (تجربة بدون إرسال) من الشاشة الرئيسية.", AutoSize = false, Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Surface, RightToLeft = RightToLeft.Yes, TextAlign = ContentAlignment.BottomLeft };
            opts.Controls.Add(note);
            opts.Controls.Add(_runAfter);
            opts.Controls.Add(_schedule);
            var optsWrap = new Panel { Dock = DockStyle.Top, Height = 148, Padding = new Padding(0, 14, 0, 0) };
            optsWrap.Controls.Add(opts);

            page.Controls.Add(optsWrap);
            page.Controls.Add(g);
            page.Controls.Add(SectionTitle("المتجر الإلكتروني والجدولة"));
            return page;
        }

        // =================================================================
        // Behaviour
        // =================================================================

        private void SelectTile(List<OptionTile> group, OptionTile tile)
        {
            foreach (var t in group) t.IsSelected = t == tile;
        }

        private static string SelectedValue(List<OptionTile> group)
        {
            foreach (var t in group) if (t.IsSelected) return (string)t.Value;
            return null;
        }

        private bool IsPharmaFly
        {
            get { return string.Equals(SelectedValue(_softTiles), "PharmaFly", StringComparison.OrdinalIgnoreCase); }
        }

        private void OnSoftwareChanged()
        {
            _dbTestedOk = false;
            // Suggest the default database name of the selected program.
            var db = _database.Text.Trim();
            if (IsPharmaFly && (db.Length == 0 || db.Equals("stock", StringComparison.OrdinalIgnoreCase))) _database.Text = "pfx";
            else if (!IsPharmaFly && (db.Length == 0 || db.Equals("pfx", StringComparison.OrdinalIgnoreCase))) _database.Text = "stock";
            UpdateRemoteOption();
            UpdateModeDescriptions();
        }

        private void UpdateModeDescriptions()
        {
            _multiTile.Description = IsPharmaFly
                ? "يرسل مخزون فرع هذه القاعدة (ويقدر يقرأ باقي الفروع من سيرفراتها) في دفعات (1000 منتج لكل فرع)."
                : "يرسل مخزون كل فرع على حدة (الفرع الرئيسي + الفروع) في دفعات (1000 منتج لكل فرع).";
            _multiTile.Invalidate();
        }

        private void UpdateRemoteOption()
        {
            _remoteAllowed = IsPharmaFly && SelectedValue(_modeTiles) == SyncModes.Multi;
            _remoteBranches.Visible = _remoteAllowed;
        }

        private void UpdateAuthFields()
        {
            var sql = _sqlAuth.Checked;
            _user.Enabled = sql;
            _pass.Enabled = sql;
        }

        private void LoadFromProfile()
        {
            _name.Text = _p.Name ?? "";
            foreach (var t in _softTiles) t.IsSelected = string.Equals((string)t.Value, _p.Software, StringComparison.OrdinalIgnoreCase) && t.Available;
            foreach (var t in _modeTiles) t.IsSelected = (string)t.Value == (_p.Mode ?? SyncModes.Single);
            _server.Text = _p.SqlServer ?? "";
            _database.Text = _p.Database ?? "";
            _winAuth.Checked = _p.UsesWindowsAuth;
            _sqlAuth.Checked = !_p.UsesWindowsAuth;
            _remoteBranches.Checked = _p.IncludeRemoteBranches;
            UpdateRemoteOption();
            UpdateModeDescriptions();
            _user.Text = _p.Username ?? "";
            _pass.Text = _p.Password ?? "";
            UpdateAuthFields();
            _apiUrl.Text = _p.ApiUrl ?? "";
            _apiKey.Text = _p.ApiKey ?? "";
            _interval.Value = Clamp(_p.SyncIntervalMinutes, 1, 1440, 120);
            _timeout.Value = Clamp(_p.RequestTimeoutSeconds, 10, 3600, 300);
            _schedule.Checked = _isNew || _p.ScheduleEnabled;
            _dbTestedOk = false;
        }

        private static decimal Clamp(int v, int min, int max, int def)
        {
            if (v <= 0) v = def;
            return Math.Max(min, Math.Min(max, v));
        }

        private void ReadIntoProfile()
        {
            _p.Name = _name.Text.Trim();
            _p.Software = SelectedValue(_softTiles);
            _p.Mode = SelectedValue(_modeTiles);
            _p.SqlServer = _server.Text.Trim();
            _p.Database = _database.Text.Trim();
            _p.Username = _sqlAuth.Checked ? _user.Text.Trim() : "";
            _p.Password = _sqlAuth.Checked ? _pass.Text : "";
            _p.ApiUrl = _apiUrl.Text.Trim();
            _p.ApiKey = _apiKey.Text.Trim();
            _p.SyncIntervalMinutes = (int)_interval.Value;
            _p.RequestTimeoutSeconds = (int)_timeout.Value;
            _p.ScheduleEnabled = _schedule.Checked;
            _p.IncludeRemoteBranches = _remoteAllowed && _remoteBranches.Checked;
        }

        private void Go(int step)
        {
            if (step < 0 || step > 2) return;
            _step = step;
            for (int i = 0; i < 3; i++) _pages[i].Visible = i == step;
            _steps.Current = step;
            _back.Visible = step > 0;
            _next.Visible = step < 2;
            _save.Visible = step == 2;
            if (step == 2) _save.Focus(); else _next.Focus();
        }

        private bool ValidateStep(int step)
        {
            if (step == 0)
            {
                if (_name.Text.Trim().Length == 0) { Ui.Warn(this, "اكتب اسم العميل / الصيدلية."); _name.Focus(); return false; }
                if (SelectedValue(_softTiles) == null) { Ui.Warn(this, "اختر برنامج الصيدلية."); return false; }
                if (SelectedValue(_modeTiles) == null) { Ui.Warn(this, "اختر نوع الربط (فرع واحد أو متعدد الفروع)."); return false; }
                return true;
            }
            if (step == 1)
            {
                if (_server.Text.Trim().Length == 0) { Ui.Warn(this, "اكتب اسم SQL Server."); _server.Focus(); return false; }
                if (_database.Text.Trim().Length == 0) { Ui.Warn(this, "اكتب أو اختر اسم قاعدة البيانات."); _database.Focus(); return false; }
                if (_sqlAuth.Checked && _user.Text.Trim().Length == 0) { Ui.Warn(this, "اكتب اسم مستخدم SQL Server أو اختر Windows Authentication."); _user.Focus(); return false; }
                if (!_dbTestedOk)
                {
                    bool ok = false;
                    TestDatabase(r => ok = r);
                    if (!ok && !Ui.Ask(this, "اختبار قاعدة البيانات لم ينجح.\nهل تريد المتابعة على أي حال؟", "تنبيه", true)) return false;
                }
                return true;
            }
            return ValidateFinal();
        }

        private bool ValidateFinal()
        {
            Uri uri;
            var url = _apiUrl.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Ui.Warn(this, "رابط الـ API غير صحيح. لازم يبدأ بـ https:// أو http://");
                _apiUrl.Focus();
                return false;
            }
            if (_apiKey.Text.Trim().Length == 0) { Ui.Warn(this, "اكتب مفتاح الـ API."); _apiKey.Focus(); return false; }
            return true;
        }

        /// <summary>Runs on a worker thread while showing a wait cursor; keeps UI responsive.</summary>
        private void RunBusy(Action work)
        {
            Exception error = null;
            var done = new ManualResetEvent(false);
            UseWaitCursor = true;
            Enabled = false;
            var t = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } finally { done.Set(); } }) { IsBackground = true };
            t.Start();
            while (!done.WaitOne(30)) Application.DoEvents();
            Enabled = true;
            UseWaitCursor = false;
            if (error != null) throw error;
        }

        private void LoadDatabases()
        {
            var server = _server.Text.Trim();
            var user = _sqlAuth.Checked ? _user.Text.Trim() : "";
            var pass = _pass.Text;
            List<string> dbs = null;
            try
            {
                RunBusy(() => dbs = SqlHelper.ListDatabases(server, user, pass));
                var current = _database.Text;
                _database.Items.Clear();
                foreach (var d in dbs) _database.Items.Add(d);
                _database.Text = current;
                if (dbs.Count == 0) Ui.Info(this, "لا توجد قواعد بيانات للمستخدم على هذا السيرفر.");
                else _database.DroppedDown = true;
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تعذر الاتصال بالسيرفر:\n" + SqlHelper.ShortError(ex));
            }
        }

        private void TestDatabase(Action<bool> callback)
        {
            var mode = SelectedValue(_modeTiles) ?? SyncModes.Single;
            var software = SelectedValue(_softTiles) ?? "Estock";
            var includeRemote = _remoteAllowed && _remoteBranches.Checked;
            var cs = SqlHelper.BuildConnectionString(_server.Text, _database.Text, _sqlAuth.Checked ? _user.Text : "", _pass.Text, 15);
            var sb = new StringBuilder();
            bool ok = false;
            try
            {
                RunBusy(() =>
                {
                    using (var conn = new SqlConnection(cs))
                    {
                        conn.Open();
                        sb.AppendLine("✔ تم الاتصال بقاعدة البيانات بنجاح (SQL Server " + conn.ServerVersion + ")");
                        bool tablesOk;
                        foreach (var l in Inspectors.Inspect(software, conn, mode, includeRemote, out tablesOk)) sb.AppendLine(l);
                        ok = tablesOk;
                        if (!tablesOk) sb.AppendLine("✖ قاعدة البيانات لا تحتوي على جداول " + software + " المطلوبة لهذا النوع من الربط.");
                    }
                });
            }
            catch (Exception ex)
            {
                sb.Length = 0;
                sb.AppendLine("✖ فشل الاتصال بقاعدة البيانات:");
                sb.AppendLine(SqlHelper.ShortError(ex));
                ok = false;
            }
            _dbTestedOk = ok;
            _dbResult.Text = sb.ToString();
            _dbResult.ForeColor = ok ? Theme.Success : Theme.Danger;
            _dbResult.BackColor = ok ? Theme.SuccessSoft : Theme.DangerSoft;
            // grow the box to fit all lines (the page scrolls if needed)
            var needed = TextRenderer.MeasureText(_dbResult.Text, _dbResult.Font, new Size(_dbResult.Width - _dbResult.Padding.Horizontal, 0), TextFormatFlags.WordBreak).Height + _dbResult.Padding.Vertical + 6;
            _dbResult.Height = Math.Max(170, needed);
            _dbResult.Parent.Height = _dbResult.Height + 16;
            var pg = _pages[1] as ScrollableControl;
            if (pg != null) pg.AutoScrollPosition = new Point(0, pg.DisplayRectangle.Height);
            if (callback != null) callback(ok);
        }

        private void ProbeApi()
        {
            Uri uri;
            if (!Uri.TryCreate(_apiUrl.Text.Trim(), UriKind.Absolute, out uri)) { Ui.Warn(this, "اكتب رابط الـ API أولاً."); return; }
            HttpResult r = null;
            RunBusy(() => r = HttpHelper.Probe(uri.ToString(), 20));
            if (r.Success)
            {
                _apiResult.Text = "✔ السيرفر متاح (HTTP " + r.StatusCode + ")";
                _apiResult.ForeColor = Theme.Success;
            }
            else
            {
                _apiResult.Text = "✖ تعذر الوصول: " + r.ErrorMessage;
                _apiResult.ForeColor = Theme.Danger;
            }
        }

        private void ImportConfig()
        {
            using (var dlg = new OpenFileDialog { Filter = "config.json|*.json|All files|*.*", Title = "اختر ملف config.json من فولدر السكربت القديم" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var d = Storage.FromJson<Dictionary<string, object>>(File.ReadAllText(dlg.FileName, Encoding.UTF8));
                    Func<string, string> s = k => d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : null;
                    if (s("sqlServer") != null) _server.Text = s("sqlServer");
                    if (s("database") != null) _database.Text = s("database");
                    var user = s("username") ?? "";
                    _user.Text = user;
                    _pass.Text = s("password") ?? "";
                    _sqlAuth.Checked = user.Trim().Length > 0;
                    _winAuth.Checked = !_sqlAuth.Checked;
                    if (s("apiUrl") != null) _apiUrl.Text = s("apiUrl");
                    if (s("apiKey") != null) _apiKey.Text = s("apiKey");
                    int n;
                    if (int.TryParse(s("syncIntervalMinutes"), out n)) _interval.Value = Clamp(n, 1, 1440, 120);
                    if (int.TryParse(s("requestTimeoutSeconds"), out n)) _timeout.Value = Clamp(n, 10, 3600, 300);
                    _dbTestedOk = false;
                    Ui.Info(this, "تم استيراد الإعدادات. راجع البيانات واختر نوع الربط (فرع واحد / متعدد الفروع) ثم اختبر الاتصال.");
                }
                catch (Exception ex)
                {
                    Ui.Error(this, "ملف غير صالح:\n" + ex.Message);
                }
            }
        }

        private void DoSave()
        {
            for (int i = 0; i < 2; i++)
            {
                if (i == 1 && _dbTestedOk) continue;
                if (!ValidateStep(i)) { Go(i); return; }
            }
            if (!ValidateFinal()) return;
            ReadIntoProfile();

            // Warn on duplicates (would sync the same data twice)
            foreach (var other in Storage.LoadProfiles())
            {
                if (other.Id == _p.Id) continue;
                if (string.Equals(other.SqlServer, _p.SqlServer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(other.Database, _p.Database, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(other.ApiUrl, _p.ApiUrl, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Ui.Ask(this, "يوجد ربط آخر (" + other.Name + ") بنفس قاعدة البيانات ونفس الـ API.\nده هيخلي البيانات تتبعت مرتين. هل تريد الحفظ على أي حال؟", "تنبيه", true)) return;
                }
            }

            try
            {
                Storage.SaveProfile(_p);
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تعذر حفظ الإعدادات:\n" + ex.Message);
                return;
            }

            try
            {
                if (_p.ScheduleEnabled)
                {
                    var exe = AppInstaller.EnsureInstalled();
                    TaskService.Install(_p, exe);
                }
                else
                {
                    TaskService.Remove(_p.Id);
                }
            }
            catch (Exception ex)
            {
                Ui.Error(this, "تم حفظ الإعدادات لكن تعذر ضبط المهمة المجدولة:\n" + ex.Message + "\n\nتأكد أن البرنامج يعمل كمسؤول (Run as Administrator).");
            }

            RunAfterSave = _runAfter.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Draws "1 — 2 — 3" step indicator, right-to-left.</summary>
    public class StepIndicator : Control
    {
        private readonly string[] _titles;
        private int _current;

        public StepIndicator(string[] titles)
        {
            _titles = titles;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        public int Current { get { return _current; } set { _current = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int n = _titles.Length;
            int seg = Width / n;
            for (int i = 0; i < n; i++)
            {
                // RTL: step 0 is the right-most segment
                int cx = Width - (seg * i + seg / 2);
                int cy = 22;
                bool done = i < _current, active = i == _current;
                var color = done ? Theme.Success : active ? Theme.Primary : Color.FromArgb(203, 213, 225);
                if (i < n - 1)
                {
                    using (var p = new Pen(i < _current ? Theme.Success : Color.FromArgb(226, 232, 240), 2f))
                        g.DrawLine(p, cx - 18, cy, cx - seg + 18, cy);
                }
                var circle = new Rectangle(cx - 14, cy - 14, 28, 28);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, circle);
                TextRenderer.DrawText(g, done ? "✓" : (i + 1).ToString(), Theme.BodyBold, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                var tr = new Rectangle(cx - seg / 2, cy + 16, seg, 22);
                TextRenderer.DrawText(g, _titles[i], active ? Theme.BodyBold : Theme.Small, tr, active ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.RightToLeft);
            }
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }
}
