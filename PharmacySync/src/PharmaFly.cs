using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;

namespace PharmacySync
{
    // ======================================================================
    // Pharma Fly (database "pfx")
    //
    // Mapping to the same payload Estock sends:
    //   code                  = Items.ID                (Pharma Fly item code)
    //   name_en / name_ar     = Items.name              (Latin → name_en, Arabic → name_ar, mixed → both)
    //   price                 = Items.SalesPrice
    //   quantity              = Σ Work (Packs + Units / Items.Parts) in all non-hidden stores, floored
    //                           (Work = live stock per item / expiry / store; hidden store = damaged & expired)
    //   international_barcode = first CrossRefrences.ref of the item
    //   image                 = ""
    // Products: Items that are active and not archived (like Estock "deleted != 'Y'").
    //
    // Branches: every Pharma Fly branch has its OWN database. The local database tells which branch it is
    // (BranchIdentity.BranchGuid → Branches). Other branches can optionally be read from their own servers
    // using the connection data saved by Pharma Fly in the Branches table.
    // ======================================================================
    public static class PharmaFlyQueries
    {
        /// <summary>Stock per item from Work, ignoring hidden stores (damaged / expired).</summary>
        private const string StockSubQuery = @"
    SELECT w.item_id,
           SUM(CAST(ISNULL(w.Packs, 0) AS FLOAT)
               + ISNULL(CAST(ISNULL(w.Units, 0) AS FLOAT) / NULLIF(CAST(it.Parts AS FLOAT), 0), 0)) AS quantity
    FROM Work w
    INNER JOIN Items it ON it.ID = w.item_id
    LEFT JOIN Stores st ON st.ID = w.StoreID
    WHERE ISNULL(st.Hidden, 0) = 0
    GROUP BY w.item_id";

        private const string ProductColumns = @"
SELECT
    i.ID AS product_id,
    CAST(i.ID AS VARCHAR(20)) AS code,
    i.name AS name,
    CAST(i.SalesPrice AS FLOAT) AS price,
    {QTY} AS quantity,
    bc.ref AS international_barcode
FROM Items i
{JOIN} (" + StockSubQuery + @"
) s ON s.item_id = i.ID
OUTER APPLY (
    SELECT TOP 1 LTRIM(RTRIM(c.ref)) AS ref
    FROM CrossRefrences c
    WHERE c.itemID = i.ID AND ISNULL(c.IsActive, 1) = 1 AND LTRIM(RTRIM(ISNULL(c.ref, ''))) <> ''
    ORDER BY c.Id
) bc
WHERE ISNULL(i.IsActive, 1) = 1 AND ISNULL(i.Archived, 0) = 0";

        /// <summary>Single branch: ALL active items, quantity 0 when no stock (same as Estock single: LEFT JOIN + ISNULL).</summary>
        public static readonly string AllProducts = ProductColumns.Replace("{QTY}", "ISNULL(s.quantity, 0)").Replace("{JOIN}", "LEFT JOIN");

        /// <summary>Multi branch: only items with stock &gt; 0 (same as Estock multi: HAVING SUM(...) &gt; 0).</summary>
        public static readonly string ProductsInStock = ProductColumns.Replace("{QTY}", "s.quantity").Replace("{JOIN}", "INNER JOIN") + " AND s.quantity > 0";

        public const string SelfBranch = @"
SELECT TOP 1 b.ID, b.BranchName, b.BranchGuid
FROM Branches b
WHERE b.BranchGuid = (SELECT TOP 1 BranchGuid FROM BranchIdentity)
   OR (NOT EXISTS (SELECT 1 FROM BranchIdentity bi INNER JOIN Branches b2 ON b2.BranchGuid = bi.BranchGuid) AND b.IsSelfBranch = 1)
ORDER BY CASE WHEN b.BranchGuid = (SELECT TOP 1 BranchGuid FROM BranchIdentity) THEN 0 ELSE 1 END, b.ID";

        public const string AllBranches = @"
SELECT b.ID, b.BranchName, b.IpAddress, b.DatabaseName, b.UserLogin, b.UserPassword, b.BranchGuid, b.IsSelfBranch, b.IsActive
FROM Branches b
ORDER BY b.ID";

