using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace PharmacySync.UI
{
    public static class Auth
    {
        public static bool Verify(string user, string password)
        {
            if (user == null || password == null) return false;
            if (!string.Equals(user.Trim(), Credentials.Username, StringComparison.OrdinalIgnoreCase)) return false;
            var salt = Convert.FromBase64String(Credentials.Salt);
            var expected = Convert.FromBase64String(Credentials.Hash);
            byte[] actual;
            var kdf = new Rfc2898DeriveBytes(password, salt, Credentials.Iterations);
            actual = kdf.GetBytes(expected.Length);
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
            return diff == 0;
        }
    }

    public class LoginForm : Form
    {
        private readonly TextBox _user;
        private readonly TextBox _pass;
        private readonly Label _error;
        private int _failures;

        public LoginForm()
        {
            Theme.ApplyForm(this);
            Text = AppInfo.Name + " - تسجيل الدخول";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 470);
            BackColor = Theme.Surface;

            var header = new HeaderBar { Height = 150 };
            var logo = new PictureBox { Image = AppIcon.Draw(56), SizeMode = PictureBoxSizeMode.CenterImage, Size = new Size(64, 64), BackColor = Color.Transparent, Location = new Point((420 - 64) / 2, 18) };
            var title = new Label { Text = AppInfo.Name, Font = Theme.Title, ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 86, 420, 30) };
            var sub = new Label { Text = "ربط برامج الصيدليات مع المتجر الإلكتروني", Font = Theme.Small, ForeColor = Color.FromArgb(210, 240, 236), BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 116, 420, 22) };
            header.Controls.AddRange(new Control[] { logo, title, sub });

            var lblUser = new Label { Text = "اسم المستخدم", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(40, 172, 340, 22), ForeColor = Theme.Muted };
            _user = new TextBox { Bounds = new Rectangle(40, 196, 340, 30), Font = Theme.Font(11f), BorderStyle = BorderStyle.FixedSingle, RightToLeft = RightToLeft.No };
            var lblPass = new Label { Text = "كلمة المرور", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(40, 238, 340, 22), ForeColor = Theme.Muted };
            _pass = new TextBox { Bounds = new Rectangle(40, 262, 340, 30), Font = Theme.Font(11f), BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true, RightToLeft = RightToLeft.No };
            _error = new Label { Text = "", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(40, 300, 340, 24), ForeColor = Theme.Danger };
            var btn = new FlatButton("تسجيل الدخول", ButtonKind.Primary) { Bounds = new Rectangle(40, 330, 340, 42) };
            btn.Click += (s, e) => TryLogin();
            var ver = new Label { Text = "الإصدار " + AppInfo.Version, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 430, 420, 24), ForeColor = Theme.Muted, Font = Theme.Small };

            Controls.AddRange(new Control[] { lblUser, _user, lblPass, _pass, _error, btn, ver, header });

            _user.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _pass.Focus(); } };
            _pass.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryLogin(); } };
            Shown += (s, e) => _user.Focus();
        }

        private void TryLogin()
        {
            if (Auth.Verify(_user.Text, _pass.Text))
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _failures++;
            _pass.Clear();
            _pass.Focus();
            if (_failures >= 5)
            {
                Ui.Error(this, "تم إدخال بيانات خاطئة عدة مرات. سيتم إغلاق البرنامج.");
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            _error.Text = "اسم المستخدم أو كلمة المرور غير صحيحة";
        }
    }
}
