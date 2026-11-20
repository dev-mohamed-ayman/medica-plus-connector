using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace PharmacySync
{
    public class TaskInfo
    {
        public bool Exists;
        public bool Enabled;
        public bool Running;
        public DateTime? LastRun;
        public DateTime? NextRun;
        public int LastResult;
    }

    /// <summary>
    /// Windows Task Scheduler (COM "Schedule.Service") — replaces InstallScheduler.ps1.
    /// Tasks live in the \PharmacySync folder and run "PharmacySync.exe --run &lt;id&gt;" as SYSTEM.
    /// </summary>
    public static class TaskService
    {
        private const int TASK_CREATE_OR_UPDATE = 6;
        private const int TASK_LOGON_SERVICE_ACCOUNT = 5;
        private const int TASK_STATE_RUNNING = 4;

        public static string TaskName(string profileId) { return "Sync-" + profileId; }

        private static object Connect()
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            var svc = Activator.CreateInstance(t);
            Com.Call(svc, "Connect");
            return svc;
        }

        private static object GetFolder(object svc, bool create)
        {
            try
            {
                return Com.Call(svc, "GetFolder", "\\" + AppInfo.TaskFolder);
            }
            catch (Exception)
            {
                if (!create) return null;
                var root = Com.Call(svc, "GetFolder", "\\");
                return Com.Call(root, "CreateFolder", AppInfo.TaskFolder);
            }
        }

        private static object GetTask(object folder, string name)
        {
            if (folder == null) return null;
            try { return Com.Call(folder, "GetTask", name); }
            catch (Exception) { return null; }
        }

        public static void Install(ConnectionProfile p, string exePath)
        {
            var svc = Connect();
            var folder = GetFolder(svc, true);
            var xml = BuildXml(p, exePath);
            Com.Call(folder, "RegisterTask", TaskName(p.Id), xml, TASK_CREATE_OR_UPDATE, "SYSTEM", null, TASK_LOGON_SERVICE_ACCOUNT, null);
        }

        public static void Remove(string profileId)
        {
            var svc = Connect();
            var folder = GetFolder(svc, false);
            if (folder == null) return;
            if (GetTask(folder, TaskName(profileId)) == null) return;
            Com.Call(folder, "DeleteTask", TaskName(profileId), 0);
        }

        public static void RunNow(string profileId)
        {
            var svc = Connect();
            var task = GetTask(GetFolder(svc, false), TaskName(profileId));
            if (task == null) throw new Exception("المهمة المجدولة غير موجودة.");
            Com.Call(task, "Run", (object)null);
        }

        public static TaskInfo Query(string profileId)
        {
            var info = new TaskInfo();
            try
            {
                var svc = Connect();
                var task = GetTask(GetFolder(svc, false), TaskName(profileId));
                if (task == null) return info;
                info.Exists = true;
                info.Enabled = Convert.ToBoolean(Com.Get(task, "Enabled"));
                info.Running = Convert.ToInt32(Com.Get(task, "State")) == TASK_STATE_RUNNING;
                info.LastResult = Convert.ToInt32(Com.Get(task, "LastTaskResult"));
                info.LastRun = ValidDate(Convert.ToDateTime(Com.Get(task, "LastRunTime")));
                info.NextRun = ValidDate(Convert.ToDateTime(Com.Get(task, "NextRunTime")));
            }
            catch (Exception) { }
            return info;
        }

        private static DateTime? ValidDate(DateTime d)
        {
            if (d.Year < 2000) return null;
            return d;
        }

        // ---------------- Legacy (old PowerShell scripts) ----------------

        public static bool LegacyTaskExists()
        {
            try
            {
                var svc = Connect();
                return GetTask(Com.Call(svc, "GetFolder", "\\"), AppInfo.LegacyTaskName) != null;
            }
            catch { return false; }
        }

        public static void RemoveLegacyTask()
        {
            var svc = Connect();
            var root = Com.Call(svc, "GetFolder", "\\");
            Com.Call(root, "DeleteTask", AppInfo.LegacyTaskName, 0);
        }

        // ---------------- XML ----------------

        private static string BuildXml(ConnectionProfile p, string exePath)
        {
            var interval = Math.Max(1, p.SyncIntervalMinutes);
            var start = DateTime.Now.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss");
            var desc = "Pharmacy Sync - " + p.Name + " (" + p.Software + " / " + p.Mode + ") - every " + interval + " minutes";
            return
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>" + Esc(AppInfo.Name) + @"</Author>
    <Description>" + Esc(desc) + @"</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Repetition>
        <Interval>PT" + interval + @"M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>" + start + @"</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT6H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>" + Esc(exePath) + @"</Command>
      <Arguments>--run " + Esc(p.Id) + @"</Arguments>
      <WorkingDirectory>" + Esc(Path.GetDirectoryName(exePath)) + @"</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        private static string Esc(string s) { return SecurityElement.Escape(s ?? ""); }
    }

    /// <summary>All-users shell folders (Environment.SpecialFolder lacks them on .NET 3.5).</summary>
    internal static class ShellFolders
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetFolderPath(IntPtr hwnd, int csidl, IntPtr token, int flags, System.Text.StringBuilder path);

        public static string Get(int csidl)
        {
            var sb = new System.Text.StringBuilder(260);
            return SHGetFolderPath(IntPtr.Zero, csidl, IntPtr.Zero, 0, sb) == 0 ? sb.ToString() : null;
        }
    }

    /// <summary>Late-bound COM calls (works on .NET 3.5 and 4.x, unlike "dynamic").</summary>
    internal static class Com
    {
        public static object Call(object target, string method, params object[] args)
        {
            try { return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        public static object Get(object target, string prop)
        {
            try { return target.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, target, null); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        public static void Set(object target, string prop, object value)
        {
            try { target.GetType().InvokeMember(prop, BindingFlags.SetProperty, null, target, new[] { value }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }

    /// <summary>
    /// Copies the exe to Program Files so scheduled tasks keep working even if the technician
    /// deletes the downloaded copy, and creates Desktop / Start menu shortcuts.
    /// </summary>
    public static class AppInstaller
    {
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.FolderName); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, AppInfo.ExeName); } }

        public static string CurrentExe
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        public static bool IsRunningFromInstallDir
        {
            get { return string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Returns the exe path that scheduled tasks should use.</summary>
        public static string EnsureInstalled()
        {
            if (IsRunningFromInstallDir) return InstalledExe;
            Directory.CreateDirectory(InstallDir);
            if (!File.Exists(InstalledExe) || !FilesEqual(CurrentExe, InstalledExe))
            {
                try
                {
                    File.Copy(CurrentExe, InstalledExe, true);
                }
                catch (IOException)
                {
                    throw new Exception("لا يمكن تحديث نسخة البرنامج المثبتة لأنها مفتوحة أو قيد التشغيل الآن.\n" + InstalledExe + "\nأغلق النسخة الأخرى وحاول مرة أخرى.");
                }
            }
            CreateShortcuts();
            return InstalledExe;
        }

        private static bool FilesEqual(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            var ba = File.ReadAllBytes(a); var bb = File.ReadAllBytes(b);
            for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
            return true;
        }

        private static void CreateShortcuts()
        {
            try
            {
                var targets = new[]
                {
                    ShellFolders.Get(0x19), // CSIDL_COMMON_DESKTOPDIRECTORY
                    ShellFolders.Get(0x17)  // CSIDL_COMMON_PROGRAMS
                };
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                foreach (var dir in targets)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    var lnkPath = Path.Combine(dir, AppInfo.Name + ".lnk");
                    var lnk = Com.Call(shell, "CreateShortcut", lnkPath);
                    Com.Set(lnk, "TargetPath", InstalledExe);
                    Com.Set(lnk, "WorkingDirectory", InstallDir);
                    Com.Set(lnk, "Description", AppInfo.Name);
                    Com.Set(lnk, "IconLocation", InstalledExe + ",0");
                    Com.Call(lnk, "Save");
                    Marshal.FinalReleaseComObject(lnk);
                }
                Marshal.FinalReleaseComObject(shell);
            }
            catch { }
        }
    }
}
