<#
  Compares a Pharma Fly stock report (Excel exported from Pharma Fly) with exactly what PharmacySync sends.

  Run it on the client machine (same database the report was exported from):
    powershell -ExecutionPolicy Bypass -File Compare-PharmaFlyReport.ps1 -Report "C:\path\Book1.xlsx" -Server ".\SQLEXPRESS" -Database pfx

  Optional: -User sa -Password xxx  (otherwise Windows Authentication)
            -Exe "C:\Program Files\PharmacySync\PharmacySync.exe"   (query is loaded from the exe so it is identical)

  Report columns used: ID, Name, Sales Price, Packs, Units (first sheet, first row = headers).
  Output: summary on screen + <report>-compare.csv with every difference.
#>
param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$Server = ".\SQLEXPRESS",
    [string]$Database = "pfx",
    [string]$User = "",
    [string]$Password = "",
    [string]$Exe
)
$ErrorActionPreference = "Stop"

# ---------- locate exe & load the exact query ----------
if (-not $Exe) {
    $cands = @((Join-Path $PSScriptRoot "..\dist\PharmacySync.exe"), (Join-Path $env:ProgramFiles "PharmacySync\PharmacySync.exe"), (Join-Path $PSScriptRoot "PharmacySync.exe"))
    $Exe = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Exe) { throw "PharmacySync.exe not found. Pass -Exe." }
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path $Exe)))
$q = $asm.GetType("PharmacySync.PharmaFlyQueries")
$allProducts = $q.GetField("AllProducts").GetValue($null)

# ---------- read xlsx (no Excel needed) ----------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Report))
function Read-Entry($name) { $e = $zip.GetEntry($name); if (-not $e) { return $null }; $r = New-Object IO.StreamReader($e.Open()); $t = $r.ReadToEnd(); $r.Close(); return $t }
$shared = @()
$ssXml = Read-Entry "xl/sharedStrings.xml"
if ($ssXml) { $x = [xml]$ssXml; foreach ($si in $x.sst.si) { $shared += ($si.SelectNodes(".//*[local-name()='t']") | ForEach-Object { $_.InnerText }) -join "" } }
$sheet = [xml](Read-Entry "xl/worksheets/sheet1.xml")
$zip.Dispose()
$rows = @()
foreach ($row in $sheet.worksheet.sheetData.row) {
    $h = @{}
    foreach ($c in $row.c) {
        $col = ($c.r -replace '\d', '')
        $v = $c.v
        if ($c.t -eq "s") { $v = $shared[[int]$v] } elseif ($c.t -eq "inlineStr") { $v = $c.is.InnerText }
        $h[$col] = $v
    }
    $rows += , $h
}
$header = $rows[0]
$map = @{}
foreach ($k in $header.Keys) { $map[("" + $header[$k]).Trim().ToLower()] = $k }
foreach ($need in "id", "name", "sales price", "packs", "units") { if (-not $map.ContainsKey($need)) { throw "Column '$need' not found in report header." } }

# ---------- query database (same SQL as the app + raw Work packs/units) ----------
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb["Data Source"] = $Server; $csb["Initial Catalog"] = $Database
if ($User) { $csb["User ID"] = $User; $csb["Password"] = $Password } else { $csb["Integrated Security"] = $true }
$conn = New-Object System.Data.SqlClient.SqlConnection($csb.ConnectionString); $conn.Open()
function Query($sql) { $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 600; $cmd.CommandText = $sql; $a = New-Object System.Data.SqlClient.SqlDataAdapter($cmd); $t = New-Object System.Data.DataTable; [void]$a.Fill($t); return , $t }
$app = @{}
foreach ($r in (Query $allProducts).Rows) { $app["" + $r.product_id] = $r }
$work = @{}
foreach ($r in (Query "SELECT w.item_id, SUM(ISNULL(w.Packs,0)) packs, SUM(ISNULL(w.Units,0)) units, MAX(i.Parts) parts FROM Work w JOIN Items i ON i.ID = w.item_id LEFT JOIN Stores s ON s.ID = w.StoreID WHERE ISNULL(s.Hidden,0) = 0 GROUP BY w.item_id").Rows) { $work["" + $r.item_id] = $r }
$items = @{}
foreach ($r in (Query "SELECT ID, name, SalesPrice, Parts, IsActive, Archived FROM Items").Rows) { $items["" + $r.ID] = $r }
$conn.Close()

