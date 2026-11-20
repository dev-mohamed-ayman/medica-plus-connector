#if NET35
// =============================================================================================
// Windows 7 build only (.NET 3.5).
//
// .NET 3.5 on a stock Windows 7 cannot speak TLS 1.2 (that needs KB3154518 + registry changes),
// and Windows 7 machines usually have an outdated root-certificate store (no ISRG Root X1 for
// Let's Encrypt, etc.). So for this build HTTPS is done with the bundled BouncyCastle TLS stack,
// and server certificates are verified against the Windows store PLUS the roots bundled in
// Win7\roots.pem. Nothing has to be installed on the customer's machine.
//
// BouncyCastle.Crypto.dll is embedded in the exe as a resource and loaded on demand.
// =============================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;

namespace PharmacySync
{
    /// <summary>Loads the embedded BouncyCastle assembly. Must run before any BouncyCastle type is touched.</summary>
    internal static class Win7Bootstrap
    {
        private static Assembly _bc;
        private static readonly object Lock = new object();

        public static void Init()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                if (!e.Name.StartsWith("BouncyCastle.Crypto", StringComparison.OrdinalIgnoreCase)) return null;
                lock (Lock)
                {
                    if (_bc == null)
                    {
                        var bytes = ReadResource("PharmacySync.BouncyCastle.Crypto.dll");
                        if (bytes != null) _bc = Assembly.Load(bytes);
                    }
                    return _bc;
                }
            };
        }

        internal static byte[] ReadResource(string name)
        {
            using (var s = typeof(Win7Bootstrap).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }
    }

    /// <summary>Minimal HTTP/1.1 client (POST/GET, chunked, gzip) over plain TCP or BouncyCastle TLS 1.2.</summary>
    internal static class Win7Http
    {
        private const int MaxRedirects = 5;

        private class Response
        {
            public int Status;
            public string Reason = "";
            public string Body = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static HttpResult PostJson(string url, string apiKey, string json, int timeoutSeconds)
        {
            var result = new HttpResult();
            try
            {
                var headers = new List<KeyValuePair<string, string>>();
                headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
                headers.Add(new KeyValuePair<string, string>("Accept", "application/json"));
                headers.Add(new KeyValuePair<string, string>("X-API-KEY", apiKey ?? ""));
                var resp = Send("POST", url, headers, Encoding.UTF8.GetBytes(json ?? ""), Math.Max(10, timeoutSeconds) * 1000, MaxRedirects);
                result.StatusCode = resp.Status;
                result.Body = resp.Body;
                if (resp.Status >= 200 && resp.Status < 300)
                {
                    result.Success = true;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "The remote server returned an error: (" + resp.Status + ") " + resp.Reason + ".";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = Describe(ex);
            }
            return result;
        }

        /// <summary>Checks the API host is reachable WITHOUT sending any data (a plain GET).</summary>
        public static HttpResult Probe(string url, int timeoutSeconds)
        {
            var result = new HttpResult();
            try
            {
                var headers = new List<KeyValuePair<string, string>>();
                headers.Add(new KeyValuePair<string, string>("Accept", "application/json"));
                var resp = Send("GET", url, headers, null, Math.Max(5, timeoutSeconds) * 1000, 0);
                // Any HTTP answer (405, 401, 404 ...) means the server is reachable.
                result.StatusCode = resp.Status;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = Describe(ex);
            }
            return result;
        }

        private static string Describe(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is Win7CertificateException) return e.Message;
            for (var e = ex; e != null; e = e.InnerException)
                if (e is TlsFatalAlert) return "TLS handshake failed: " + e.Message;
            var inner = ex;
            while (inner.InnerException != null && !(inner is SocketException)) inner = inner.InnerException;
            return inner.Message;
        }

        // ------------------------------------------------------------------ request

        private static Response Send(string method, string url, List<KeyValuePair<string, string>> headers, byte[] body, int timeoutMs, int redirectsLeft)
        {
            var uri = new Uri(url);
            bool https = uri.Scheme == Uri.UriSchemeHttps;
            if (!https && uri.Scheme != Uri.UriSchemeHttp) throw new NotSupportedException("Unsupported URL scheme: " + uri.Scheme);

            Response resp;
            using (var tcp = Connect(uri.Host, uri.Port, timeoutMs))
            {
                Stream stream = tcp.GetStream();
                TlsClientProtocol tls = null;
                try
                {
                    if (https)
                    {
                        tls = new TlsClientProtocol(stream, new SecureRandom());
                        tls.Connect(new Win7TlsClient(uri.Host));
                        stream = tls.Stream;
                    }

                    var sb = new StringBuilder();
                    sb.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                    sb.Append("Host: ").Append(uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port).Append("\r\n");
                    sb.Append("User-Agent: PharmacySync/").Append(AppInfo.Version).Append("\r\n");
                    sb.Append("Accept-Encoding: gzip, deflate\r\n");
                    sb.Append("Connection: close\r\n");
                    foreach (var h in headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                    if (body != null) sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                    sb.Append("\r\n");

                    var head = Encoding.UTF8.GetBytes(sb.ToString());
                    var packet = new byte[head.Length + (body == null ? 0 : body.Length)];
                    Buffer.BlockCopy(head, 0, packet, 0, head.Length);
                    if (body != null) Buffer.BlockCopy(body, 0, packet, head.Length, body.Length);
                    stream.Write(packet, 0, packet.Length);
                    stream.Flush();

                    resp = ReadResponse(stream);
                }
                finally
                {
                    if (tls != null) { try { tls.Close(); } catch { } }
                }
            }

            string location;
            if (redirectsLeft > 0 && IsRedirect(resp.Status) && resp.Headers.TryGetValue("Location", out location) && !StrUtil.IsBlank(location))
            {
                var next = new Uri(uri, location.Trim());
                return Send(method, next.ToString(), headers, body, timeoutMs, redirectsLeft - 1);
            }
            return resp;
        }

        private static bool IsRedirect(int s) { return s == 301 || s == 302 || s == 303 || s == 307 || s == 308; }

        private static TcpClient Connect(string host, int port, int timeoutMs)
        {
            var tcp = new TcpClient();
            try
            {
                var ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                    throw new TimeoutException("Connection to " + host + ":" + port + " timed out.");
                tcp.EndConnect(ar);
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;
                return tcp;
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        // ------------------------------------------------------------------ response

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static Response ReadResponse(Stream s)
        {
            var r = new Response();
            while (true)
            {
                var statusLine = ReadLine(s);
                if (statusLine == null) throw new IOException("The server closed the connection without sending a response.");
                var parts = statusLine.Split(new[] { ' ' }, 3);
                if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid HTTP response: " + statusLine);
                r.Status = int.Parse(parts[1]);
                r.Reason = parts.Length > 2 ? parts[2] : "";
                r.Headers.Clear();
                string line;
                while (!string.IsNullOrEmpty(line = ReadLine(s)))
                {
                    int idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var name = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    string prev;
                    r.Headers[name] = r.Headers.TryGetValue(name, out prev) ? prev + ", " + value : value;
                }
                if (r.Status >= 100 && r.Status < 200) continue; // 100 Continue etc.
                break;
            }

            byte[] raw;
            string te, cl, ce;
            if (r.Status == 204 || r.Status == 304) raw = new byte[0];
            else if (r.Headers.TryGetValue("Transfer-Encoding", out te) && te.ToLowerInvariant().Contains("chunked")) raw = ReadChunked(s);
            else if (r.Headers.TryGetValue("Content-Length", out cl)) raw = ReadExact(s, int.Parse(cl.Split(',')[0].Trim()));
            else raw = ReadToEnd(s);

            if (r.Headers.TryGetValue("Content-Encoding", out ce)) raw = Decompress(raw, ce.Trim().ToLowerInvariant());

            int start = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            r.Body = Encoding.UTF8.GetString(raw, start, raw.Length - start);
            return r;
        }

        private static string ReadLine(Stream s)
        {
            var ms = new MemoryStream();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return ms.Length == 0 ? null : Latin1.GetString(ms.ToArray());
                if (b == '\n') break;
                if (b != '\r') ms.WriteByte((byte)b);
                if (ms.Length > 65536) throw new IOException("HTTP header line too long.");
            }
            return Latin1.GetString(ms.ToArray());
        }

        private static byte[] ReadExact(Stream s, int count)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new IOException("The connection was closed before the full response was received.");
                off += n;
            }
            return buf;
        }

        private static byte[] ReadChunked(Stream s)
        {
            var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = ReadLine(s);
                if (sizeLine == null) throw new IOException("The connection was closed in the middle of a chunked response.");
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                int size = Convert.ToInt32(sizeLine.Trim(), 16);
                if (size == 0)
                {
                    string trailer;
                    while (!string.IsNullOrEmpty(trailer = ReadLine(s))) { }
                    break;
                }
                var chunk = ReadExact(s, size);
                ms.Write(chunk, 0, chunk.Length);
                ReadLine(s); // CRLF after the chunk
            }
            return ms.ToArray();
        }

        private static byte[] ReadToEnd(Stream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            }
            catch (IOException)
            {
                // Many servers close TLS without close_notify; keep what we already received.
                if (ms.Length == 0) throw;
            }
            return ms.ToArray();
        }

        private static byte[] Decompress(byte[] data, string encoding)
        {
            if (data.Length == 0) return data;
            Stream z;
            if (encoding.Contains("gzip"))
            {
                z = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            }
            else if (encoding.Contains("deflate"))
            {
                // Most servers send zlib-wrapped deflate; DeflateStream wants the raw stream.
                int skip = (data.Length > 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0) ? 2 : 0;
                z = new DeflateStream(new MemoryStream(data, skip, data.Length - skip), CompressionMode.Decompress);
            }
            else
            {
                return data;
            }
            using (z)
            {
                var ms = new MemoryStream();
                var buf = new byte[16384];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }
    }

    // ---------------------------------------------------------------------- TLS

    internal class Win7TlsClient : DefaultTlsClient
    {
        private readonly string _host;

        public Win7TlsClient(string host) { _host = host; }

        public override IDictionary GetClientExtensions()
        {
            var ext = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());
            IPAddress ip;
            if (!IPAddress.TryParse(_host, out ip))
            {
                var names = new ArrayList();
                names.Add(new ServerName(NameType.host_name, _host)); // SNI (required by Cloudflare / shared hosting)
                TlsExtensionsUtilities.AddServerNameExtension(ext, new ServerNameList(names));
            }
            return ext;
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new Win7TlsAuthentication(_host);
        }
    }

    internal class Win7TlsAuthentication : TlsAuthentication
    {
        private readonly string _host;

        public Win7TlsAuthentication(string host) { _host = host; }

        public void NotifyServerCertificate(Certificate serverCertificate)
        {
            var list = serverCertificate == null ? null : serverCertificate.GetCertificateList();
            if (list == null || list.Length == 0) throw new Win7CertificateException("The server did not send a certificate.");
            Win7CertValidator.Validate(_host, list);
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            return null; // no client certificate
        }
    }

    internal class Win7CertificateException : IOException
    {
        public Win7CertificateException(string message) : base(message) { }
    }

    internal static class Win7CertValidator
    {
        private static List<X509Certificate2> _roots;
        private static Dictionary<string, bool> _rootThumbprints;

        private static void LoadRoots()
        {
            if (_roots != null) return;
            var roots = new List<X509Certificate2>();
            var thumbs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var bytes = Win7Bootstrap.ReadResource("PharmacySync.roots.pem");
            if (bytes != null)
            {
                var text = Encoding.ASCII.GetString(bytes);
                const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
                int pos = 0;
                while ((pos = text.IndexOf(begin, pos, StringComparison.Ordinal)) >= 0)
                {
                    int stop = text.IndexOf(end, pos, StringComparison.Ordinal);
                    if (stop < 0) break;
                    var b64 = text.Substring(pos + begin.Length, stop - pos - begin.Length);
                    try
                    {
                        var cert = new X509Certificate2(Convert.FromBase64String(b64));
                        roots.Add(cert);
                        thumbs[cert.Thumbprint] = true;
                    }
                    catch { }
                    pos = stop + end.Length;
                }
            }
            _rootThumbprints = thumbs;
            _roots = roots;
        }

        public static void Validate(string host, X509CertificateStructure[] list)
        {
            LoadRoots();
            var leaf = new X509Certificate2(list[0].GetEncoded());

            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
            for (int i = 1; i < list.Length; i++)
                chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(list[i].GetEncoded()));
            foreach (var r in _roots) chain.ChainPolicy.ExtraStore.Add(r);
            chain.Build(leaf);

            var elements = chain.ChainElements;
            if (elements.Count == 0) throw new Win7CertificateException("Server certificate rejected: could not build a certificate chain.");
            var top = elements[elements.Count - 1].Certificate;
            bool topIsBundled = _rootThumbprints.ContainsKey(top.Thumbprint);

            foreach (X509ChainElement el in elements)
            {
                foreach (X509ChainStatus st in el.ChainElementStatus)
                {
                    var f = st.Status;
                    if (f == X509ChainStatusFlags.NoError) continue;
                    if (f == X509ChainStatusFlags.RevocationStatusUnknown || f == X509ChainStatusFlags.OfflineRevocation) continue;
                    if (f == X509ChainStatusFlags.UntrustedRoot && topIsBundled) continue;
                    throw new Win7CertificateException("Server certificate rejected (" + f + "): " + (st.StatusInformation ?? "").Trim()
                        + " [" + el.Certificate.Subject + "]");
                }
            }
            foreach (X509ChainStatus st in chain.ChainStatus)
            {
                if (st.Status == X509ChainStatusFlags.PartialChain)
                    throw new Win7CertificateException("Server certificate rejected: the certificate chain is incomplete.");
            }

            CheckHostName(host, list[0], leaf);
        }

        private static void CheckHostName(string host, X509CertificateStructure bcCert, X509Certificate2 leaf)
        {
            var names = new List<string>();
            try
            {
                var c = new Org.BouncyCastle.X509.X509Certificate(bcCert);
                var sans = c.GetSubjectAlternativeNames();
                if (sans != null)
                {
                    foreach (IList entry in sans)
                    {
                        if (entry == null || entry.Count < 2) continue;
                        int tag = Convert.ToInt32(entry[0]);
                        if (tag == GeneralName.DnsName || tag == GeneralName.IPAddress) names.Add(Convert.ToString(entry[1]));
                    }
                }
            }
            catch { }
            if (names.Count == 0)
            {
                var cn = leaf.GetNameInfo(X509NameType.DnsName, false);
                if (!StrUtil.IsBlank(cn)) names.Add(cn);
            }

            foreach (var n in names)
                if (HostMatches(host, n)) return;

            throw new Win7CertificateException("Server certificate rejected: it is not valid for '" + host + "' (valid for: " + string.Join(", ", names.ToArray()) + ").");
        }

        private static bool HostMatches(string host, string pattern)
        {
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            pattern = (pattern ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (pattern.Length == 0) return false;
            if (host == pattern) return true;
            if (pattern.StartsWith("*."))
            {
                var suffix = pattern.Substring(1); // ".example.com"
                if (!host.EndsWith(suffix)) return false;
                var label = host.Substring(0, host.Length - suffix.Length);
                return label.Length > 0 && label.IndexOf('.') < 0;
            }
            return false;
        }
    }
}
#endif
