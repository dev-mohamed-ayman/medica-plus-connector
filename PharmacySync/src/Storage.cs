using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace PharmacySync
{
    /// <summary>
    /// All data lives in %ProgramData%\PharmacySync (readable by the SYSTEM account used by the scheduled task).
    /// Profiles are encrypted with DPAPI (LocalMachine) so they cannot be read or edited by hand,
    /// and the folder ACL is restricted to Administrators + SYSTEM.
    /// </summary>
    public static class Storage
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PharmacySync::profile::v1::7f3a91c2");

        public static string DataRoot
        {
            get
            {
                // Developer/testing override only.
                var overrideDir = Environment.GetEnvironmentVariable("PHARMACYSYNC_DATA");
                if (!StrUtil.IsBlank(overrideDir)) return overrideDir;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.FolderName);
            }
        }

        public static string ConnectionsRoot { get { return Path.Combine(DataRoot, "connections"); } }

        public static string ProfileDir(string id) { return Path.Combine(ConnectionsRoot, id); }
        public static string ProfileFile(string id) { return Path.Combine(ProfileDir(id), "profile.dat"); }
        public static string StatusFile(string id) { return Path.Combine(ProfileDir(id), "status.json"); }
        public static string LogDir(string id) { return Path.Combine(ProfileDir(id), "logs"); }
        public static string SyncLogFile(string id) { return Path.Combine(LogDir(id), "sync.log"); }
        public static string PrevSyncLogFile(string id) { return Path.Combine(LogDir(id), "sync.previous.log"); }
        public static string DryRunLogFile(string id) { return Path.Combine(LogDir(id), "dry-run.log"); }
        public static string CrashLogFile { get { return Path.Combine(DataRoot, "error.log"); } }

        public static void EnsureDataRoot()
        {
            if (!Directory.Exists(DataRoot))
            {
                Directory.CreateDirectory(DataRoot);
            }
            if (StrUtil.IsBlank(Environment.GetEnvironmentVariable("PHARMACYSYNC_DATA"))) TryRestrictAcl(DataRoot);
            if (!Directory.Exists(ConnectionsRoot)) Directory.CreateDirectory(ConnectionsRoot);
        }

        /// <summary>Only Administrators and SYSTEM may access the data folder (non-admin users cannot tamper).</summary>
        private static void TryRestrictAcl(string dir)
        {
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, sec);
            }
            catch
            {
                // Not fatal (e.g. not elevated during development).
            }
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        public static string ToJson(object o) { return Serializer().Serialize(o); }
        public static T FromJson<T>(string s) { return Serializer().Deserialize<T>(s); }

        // ---------------- Profiles ----------------

        public static List<ConnectionProfile> LoadProfiles()
        {
            var list = new List<ConnectionProfile>();
            if (!Directory.Exists(ConnectionsRoot)) return list;
            foreach (var dir in Directory.GetDirectories(ConnectionsRoot))
            {
                var id = Path.GetFileName(dir);
                try
                {
                    var p = LoadProfile(id);
                    if (p != null) list.Add(p);
                }
                catch (Exception ex)
                {
                    LogCrash("Failed to load profile " + id, ex);
                }
            }
            list.Sort((a, b) => string.Compare(a.CreatedAt, b.CreatedAt, StringComparison.Ordinal));
            return list;
        }

        public static ConnectionProfile LoadProfile(string id)
        {
            var file = ProfileFile(id);
            if (!File.Exists(file)) return null;
            var cipher = File.ReadAllBytes(file);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            var p = FromJson<ConnectionProfile>(Encoding.UTF8.GetString(plain));
            p.Id = id;
            return p;
        }

        public static void SaveProfile(ConnectionProfile p)
        {
            EnsureDataRoot();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (string.IsNullOrEmpty(p.CreatedAt)) p.CreatedAt = now;
            p.UpdatedAt = now;
            Directory.CreateDirectory(ProfileDir(p.Id));
            var plain = Encoding.UTF8.GetBytes(ToJson(p));
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            WriteAtomic(ProfileFile(p.Id), cipher);
        }

        public static void DeleteProfile(string id)
        {
            var dir = ProfileDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        // ---------------- Status ----------------

        public static RunStatus LoadStatus(string id)
        {
            try
            {
                var f = StatusFile(id);
                if (!File.Exists(f)) return null;
                return FromJson<RunStatus>(File.ReadAllText(f, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        public static void SaveStatus(string id, RunStatus s)
        {
            Directory.CreateDirectory(ProfileDir(id));
            WriteAtomic(StatusFile(id), Encoding.UTF8.GetBytes(ToJson(s)));
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static void LogCrash(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataRoot);
                File.AppendAllText(CrashLogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + context + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
