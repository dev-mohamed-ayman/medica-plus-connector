using System;
using System.Threading;
using System.Windows.Forms;
using PharmacySync.UI;

namespace PharmacySync
{
    internal static class Program
    {
        /// <summary>
        /// PharmacySync.exe                → opens the app (login + dashboard)
        /// PharmacySync.exe --run &lt;id&gt;    → headless sync (used by the scheduled task, no window)
        /// PharmacySync.exe --dry-run &lt;id&gt; → headless dry run (testing)
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
#if NET35
            Win7Bootstrap.Init(); // embedded BouncyCastle (HTTPS / TLS 1.2 on Windows 7)
#endif
            if (args.Length >= 2 && (args[0] == "--run" || args[0] == "--dry-run"))
            {
                return Headless(args[1], args[0] == "--dry-run");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
            {
                Storage.LogCrash("UI", e.Exception);
                Ui.Error(null, "حدث خطأ غير متوقع:\n" + e.Exception.Message);
            };

            bool created;
            using (var single = new Mutex(true, "Local\\PharmacySync_UI", out created))
            {
                if (!created)
                {
                    Ui.Info(null, "البرنامج مفتوح بالفعل.");
                    return 0;
                }

                try
                {
                    Storage.EnsureDataRoot();
                }
                catch (Exception ex)
                {
                    Ui.Error(null, "تعذر إنشاء مجلد البيانات:\n" + ex.Message + "\n\nشغل البرنامج كمسؤول (Run as Administrator).");
                    return 1;
                }

                using (var login = new LoginForm())
                {
                    if (login.ShowDialog() != DialogResult.OK) return 0;
                }
                Application.Run(new MainForm());
            }
            return 0;
        }

        private static int Headless(string id, bool dryRun)
        {
            try
            {
                Storage.EnsureDataRoot();
                var p = Storage.LoadProfile(id);
                if (p == null)
                {
                    Storage.LogCrash("Scheduled run", new Exception("Connection '" + id + "' not found."));
                    return 2;
                }
                var outcome = SyncRunner.Run(p, new SyncOptions { DryRun = dryRun, Trigger = "Scheduled" }, null);
                return outcome.Result == RunResults.Success || outcome.Result == RunResults.Skipped ? 0 : 1;
            }
            catch (Exception ex)
            {
                Storage.LogCrash("Scheduled run " + id, ex);
                return 3;
            }
        }
    }
}