        public const string FirmInfo = "SELECT TOP 1 FirmNameA, FirmNameE, FirmTel, FirmTel2, FirmAddress FROM Settings ORDER BY ID";
    }

    internal static class PharmaFly
    {
        private static bool HasArabic(string s)
        {
            foreach (var c in s) if (c >= (char)0x0600 && c <= (char)0x06FF) return true;
            return false;
        }

        private static bool HasLatin(string s)
        {
            foreach (var c in s) if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return true;
            return false;
        }

        /// <summary>Loads products (all items or only items in stock) from one Pharma Fly database.</summary>
        public static List<ProductItem> LoadProducts(SqlConnection conn, bool onlyInStock, SyncLogger log, string context, out int rowsLoaded)
        {
            var table = SqlHelper.Query(conn, onlyInStock ? PharmaFlyQueries.ProductsInStock : PharmaFlyQueries.AllProducts);
            rowsLoaded = table.Rows.Count;
            var list = new List<ProductItem>(table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                var name = SyncRunner.Str(row, "name");
                string nameAr = "", nameEn = "";
                if (HasArabic(name)) nameAr = name;
                if (HasLatin(name) || !HasArabic(name)) nameEn = name;

                string reason;
                var item = SyncRunner.BuildProductCore(SyncRunner.Str(row, "code"), nameAr, nameEn, row["price"], row["quantity"],
                    SyncRunner.Str(row, "international_barcode"), out reason);
                if (item == null)
                {
                    log.Warn("Skipping product with " + reason + " (ID: " + row["product_id"] + context + ")");
                    continue;
                }
                list.Add(item);
            }
            return list;
        }

        public class BranchRef
        {
            public string Id;
            public string Name;
            public string Guid;
            public string Server;
            public string Database;
            public string User;
            public string Password;
            public bool IsActive;
        }

        public static BranchRef ReadSelfBranch(SqlConnection conn)
        {
            var t = SqlHelper.Query(conn, PharmaFlyQueries.SelfBranch);
            if (t.Rows.Count == 0) return null;
            var r = t.Rows[0];
            return new BranchRef { Id = SyncRunner.Str(r, "ID"), Name = SyncRunner.Str(r, "BranchName"), Guid = SyncRunner.Str(r, "BranchGuid"), IsActive = true };
        }

        public static List<BranchRef> ReadAllBranches(SqlConnection conn)
        {
            var list = new List<BranchRef>();
            foreach (DataRow r in SqlHelper.Query(conn, PharmaFlyQueries.AllBranches).Rows)
            {
                list.Add(new BranchRef
                {
                    Id = SyncRunner.Str(r, "ID"),
                    Name = SyncRunner.Str(r, "BranchName"),
                    Guid = SyncRunner.Str(r, "BranchGuid"),
                    Server = SyncRunner.Str(r, "IpAddress"),
                    Database = SyncRunner.Str(r, "DatabaseName"),
                    User = SyncRunner.Str(r, "UserLogin"),
                    Password = r["UserPassword"] == DBNull.Value ? "" : r["UserPassword"].ToString(),
                    IsActive = r["IsActive"] == DBNull.Value || Convert.ToBoolean(r["IsActive"])
                });
            }
            return list;
        }

        /// <summary>Reads address/phone/name of the pharmacy saved in this database's Settings.</summary>
        public static void ReadFirm(SqlConnection conn, out string nameAr, out string address, out string phone)
        {
            nameAr = ""; address = ""; phone = "";
            var t = SqlHelper.Query(conn, PharmaFlyQueries.FirmInfo);
            if (t.Rows.Count == 0) return;
            var r = t.Rows[0];
            nameAr = SyncRunner.Str(r, "FirmNameA");
            if (nameAr.Length == 0) nameAr = SyncRunner.Str(r, "FirmNameE");
            address = SyncRunner.Str(r, "FirmAddress");
            phone = SyncRunner.Str(r, "FirmTel");
            if (phone.Length == 0) phone = SyncRunner.Str(r, "FirmTel2");
        }

        public static string RemoteConnectionString(BranchRef b)
        {
            return SqlHelper.BuildConnectionString(b.Server, StrUtil.IsBlank(b.Database) ? "pfx" : b.Database, b.User, b.Password, 15);
        }
    }