# ---------- compare ----------
$diffs = New-Object System.Collections.Generic.List[object]
$stats = [ordered]@{ ReportRows = 0; OK = 0; NotInDatabase = 0; NotSentInactiveOrArchived = 0; NameDiff = 0; PriceDiff = 0; StockDiff = 0 }
for ($i = 1; $i -lt $rows.Count; $i++) {
    $r = $rows[$i]
    $id = ("" + $r[$map["id"]]).Trim(); if (-not $id) { continue }
    $stats.ReportRows++
    $rName = ("" + $r[$map["name"]]).Trim(); $rPrice = [double]$r[$map["sales price"]]
    $rPacks = [double]$r[$map["packs"]]; $rUnits = [double]$r[$map["units"]]
    $problems = @()
    if (-not $items.ContainsKey($id)) { $stats.NotInDatabase++; $problems += "item not in database" }
    elseif (-not $app.ContainsKey($id)) { $stats.NotSentInactiveOrArchived++; $problems += "item inactive/archived (not sent)" }
    else {
        $a = $app[$id]; $it = $items[$id]
        if (("" + $a.name).Trim() -ne $rName) { $stats.NameDiff++; $problems += "name" }
        if ([Math]::Abs([double]$a.price - $rPrice) -gt 0.0001) { $stats.PriceDiff++; $problems += "price" }
        $wp = 0; $wu = 0; if ($work.ContainsKey($id)) { $wp = [double]$work[$id].packs; $wu = [double]$work[$id].units }
        $parts = [double]$it.Parts; if ($parts -le 0) { $parts = 1 }
        if ([Math]::Abs(($wp + $wu / $parts) - ($rPacks + $rUnits / $parts)) -gt 0.0001) { $stats.StockDiff++; $problems += "stock" }
    }
    if ($problems.Count -eq 0) { $stats.OK++; continue }
    $a = $app[$id]
    $diffs.Add([pscustomobject]@{
        ID = $id; Problem = ($problems -join ", "); ReportName = $rName; AppName = if ($a) { $a.name } else { "" }
        ReportPrice = $rPrice; AppPrice = if ($a) { $a.price } else { "" }
        ReportPacks = $rPacks; ReportUnits = $rUnits
        WorkPacks = if ($work.ContainsKey($id)) { $work[$id].packs } else { 0 }; WorkUnits = if ($work.ContainsKey($id)) { $work[$id].units } else { 0 }
        QuantitySent = if ($a) { [Math]::Floor([double]$a.quantity) } else { "" }
    })
}
# items the app sends with stock that are NOT in the report
$reportIds = @{}; for ($i = 1; $i -lt $rows.Count; $i++) { $reportIds[("" + $rows[$i][$map["id"]]).Trim()] = 1 }
$extra = 0
foreach ($k in $app.Keys) {
    if ($reportIds.ContainsKey($k)) { continue }
    $qv = [double]$app[$k].quantity
    if ([Math]::Abs($qv) -gt 0.0001) {
        $extra++
        $diffs.Add([pscustomobject]@{ ID = $k; Problem = "has stock in database but missing from report"; ReportName = ""; AppName = $app[$k].name; ReportPrice = ""; AppPrice = $app[$k].price; ReportPacks = ""; ReportUnits = ""; WorkPacks = $work[$k].packs; WorkUnits = $work[$k].units; QuantitySent = [Math]::Floor($qv) })
    }
}
$stats["InDatabaseWithStockButNotInReport"] = $extra

$out = [IO.Path]::ChangeExtension((Resolve-Path $Report).Path, $null).TrimEnd('.') + "-compare.csv"
$diffs | Export-Csv -Path $out -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "========== Pharma Fly report vs PharmacySync ==========" -ForegroundColor Cyan
$stats.GetEnumerator() | ForEach-Object { "{0,-36} {1}" -f $_.Key, $_.Value }
Write-Host ""
if ($diffs.Count -eq 0) { Write-Host "100% MATCH" -ForegroundColor Green } else { Write-Host "Differences: $($diffs.Count)  ->  $out" -ForegroundColor Yellow }
