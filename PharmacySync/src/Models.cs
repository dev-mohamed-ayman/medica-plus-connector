using System;
using System.Collections.Generic;

namespace PharmacySync
{
    public static class AppInfo
    {
        public const string Name = "Pharmacy Sync";
        public const string Version = "1.0.0";
        public const string FolderName = "PharmacySync";
        public const string ExeName = "PharmacySync.exe";
        public const string TaskFolder = "PharmacySync";
        public const string LegacyTaskName = "PharmacyInventorySync";
    }

    /// <summary>Supported pharmacy programs. Only those with Available = true can be used.</summary>
    public class SoftwareInfo
    {
        public string Id;
        public string Title;
        public string Subtitle;
        public bool Available;

        public static readonly SoftwareInfo[] All = new[]
        {
            new SoftwareInfo { Id = "Estock",    Title = "Estock",     Subtitle = "إي ستوك",  Available = true  },
            new SoftwareInfo { Id = "PharmaFly", Title = "Pharma Fly", Subtitle = "فارما فلاي", Available = true  },
            new SoftwareInfo { Id = "BConnect",  Title = "BConnect",   Subtitle = "قريباً",     Available = false },
        };

        public static SoftwareInfo Find(string id)
        {
            foreach (var s in All) if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
    }

    public static class SyncModes
    {
        public const string Single = "Single";
        public const string Multi = "Multi";

        public static string Title(string mode)
        {
            return mode == Multi ? "متعدد الفروع" : "فرع واحد";
        }
    }

    /// <summary>One client connection (equivalent of one script folder + config.json).</summary>
    public class ConnectionProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Software { get; set; }
        public string Mode { get; set; }

        public string SqlServer { get; set; }
        public string Database { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        public int SyncIntervalMinutes { get; set; }
        public int RequestTimeoutSeconds { get; set; }

        /// <summary>Pharma Fly multi-branch: also read other branches from their own servers (Branches table).</summary>
        public bool IncludeRemoteBranches { get; set; }

        public bool ScheduleEnabled { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }

        public ConnectionProfile()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            Software = "Estock";
            Mode = SyncModes.Single;
            SqlServer = ".\\SQLEXPRESS";
            Database = "stock";
            Username = "";
            Password = "";
            ApiUrl = "";
            ApiKey = "";
            SyncIntervalMinutes = 120;
            RequestTimeoutSeconds = 300;
        }

        public bool UsesWindowsAuth
        {
            get { return StrUtil.IsBlank(Username); }
        }

        public ConnectionProfile Clone()
        {
            return (ConnectionProfile)MemberwiseClone();
        }
    }

    public static class RunResults
    {
        public const string Success = "Success";
        public const string Partial = "Partial";
        public const string Failed = "Failed";
        public const string Skipped = "Skipped";
    }

    /// <summary>Result of the last real (non dry-run) sync for a profile.</summary>
    public class RunStatus
    {
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public double DurationSeconds { get; set; }
        public string Result { get; set; }
        public string Message { get; set; }
        public string Trigger { get; set; }
        public int ProductsSent { get; set; }
        public int ProductsFailed { get; set; }
        public int BranchesCount { get; set; }
    }

    /// <summary>A product row exactly as the PowerShell scripts send it.</summary>
    public class ProductItem
    {
        public string Code;
        public string NameAr;   // null = omitted
        public string NameEn;   // null = omitted
        public double Price;
        public int Quantity;
        public string Barcode;
        public string Image = "";
    }

    public class BranchItem
    {
        public string Code;
        public string Name;
        public string Address;
        public string Phone;
        public List<ProductItem> Products = new List<ProductItem>();
    }
}
