using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace PharmacySync
{
    // ======================================================================
    // Logger (same format as the scripts: [yyyy-MM-dd HH:mm:ss] [LEVEL] msg)
    // ======================================================================
    public class SyncLogger : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new object();
        public event Action<string, string> LineWritten; // level, full line

        public SyncLogger(string file)
        {
            if (file != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                _writer = new StreamWriter(file, false, new UTF8Encoding(true));
                _writer.AutoFlush = true;
            }
        }

        public void Info(string m) { Write(m, "INFO"); }
        public void Warn(string m) { Write(m, "WARNING"); }
        public void Error(string m) { Write(m, "ERROR"); }

        public void Write(string message, string level)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message;
            lock (_lock)
            {
                if (_writer != null)
                {
                    try { _writer.WriteLine(line); } catch { }
                }
            }
            var h = LineWritten;
            if (h != null)
            {
                try { h(level, line); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_writer != null) _writer.Dispose();
            }
        }
    }

    // ======================================================================
    // SQL Server helpers
    // ======================================================================
    public static class SqlHelper
    {
        public const int ConnectTimeoutSeconds = 30;
        public const int CommandTimeoutSeconds = 600;

        public static string BuildConnectionString(string server, string database, string username, string password, int connectTimeout)
        {
            var b = new SqlConnectionStringBuilder();
            b.DataSource = (server ?? "").Trim();
            if (!StrUtil.IsBlank(database)) b.InitialCatalog = database.Trim();
            if (StrUtil.IsBlank(username))
            {
                b.IntegratedSecurity = true;
            }
            else
            {
                b.IntegratedSecurity = false;
                b.UserID = username.Trim();
                b.Password = password ?? "";
            }
            b.ConnectTimeout = connectTimeout;
            b.ApplicationName = AppInfo.Name;
            return b.ConnectionString;
        }

        public static string BuildConnectionString(ConnectionProfile p)
        {
            return BuildConnectionString(p.SqlServer, p.Database, p.Username, p.Password, ConnectTimeoutSeconds);
        }

        public static DataTable Query(SqlConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = CommandTimeoutSeconds;
                using (var adapter = new SqlDataAdapter(cmd))
                {
                    var table = new DataTable();
                    adapter.Fill(table);
                    return table;
                }
            }
        }

        public static object Scalar(SqlConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = CommandTimeoutSeconds;
                return cmd.ExecuteScalar();
            }
        }

        /// <summary>SQL Server instances installed on this machine (from the registry).</summary>
        public static List<string> LocalInstances()
        {
            var result = new List<string>();
#if NET35
            // .NET 3.5 has no RegistryView: read the native view, then the 32-bit (Wow6432Node) view.
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL",
                                         @"SOFTWARE\Wow6432Node\Microsoft\Microsoft SQL Server\Instance Names\SQL" })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(path))
                    {
#else
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                    {
#endif
                        if (key == null) continue;
                        foreach (var name in key.GetValueNames())
                        {
                            var s = name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : ".\\" + name;
                            if (!result.Contains(s)) result.Add(s);
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        public static List<string> ListDatabases(string server, string username, string password)
        {
            var list = new List<string>();
            using (var conn = new SqlConnection(BuildConnectionString(server, "master", username, password, 15)))
            {
                conn.Open();
                var t = Query(conn, "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name");
                foreach (DataRow r in t.Rows) list.Add(r[0].ToString());
            }
            return list;
        }

        public static string ShortError(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null && !(e is SqlException) && !(e is WebException)) e = e.InnerException;
            return e.Message;
        }
    }

    // ======================================================================
    // HTTP (same behaviour as Invoke-RestMethod: POST, X-API-KEY, application/json, UTF-8 body)
    // ======================================================================
    public class HttpResult
    {
        public bool Success;
        public int StatusCode;
        public string Body;
        public string ErrorMessage;
    }

#if NET35
    // Windows 7 build: .NET 3.5 cannot do TLS 1.2 without OS updates, so HTTPS goes through
    // the bundled BouncyCastle TLS stack (see Win7\Win7Http.cs).
    public static class HttpHelper
    {
        public static HttpResult PostJson(string url, string apiKey, string json, int timeoutSeconds)
        {
            return Win7Http.PostJson(url, apiKey, json, timeoutSeconds);
        }

        public static HttpResult Probe(string url, int timeoutSeconds)
        {
            return Win7Http.Probe(url, timeoutSeconds);
        }
    }
#else
    public static class HttpHelper
    {
        private static bool _initialized;

        public static void EnsureTls()
        {
            if (_initialized) return;
            _initialized = true;
            ServicePointManager.Expect100Continue = false;
            try
            {
                // TLS 1.3 exists in SChannel only on Windows 11 / Server 2022 (build 20348+).
                // Requesting it on Windows 7/8/10 makes the handshake fail, so ask for TLS 1.2 only there.
                var os = Environment.OSVersion.Version;
                bool tls13 = os.Major > 10 || (os.Major == 10 && os.Build >= 20348);
                ServicePointManager.SecurityProtocol = tls13
                    ? (SecurityProtocolType)3072 | (SecurityProtocolType)12288
                    : (SecurityProtocolType)3072;
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; }
                catch { }
            }
        }

        public static HttpResult PostJson(string url, string apiKey, string json, int timeoutSeconds)
        {
            EnsureTls();
            var result = new HttpResult();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.Headers["X-API-KEY"] = apiKey ?? "";
                req.UserAgent = "PharmacySync/" + AppInfo.Version;
                var ms = Math.Max(10, timeoutSeconds) * 1000;
                req.Timeout = ms;
                req.ReadWriteTimeout = ms;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                var body = Encoding.UTF8.GetBytes(json);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    result.StatusCode = (int)resp.StatusCode;
                    result.Body = ReadBody(resp);
                    result.Success = true;
                }
            }
            catch (WebException ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp)
                    {
                        result.StatusCode = (int)resp.StatusCode;
                        try { result.Body = ReadBody(resp); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        /// <summary>Checks the API host is reachable WITHOUT sending any data (a plain GET).</summary>
        public static HttpResult Probe(string url, int timeoutSeconds)
        {
            EnsureTls();
            var result = new HttpResult();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Accept = "application/json";
                req.UserAgent = "PharmacySync/" + AppInfo.Version;
                req.Timeout = timeoutSeconds * 1000;
                req.AllowAutoRedirect = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    result.StatusCode = (int)resp.StatusCode;
                    result.Success = true;
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp)
                    {
                        // Any HTTP answer (405, 401, 404 ...) means the server is reachable.
                        result.StatusCode = (int)resp.StatusCode;
                        result.Success = true;
                    }
                }
                else
                {
                    result.ErrorMessage = ex.Message;
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
#endif

    /// <summary>String helpers available on both .NET 3.5 and 4.x.</summary>
    public static class StrUtil
    {
        /// <summary>Same as string.IsNullOrWhiteSpace (which does not exist on .NET 3.5).</summary>
        public static bool IsBlank(string s)
        {
            if (s == null) return true;
            for (int i = 0; i < s.Length; i++) if (!char.IsWhiteSpace(s[i])) return false;
            return true;
        }
    }
}