    // ======================================================================
    // Pharma Fly - Single branch (same flow as Estock single: all products, batches of 500, 500 ms delay)
    // ======================================================================
    public class PharmaFlySingleBranchJob : ISyncJob
    {
        public void Execute(ConnectionProfile p, SqlConnection conn, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            log.Info("Loading products and calculating stock (Pharma Fly: Items + Work, hidden stores excluded)...");
            int rows;
            var products = PharmaFly.LoadProducts(conn, false, log, "", out rows);
            log.Info("Number Of Products Loaded: " + rows);

            if (rows == 0)
            {
                log.Info("No products found to sync.");
                return;
            }

            log.Info("Number Of Products After Filtering: " + products.Count);
            outcome.ProductsPrepared = products.Count;
            EstockSingleBranchJob.SendInBatches(p, products, log, opt, outcome);
        }
    }

    // ======================================================================
    // Pharma Fly - Multi branch (same flow as Estock multi: per branch, products with stock > 0, chunks of 1000)
    // ======================================================================
    public class PharmaFlyMultiBranchJob : ISyncJob
    {
        public void Execute(ConnectionProfile p, SqlConnection conn, SyncLogger log, SyncOptions opt, SyncOutcome outcome)
        {
            var branches = new List<BranchItem>();

            // --- Step 1: this database's own branch ---
            log.Info("Identifying this database's branch (BranchIdentity → Branches)...");
            var self = PharmaFly.ReadSelfBranch(conn);
            string firmName, firmAddress, firmPhone;
            PharmaFly.ReadFirm(conn, out firmName, out firmAddress, out firmPhone);
            var selfBranch = new BranchItem
            {
                Code = self != null && self.Id.Length > 0 ? self.Id : "1",
                Name = self != null && self.Name.Length > 0 ? self.Name : (firmName.Length > 0 ? firmName : p.Name),
                Address = firmAddress,
                Phone = firmPhone
            };
            if (self == null) log.Warn("Branch identity not found in Branches table. Using code '1' and pharmacy name '" + selfBranch.Name + "'.");
            log.Info("This branch: '" + selfBranch.Name + "' (Code: " + selfBranch.Code + ")");

            log.Info("Loading branch products stock (stock > 0)...");
            int rows;
            selfBranch.Products = PharmaFly.LoadProducts(conn, true, log, ", branch " + selfBranch.Code, out rows);
            log.Info("Branch '" + selfBranch.Name + "' products with stock > 0: " + selfBranch.Products.Count);
            if (selfBranch.Products.Count > 0) branches.Add(selfBranch);

            // --- Step 2: other branches from their own servers (optional) ---
            if (p.IncludeRemoteBranches)
            {
                var all = PharmaFly.ReadAllBranches(conn);
                log.Info("Number Of Branches in Branches table: " + all.Count);
                foreach (var b in all)
                {
                    SyncRunner.CheckCancel(opt);
                    if (self != null && (b.Id == self.Id || (b.Guid.Length > 0 && b.Guid == self.Guid))) continue;
                    if (!b.IsActive) { log.Info("Branch '" + b.Name + "' is not active. Skipped."); continue; }
                    if (StrUtil.IsBlank(b.Server)) { log.Warn("Branch '" + b.Name + "' has no server address in Branches table. Skipped."); continue; }

                    log.Info("Connecting to branch '" + b.Name + "' server: " + b.Server + " / " + (StrUtil.IsBlank(b.Database) ? "pfx" : b.Database) + "...");
                    try
                    {
                        using (var rc = new SqlConnection(PharmaFly.RemoteConnectionString(b)))
                        {
                            rc.Open();
                            log.Info("Branch '" + b.Name + "' SQL Connection Status: SUCCESS");
                            string rn, ra, rp;
                            PharmaFly.ReadFirm(rc, out rn, out ra, out rp);
                            var item = new BranchItem { Code = b.Id, Name = b.Name.Length > 0 ? b.Name : rn, Address = ra, Phone = rp };
                            int rr;
                            item.Products = PharmaFly.LoadProducts(rc, true, log, ", branch " + b.Id, out rr);
                            log.Info("Branch '" + item.Name + "' products with stock > 0: " + item.Products.Count);
                            if (item.Products.Count > 0) branches.Add(item);
                        }
                    }
                    catch (SyncCancelledException) { throw; }
                    catch (Exception ex)
                    {
                        outcome.BranchErrors++;
                        log.Error("Branch '" + b.Name + "' FAILED to load: " + ex.Message);
                    }
                }
            }
            else
            {
                log.Info("Other branches are not included (install the app on each branch, or enable 'read other branches' in the connection settings).");
            }

            int total = 0;
            foreach (var b in branches) total += b.Products.Count;
            log.Info("Total branches with products: " + branches.Count);
            log.Info("Total products across all branches: " + total);
            outcome.BranchesCount = branches.Count;
            outcome.ProductsPrepared = total;

            if (branches.Count == 0)
            {
                log.Info("No branches with products to sync.");
                return;
            }
            EstockMultiBranchJob.SendBranches(p, branches, log, opt, outcome);
        }
    }

