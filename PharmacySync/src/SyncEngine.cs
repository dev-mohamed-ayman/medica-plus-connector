using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace PharmacySync
{
    public class SyncOptions
    {
        public bool DryRun;
        public string Trigger = "Manual";
        public Func<bool> CancelRequested = () => false;
    }

    public class SyncOutcome
    {
        public string Result = RunResults.Failed;
        public string Message = "";
        public int ProductsSent;
        public int ProductsFailed;
        public int ProductsPrepared;
        public int BranchesCount;
        public int BranchErrors;       // branches that could not be read (e.g. remote branch offline)
        public string SamplePayload;
    }

    public class SyncCancelledException : Exception
    {
        public SyncCancelledException() : base("Sync was cancelled by the user.") { }
    }

    /// <summary>A sync implementation for one pharmacy program + mode.</summary>
    public interface ISyncJob
    {
        void Execute(ConnectionProfile p, SqlConnection conn, SyncLogger log, SyncOptions opt, SyncOutcome outcome);
    }

    public static class SyncJobs
    {
        public static ISyncJob Create(ConnectionProfile p)
        {
            if (string.Equals(p.Software, "Estock", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Mode == SyncModes.Multi) return new EstockMultiBranchJob();
                return new EstockSingleBranchJob();
            }
            if (string.Equals(p.Software, "PharmaFly", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Mode == SyncModes.Multi) return new PharmaFlyMultiBranchJob();
                return new PharmaFlySingleBranchJob();
            }
            throw new NotSupportedException("Program '" + p.Software + "' is not supported yet.");
        }
    }

    /// <summary>
    /// Runs a sync for a profile: single-instance lock, log file, status file, SQL connection,
    /// then delegates to the program-specific job.
    /// </summary>
    public static class SyncRunner
    {
        public static SyncOutcome Run(ConnectionProfile p, SyncOptions opt, Action<string, string> onLine)
        {
            var outcome = new SyncOutcome();
            var started = DateTime.Now;

            using (var mutex = CreateMutex(p.Id))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }

                    if (!owned)
                    {
                        outcome.Result = RunResults.Skipped;
                        outcome.Message = "مزامنة أخرى لنفس الربط تعمل الآن، تم تخطي هذا التشغيل.";
                        if (onLine != null) onLine("WARNING", "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [WARNING] Another sync for this connection is already running. Skipped.");
                        return outcome;
                    }

                    string logFile;
                    if (opt.DryRun)
                    {
                        logFile = Storage.DryRunLogFile(p.Id);
                    }
                    else
                    {
                        logFile = Storage.SyncLogFile(p.Id);
                        RotateLog(p.Id);
                    }

                    using (var log = new SyncLogger(logFile))
                    {
                        if (onLine != null) log.LineWritten += onLine;
                        RunCore(p, opt, log, outcome);
                    }
                }
                finally
                {
                    if (owned) mutex.ReleaseMutex();
                }
            }

            if (!opt.DryRun)
            {
                var finished = DateTime.Now;
                try
                {
                    Storage.SaveStatus(p.Id, new RunStatus
                    {
                        StartedAt = started.ToString("yyyy-MM-dd HH:mm:ss"),
                        FinishedAt = finished.ToString("yyyy-MM-dd HH:mm:ss"),
                        DurationSeconds = Math.Round((finished - started).TotalSeconds, 1),
                        Result = outcome.Result,
                        Message = outcome.Message,
                        Trigger = opt.Trigger,
                        ProductsSent = outcome.ProductsSent,
                        ProductsFailed = outcome.ProductsFailed,
                        BranchesCount = outcome.BranchesCount
                    });
                }
                catch (Exception ex)
                {
                    Storage.LogCrash("SaveStatus", ex);
                }
            }
            return outcome;
        }

        private static void RunCore(ConnectionProfile p, SyncOptions opt, SyncLogger log, SyncOutcome outcome)
        {
            var startTime = DateTime.Now;
            SqlConnection conn = null;
            try
            {
                log.Info("Starting Pharmacy Inventory Sync Agent...");
                log.Info("App: " + AppInfo.Name + " v" + AppInfo.Version + " | Connection: " + p.Name + " | Program: " + p.Software + " | Mode: " + p.Mode + " | Trigger: " + opt.Trigger);
                log.Info("Running as: " + SafeUserName());
                if (opt.DryRun) log.Warn("DRY RUN MODE: data will be read and prepared but NOTHING will be sent to the API.");
                log.Info("Configuration loaded successfully.");

                if (StrUtil.IsBlank(p.ApiUrl) && !opt.DryRun) throw new Exception("API URL is empty.");

                var job = SyncJobs.Create(p);

                log.Info("Connecting to SQL Server: " + p.SqlServer + "...");
                if (p.UsesWindowsAuth)
                    log.Info("Using Windows Authentication (Integrated Security).");
                else
                    log.Info("Using SQL Server Authentication (User: " + p.Username + ").");

                conn = new SqlConnection(SqlHelper.BuildConnectionString(p));
                try
                {
                    conn.Open();
                    log.Info("SQL Connection Status: SUCCESS");
                }
                catch (Exception ex)
                {
                    log.Error("SQL Connection Status: FAILED");
                    throw new Exception("Failed to connect to SQL Server: " + ex.Message, ex);
                }

                job.Execute(p, conn, log, opt, outcome);

                conn.Close();

                var endTime = DateTime.Now;
                log.Info("End Time: " + endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                log.Info("Duration: " + (endTime - startTime).TotalSeconds + " seconds");

                if (opt.DryRun)
                {
                    outcome.Result = outcome.BranchErrors > 0 ? RunResults.Partial : RunResults.Success;
                    outcome.Message = (outcome.BranchErrors > 0 ? "تجربة (تعذر قراءة " + outcome.BranchErrors + " فرع): " : "تجربة ناجحة: ") + "تم تجهيز " + outcome.ProductsPrepared + " منتج" + (outcome.BranchesCount > 0 ? " في " + outcome.BranchesCount + " فرع" : "") + " (بدون إرسال).";
                    log.Info("Dry run completed successfully.");
                }
                else if (outcome.BranchErrors > 0 && outcome.ProductsFailed == 0 && outcome.ProductsSent > 0)
                {
                    outcome.Result = RunResults.Partial;
                    outcome.Message = "تم إرسال " + outcome.ProductsSent + " منتج، لكن تعذر قراءة " + outcome.BranchErrors + " فرع. راجع السجل.";
                    log.Info("Sync completed successfully.");
                }
                else if (outcome.BranchErrors > 0 && outcome.ProductsSent == 0)
                {
                    outcome.Result = RunResults.Failed;
                    outcome.Message = "تعذر قراءة " + outcome.BranchErrors + " فرع ولم يتم إرسال أي بيانات. راجع السجل.";
                    log.Info("Sync completed successfully.");
                }
                else if (outcome.ProductsFailed > 0 && outcome.ProductsSent > 0)
                {
                    outcome.Result = RunResults.Partial;
                    outcome.Message = "تم إرسال " + outcome.ProductsSent + " منتج، وفشل " + outcome.ProductsFailed + ".";
                    log.Info("Sync completed successfully.");
                }
                else if (outcome.ProductsFailed > 0)
                {
                    outcome.Result = RunResults.Failed;
                    outcome.Message = "فشل إرسال كل البيانات (" + outcome.ProductsFailed + " منتج). راجع السجل.";
                    log.Info("Sync completed successfully.");
                }
                else
                {
                    outcome.Result = RunResults.Success;
                    outcome.Message = "تم إرسال " + outcome.ProductsSent + " منتج بنجاح" + (outcome.BranchesCount > 0 ? " (" + outcome.BranchesCount + " فرع)" : "") + ".";
                    log.Info("Sync completed successfully.");
                }
            }
            catch (SyncCancelledException ex)
            {
                outcome.Result = RunResults.Failed;
                outcome.Message = "تم إيقاف المزامنة يدوياً.";
                log.Warn(ex.Message);
            }
            catch (Exception ex)
            {
                outcome.Result = RunResults.Failed;
                outcome.Message = ex.Message;
                log.Error("CRITICAL ERROR: " + ex.Message);
                log.Error("Full Exception Stack Trace: " + ex);
            }
            finally
            {
                if (conn != null)
                {
                    try { if (conn.State == ConnectionState.Open) conn.Close(); } catch { }
                    conn.Dispose();
                }
            }
        }

        private static void RotateLog(string id)
        {
            try
            {
                var cur = Storage.SyncLogFile(id);
                var prev = Storage.PrevSyncLogFile(id);
                if (File.Exists(cur))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(cur, prev);
                }
            }
            catch { }
        }

        private static Mutex CreateMutex(string id)
        {
            var name = "Global\\PharmacySync_Run_" + id;
            try
            {
                var sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                return new Mutex(false, name, out created, sec);
            }
            catch (UnauthorizedAccessException)
            {
                return new Mutex(false, "Local\\PharmacySync_Run_" + id);
            }
        }

        private static string SafeUserName()
        {
            try { return WindowsIdentity.GetCurrent().Name; } catch { return Environment.UserName; }
        }

        // ------------------------------------------------------------------
        // Shared helpers for jobs (mirror PowerShell semantics)
        // ------------------------------------------------------------------

        internal static string Str(DataRow row, string col)
        {
            var v = row[col];
            if (v == null || v == DBNull.Value) return "";
            return v.ToString().Trim();
        }

        /// <summary>
        /// Same as Build-ProductObject in the scripts. Returns null (= skip) when the product has no code.
        /// </summary>
        internal static ProductItem BuildProduct(DataRow row, SyncLogger log, out string skipReason)
        {
            return BuildProductCore(Str(row, "code"), Str(row, "name_ar"), Str(row, "name_en"), row["price"], row["quantity"], Str(row, "international_barcode"), out skipReason);
        }

        /// <summary>Common product rules (identical for every program).</summary>
        internal static ProductItem BuildProductCore(string code, string nameAr, string nameEn, object priceValue, object quantityValue, string barcode, out string skipReason)
        {
            skipReason = null;
            code = (code ?? "").Trim();
            nameAr = (nameAr ?? "").Trim();
            nameEn = (nameEn ?? "").Trim();
            if (StrUtil.IsBlank(code))
            {
                skipReason = "no code";
                return null;
            }
            if (priceValue == null || priceValue == DBNull.Value)
            {
                // In the PowerShell script a NULL price crashes the whole run; here we only skip that product.
                skipReason = "NULL price";
                return null;
            }
            var price = Convert.ToDouble(priceValue);
            var qtyRaw = quantityValue == null || quantityValue == DBNull.Value ? 0.0 : Convert.ToDouble(quantityValue);

            var p = new ProductItem
            {
                Code = code,
                Price = price,
                Quantity = Convert.ToInt32(Math.Floor(qtyRaw)),
                Barcode = (barcode ?? "").Trim(),
                Image = ""
            };
            if (!StrUtil.IsBlank(nameAr)) p.NameAr = nameAr;
            if (!StrUtil.IsBlank(nameEn)) p.NameEn = nameEn;
            if (StrUtil.IsBlank(nameAr) && StrUtil.IsBlank(nameEn)) p.NameEn = "Product " + code;
            return p;
        }

        internal static void LogHttpFailure(SyncLogger log, string what, HttpResult r)
        {
            log.Error(what + " - HTTP Status Code: " + r.StatusCode);
            log.Error("API Error: " + r.ErrorMessage);
            if (!string.IsNullOrEmpty(r.Body)) log.Error("API Error Response: " + Truncate(r.Body, 4000));
        }

        internal static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        internal static void CheckCancel(SyncOptions opt)
        {
            if (opt.CancelRequested != null && opt.CancelRequested()) throw new SyncCancelledException();
        }
    }

    // ======================================================================
    // Estock - Single branch  (port of inventory-sync-estock / SyncInventory.ps1 v1.0.0)
    // ======================================================================
    public class EstockSingleBranchJob : ISyncJob
    {
        public const int BatchSize = 500;

        public const string ProductsQuery = @"
SELECT
    p.product_id,
    p.product_code AS code,
    p.product_name_ar AS name_ar,
    p.product_name_en AS name_en,
    CAST(p.sell_price AS FLOAT) AS price,
    ISNULL(SUM(CAST(pa.amount AS FLOAT)), 0) AS quantity,
    p.product_int_code AS international_barcode,
    '' AS image
FROM Products p
LEFT JOIN Product_Amount pa ON p.product_id = pa.product_id
WHERE ISNULL(p.deleted, 'N') != 'Y'
GROUP BY p.product_id, p.product_code, p.product_name_ar, p.product_name_en, p.sell_price, p.product_int_code
";

        public void Execute(ConnectionProfile p, SqlConnection conn, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            log.Info("Loading products and calculating stock...");
            var table = SqlHelper.Query(conn, ProductsQuery);
            log.Info("Number Of Products Loaded: " + table.Rows.Count);

            if (table.Rows.Count == 0)
            {
                log.Info("No products found to sync.");
                return;
            }

            log.Info("Preparing data for API...");
            var products = new List<ProductItem>(table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                string reason;
                var item = SyncRunner.BuildProduct(row, log, out reason);
                if (item == null)
                {
                    if (reason == "no code")
                        log.Warn("Skipping product without code (ID: " + row["product_id"] + ")");
                    else
                        log.Warn("Skipping product with " + reason + " (ID: " + row["product_id"] + ", Code: " + SyncRunner.Str(row, "code") + ")");
                    continue;
                }
                products.Add(item);
            }

            log.Info("Number Of Products After Filtering: " + products.Count);
            outcome.ProductsPrepared = products.Count;
            SendInBatches(p, products, log, opt, outcome);
        }

        /// <summary>Shared sending loop: batches of 500 with a 500 ms pause (used by every single-branch job).</summary>
        public static void SendInBatches(ConnectionProfile p, List<ProductItem> products, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            var totalBatches = (int)Math.Ceiling(products.Count / (double)BatchSize);
            log.Info("Splitting into " + totalBatches + " batches of " + BatchSize + " products each...");

            for (int i = 0; i < totalBatches; i++)
            {
                SyncRunner.CheckCancel(opt);
                var start = i * BatchSize;
                var count = Math.Min(BatchSize, products.Count - start);
                var batch = products.GetRange(start, count);

                log.Info("Processing Batch " + (i + 1) + "/" + totalBatches + " (" + batch.Count + " products)...");
                var json = Payload.Products(batch);
                if (outcome.SamplePayload == null) outcome.SamplePayload = json;

                if (opt.DryRun)
                {
                    log.Info("DRY RUN: Batch " + (i + 1) + " prepared (" + json.Length + " chars) - not sent.");
                    if (i == 0) log.Info("Payload preview (first 500 chars): " + SyncRunner.Truncate(json, 500));
                    continue;
                }

                log.Info("Sending Batch " + (i + 1) + " to API...");
                var r = HttpHelper.PostJson(p.ApiUrl, p.ApiKey, json, p.RequestTimeoutSeconds);
                if (r.Success)
                {
                    log.Info("Batch " + (i + 1) + " SUCCESS");
                    log.Info("API Response: " + SyncRunner.Truncate(r.Body, 2000));
                    outcome.ProductsSent += batch.Count;
                }
                else
                {
                    SyncRunner.LogHttpFailure(log, "Batch " + (i + 1) + " FAILED", r);
                    outcome.ProductsFailed += batch.Count;
                }

                // Small delay between batches to avoid overwhelming the server (same as script)
                Thread.Sleep(500);
            }

            if (!opt.DryRun)
            {
                log.Info("Sync Summary: Successfully sent " + outcome.ProductsSent + " products, Failed: " + outcome.ProductsFailed);
                if (outcome.ProductsFailed > 0) log.Warn("Some batches failed. Check logs above for details.");
            }
        }
    }

    // ======================================================================
    // Estock - Multi branch  (port of inventory-sync-estock-multi-branches / SyncInventory.ps1 v2.0.0)
    // ======================================================================
    public class EstockMultiBranchJob : ISyncJob
    {
        public const int ChunkSize = 1000;

        public const string ServerBranchQuery = "SELECT TOP 1 branch_id FROM co_inf WHERE branch_id IS NOT NULL";

        public const string BranchesQuery = @"
SELECT
    b.branch_id,
    b.branch_code,
    b.branch_name,
    b.branch_address,
    b.branch_tel,
    b.branch_mobile,
    b.active,
    b.is_server,
    b.barcode_name,
    b.barcode_tel
FROM Branches b
";

        public const string BranchProductsQuery = @"
SELECT
    bpa.branch_id,
    p.product_id,
    p.product_code AS code,
    p.product_name_ar AS name_ar,
    p.product_name_en AS name_en,
    CAST(p.sell_price AS FLOAT) AS price,
    SUM(CAST(bpa.amount AS FLOAT)) AS quantity,
    p.product_int_code AS international_barcode
FROM Products p
INNER JOIN Branches_Product_Amount bpa ON p.product_id = bpa.product_id
WHERE ISNULL(p.deleted, 'N') != 'Y'
GROUP BY bpa.branch_id, p.product_id, p.product_code, p.product_name_ar,
         p.product_name_en, p.sell_price, p.product_int_code
HAVING SUM(CAST(bpa.amount AS FLOAT)) > 0
";

        public const string ServerProductsQuery = @"
SELECT
    p.product_id,
    p.product_code AS code,
    p.product_name_ar AS name_ar,
    p.product_name_en AS name_en,
    CAST(p.sell_price AS FLOAT) AS price,
    SUM(CAST(pa.amount AS FLOAT)) AS quantity,
    p.product_int_code AS international_barcode
FROM Products p
INNER JOIN Product_Amount pa ON p.product_id = pa.product_id
WHERE ISNULL(p.deleted, 'N') != 'Y'
GROUP BY p.product_id, p.product_code, p.product_name_ar,
         p.product_name_en, p.sell_price, p.product_int_code
HAVING SUM(CAST(pa.amount AS FLOAT)) > 0
";

        public void Execute(ConnectionProfile p, SqlConnection conn, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            log.Info("Loading branches and products...");

            // Step 1: server (main) branch
            log.Info("Identifying server (main) branch...");
            var serverObj = SqlHelper.Scalar(conn, ServerBranchQuery);
            int? serverBranchId = null;
            if (serverObj != null && serverObj != DBNull.Value) serverBranchId = Convert.ToInt32(serverObj);
            log.Info("Server Branch ID: " + (serverBranchId.HasValue ? serverBranchId.Value.ToString() : ""));

            // Step 2: branches
            log.Info("Loading branches...");
            var branchesTable = SqlHelper.Query(conn, BranchesQuery);
            log.Info("Number Of Branches: " + branchesTable.Rows.Count);

            // Step 3: per-branch stock
            log.Info("Loading branch products stock...");
            var branchProductsTable = SqlHelper.Query(conn, BranchProductsQuery);
            log.Info("Branch product stock rows loaded: " + branchProductsTable.Rows.Count);

            // Step 4: server branch stock
            DataTable serverProductsTable = new DataTable();
            if (serverBranchId.HasValue)
            {
                log.Info("Loading server branch products stock...");
                serverProductsTable = SqlHelper.Query(conn, ServerProductsQuery);
                log.Info("Server branch products with stock > 0: " + serverProductsTable.Rows.Count);
            }

            SyncRunner.CheckCancel(opt);

            // Step 5: build structure
            log.Info("Building branch-based data structure...");
            var index = new Dictionary<int, List<ProductItem>>();
            foreach (DataRow row in branchProductsTable.Rows)
            {
                if (row["branch_id"] == DBNull.Value) continue;
                var brId = Convert.ToInt32(row["branch_id"]);
                string reason;
                var item = SyncRunner.BuildProduct(row, log, out reason);
                if (item == null)
                {
                    if (reason != "no code") log.Warn("Skipping product with " + reason + " (ID: " + row["product_id"] + ", Branch: " + brId + ")");
                    continue;
                }
                List<ProductItem> list;
                if (!index.TryGetValue(brId, out list))
                {
                    list = new List<ProductItem>();
                    index[brId] = list;
                }
                list.Add(item);
            }

            var branches = new List<BranchItem>();
            int totalProducts = 0;
            foreach (DataRow br in branchesTable.Rows)
            {
                if (br["branch_id"] == DBNull.Value) continue;
                var branchId = Convert.ToInt32(br["branch_id"]);
                var isServer = serverBranchId.HasValue && branchId == serverBranchId.Value;
                var products = new List<ProductItem>();

                if (isServer)
                {
                    foreach (DataRow row in serverProductsTable.Rows)
                    {
                        string reason;
                        var item = SyncRunner.BuildProduct(row, log, out reason);
                        if (item != null) products.Add(item);
                        else if (reason != "no code") log.Warn("Skipping product with " + reason + " (ID: " + row["product_id"] + ", server branch)");
                    }
                }
                List<ProductItem> extra;
                if (index.TryGetValue(branchId, out extra)) products.AddRange(extra);

                if (products.Count == 0) continue;
                totalProducts += products.Count;

                var rawActive = SyncRunner.Str(br, "active");
                log.Info("Branch '" + SyncRunner.Str(br, "branch_name") + "' raw active value: '" + rawActive + "'");

                var b = new BranchItem
                {
                    Code = SyncRunner.Str(br, "branch_code"),
                    Name = SyncRunner.Str(br, "branch_name"),
                    Address = SyncRunner.Str(br, "branch_address"),
                    // Same rule as script: branch_tel if not NULL (even if empty), otherwise branch_mobile
                    Phone = br["branch_tel"] != DBNull.Value ? br["branch_tel"].ToString().Trim() : SyncRunner.Str(br, "branch_mobile"),
                    Products = products
                };
                branches.Add(b);
                log.Info("Branch '" + b.Name + "' (Code: " + b.Code + "): " + products.Count + " products");
            }
            log.Info("Total branches with products: " + branches.Count);
            log.Info("Total products across all branches: " + totalProducts);
            outcome.BranchesCount = branches.Count;
            outcome.ProductsPrepared = totalProducts;

            if (branches.Count == 0)
            {
                log.Info("No branches with products to sync.");
                return;
            }
            SendBranches(p, branches, log, opt, outcome);
        }

        /// <summary>Shared sending loop: per branch, chunks of 1000 products (used by every multi-branch job).</summary>
        public static void SendBranches(ConnectionProfile p, List<BranchItem> branches, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            int okChunks = 0, failedChunks = 0;
            foreach (var branch in branches)
            {
                log.Info("Processing branch: " + branch.Name + " (total products: " + branch.Products.Count + ")");
                var chunks = (int)Math.Ceiling(branch.Products.Count / (double)ChunkSize);
                log.Info("Branch '" + branch.Name + "' split into " + chunks + " chunks");

                int bOk = 0, bFail = 0;
                for (int i = 0; i < chunks; i++)
                {
                    SyncRunner.CheckCancel(opt);
                    var chunkIndex = i + 1;
                    var start = i * ChunkSize;
                    var chunk = branch.Products.GetRange(start, Math.Min(ChunkSize, branch.Products.Count - start));

                    log.Info("Preparing and sending chunk " + chunkIndex + "/" + chunks + " for branch: " + branch.Name);
                    var json = Payload.Branch(branch, chunk);
                    if (outcome.SamplePayload == null) outcome.SamplePayload = json;
                    log.Info("Payload for " + branch.Name + " (chunk " + chunkIndex + ") - first 500 chars: " + SyncRunner.Truncate(json, 500));

                    if (opt.DryRun)
                    {
                        log.Info("DRY RUN: chunk " + chunkIndex + " for branch '" + branch.Name + "' prepared (" + chunk.Count + " products) - not sent.");
                        continue;
                    }

                    log.Info("Sending chunk " + chunkIndex + " for branch '" + branch.Name + "' to API...");
                    var r = HttpHelper.PostJson(p.ApiUrl, p.ApiKey, json, p.RequestTimeoutSeconds);
                    if (r.Success)
                    {
                        log.Info("SUCCESS: Chunk " + chunkIndex + " for branch '" + branch.Name + "' sent to API");
                        log.Info("API Response: " + SyncRunner.Truncate(r.Body, 2000));
                        bOk++; okChunks++;
                        outcome.ProductsSent += chunk.Count;
                    }
                    else
                    {
                        SyncRunner.LogHttpFailure(log, "FAILED - Chunk " + chunkIndex + " for branch '" + branch.Name + "'", r);
                        bFail++; failedChunks++;
                        outcome.ProductsFailed += chunk.Count;
                    }
                }
                if (!opt.DryRun) log.Info("Branch '" + branch.Name + "' complete: " + bOk + " chunks succeeded, " + bFail + " chunks failed.");
            }
            if (!opt.DryRun) log.Info("Sync complete: " + okChunks + " chunks succeeded, " + failedChunks + " chunks failed.");
        }
    }

    // ======================================================================
    // Pre-flight checks shown in the UI (tables exist + counts)
    // ======================================================================
    public static class EstockInspector
    {
        public static List<string> RequiredTables(string mode)
        {
            var t = new List<string> { "Products", "Product_Amount" };
            if (mode == SyncModes.Multi) t.AddRange(new[] { "Branches", "Branches_Product_Amount", "co_inf" });
            return t;
        }

        /// <summary>Returns lines like "✔ ..." / "✖ ..."; ok=false if anything required is missing.</summary>
        public static List<string> Inspect(SqlConnection conn, string mode, out bool ok)
        {
            ok = true;
            var lines = new List<string>();
            ok = TableCheck.Check(conn, RequiredTables(mode), lines);
            if (!ok) return lines;

            var products = SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM Products WHERE ISNULL(deleted, 'N') != 'Y'");
            lines.Add("✔ عدد المنتجات الفعالة: " + products);
            if (mode == SyncModes.Multi)
            {
                var branches = SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM Branches");
                lines.Add("✔ عدد الفروع: " + branches);
                var server = SqlHelper.Scalar(conn, EstockMultiBranchJob.ServerBranchQuery);
                lines.Add(server == null || server == DBNull.Value ? "⚠ لم يتم العثور على الفرع الرئيسي في co_inf" : "✔ الفرع الرئيسي (co_inf): " + server);
            }
            else
            {
                var hasBranches = SqlHelper.Scalar(conn, "SELECT OBJECT_ID(N'Branches_Product_Amount')") != DBNull.Value;
                if (hasBranches)
                {
                    var n = Convert.ToInt32(SqlHelper.Scalar(conn, "SELECT COUNT(DISTINCT branch_id) FROM Branches_Product_Amount"));
                    if (n > 0) lines.Add("⚠ قاعدة البيانات فيها مخزون لـ " + n + " فرع — لو العميل متعدد الفروع اختر (متعدد الفروع)");
                }
            }
            return lines;
        }
    }

    public static class TableCheck
    {
        /// <summary>One line when everything exists, otherwise one line per missing table.</summary>
        public static bool Check(System.Data.SqlClient.SqlConnection conn, IEnumerable<string> tables, List<string> lines)
        {
            var missing = new List<string>();
            var all = new List<string>(tables);
            foreach (var t in all)
                if (SqlHelper.Scalar(conn, "SELECT OBJECT_ID(N'" + t.Replace("'", "''") + "')") == DBNull.Value) missing.Add(t);
            if (missing.Count == 0) { lines.Add("✔ كل الجداول المطلوبة موجودة (" + string.Join(", ", all.ToArray()) + ")"); return true; }
            foreach (var m in missing) lines.Add("✖ الجدول " + m + " غير موجود");
            return false;
        }
    }
}