    // ======================================================================
    // Pre-flight checks for the wizard
    // ======================================================================
    public static class PharmaFlyInspector
    {
        public static List<string> Inspect(SqlConnection conn, string mode, bool includeRemote, out bool ok)
        {
            ok = true;
            var lines = new List<string>();
            var tables = new List<string> { "Items", "Work", "Stores", "CrossRefrences", "Settings" };
            if (mode == SyncModes.Multi) tables.AddRange(new[] { "Branches", "BranchIdentity" });
            ok = TableCheck.Check(conn, tables, lines);
            if (!ok) return lines;

            lines.Add("✔ عدد الأصناف الفعالة: " + SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM Items WHERE ISNULL(IsActive, 1) = 1 AND ISNULL(Archived, 0) = 0"));
            lines.Add("✔ أصناف لها رصيد في المخزن: " + SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM (" + PharmaFlyQueries.ProductsInStock + ") x"));
            var hidden = SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM Stores WHERE ISNULL(Hidden, 0) = 1");
            if (Convert.ToInt32(hidden) > 0) lines.Add("ℹ مخازن مخفية (هالك/منتهي) غير محسوبة في الرصيد: " + hidden);

            var branchesExist = SqlHelper.Scalar(conn, "SELECT OBJECT_ID(N'Branches')") != DBNull.Value;
            if (mode == SyncModes.Multi)
            {
                var self = PharmaFly.ReadSelfBranch(conn);
                lines.Add(self == null ? "⚠ لم يتم التعرف على فرع هذه القاعدة (سيتم استخدام اسم الصيدلية وكود 1)" : "✔ فرع هذه القاعدة: " + self.Name + " (كود " + self.Id + ")");
                var all = PharmaFly.ReadAllBranches(conn);
                lines.Add("✔ عدد الفروع المسجلة: " + all.Count);
                if (includeRemote)
                {
                    foreach (var b in all)
                    {
                        if (self != null && b.Id == self.Id) continue;
                        if (!b.IsActive || StrUtil.IsBlank(b.Server)) continue;
                        try
                        {
                            using (var rc = new SqlConnection(SqlHelper.BuildConnectionString(b.Server, StrUtil.IsBlank(b.Database) ? "pfx" : b.Database, b.User, b.Password, 5)))
                            {
                                rc.Open();
                                lines.Add("✔ فرع " + b.Name + ": متصل");
                            }
                        }
                        catch (Exception ex)
                        {
                            lines.Add("⚠ فرع " + b.Name + ": غير متاح (" + SqlHelper.ShortError(ex) + ")");
                        }
                    }
                }
            }
            else if (branchesExist)
            {
                var n = Convert.ToInt32(SqlHelper.Scalar(conn, "SELECT COUNT(*) FROM Branches"));
                if (n > 1) lines.Add("⚠ البرنامج مسجل فيه " + n + " فروع — لو العميل متعدد الفروع اختر (متعدد الفروع)");
            }
            return lines;
        }
    }

    public static class Inspectors
    {
        public static List<string> Inspect(string software, SqlConnection conn, string mode, bool includeRemote, out bool ok)
        {
            if (string.Equals(software, "PharmaFly", StringComparison.OrdinalIgnoreCase))
                return PharmaFlyInspector.Inspect(conn, mode, includeRemote, out ok);
            return EstockInspector.Inspect(conn, mode, out ok);
        }
    }
}
