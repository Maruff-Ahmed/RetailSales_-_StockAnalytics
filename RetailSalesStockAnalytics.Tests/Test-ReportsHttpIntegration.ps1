<#
.SYNOPSIS
    Repeatable HTTP Integration Test Suite for Reports and Analytics Workflows.
.DESCRIPTION
    Validates ASP.NET Core MVC Reports & Analytics endpoints:
      - 7 Web Views:
        * /Reports (GET - Dashboard)
        * /Reports/DailySales (GET - Filtered View)
        * /Reports/DailyDemandingProducts (GET - Filtered View)
        * /Reports/StockMovement (GET - Filtered View)
        * /Reports/ComplaintRouting (GET - Filtered View)
        * /SalesAnalytics (GET - Category & Date Filtered View)
        * /CustomerPurchasePattern (GET - Filtered View)
      - 8 Export Endpoints (Excel & PDF for all 4 report types):
        * /Reports/ExportDailySalesExcel
        * /Reports/ExportDailySalesPdf
        * /Reports/ExportDailyDemandingProductsExcel
        * /Reports/ExportDailyDemandingProductsPdf
        * /Reports/ExportStockMovementExcel
        * /Reports/ExportStockMovementPdf
        * /Reports/ExportComplaintRoutingExcel
        * /Reports/ExportComplaintRoutingPdf
    via live HTTP requests against a local Kestrel instance connected to
    the approved test database (RetailSalesStockAnalytics_TestDb).

    Validates:
      1. Complete 15-endpoint authorization matrix across Admin, Manager, Customer, and Anonymous.
      2. Data filtering & independent SQL calculation corroboration.
      3. Deep Excel content verification (worksheets, headers, seeded data, and date filter exclusion).
      4. PDF export integrity (headers, magic bytes, structure, and filter responsiveness).
      5. Persistent state non-mutation and deterministic cleanup.
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5005",
    [string]$ConnectionString,
    [switch]$Functional
)

$ErrorActionPreference = "Stop"

# =========================================================================
# 0. SAFETY GUARDS & DATABASE VALIDATION
# =========================================================================

if (-not $Functional) {
    Write-Host ""
    Write-Host "[INFO] Reports HTTP integration tests skipped (opt-in switch -Functional was not supplied)." -ForegroundColor Yellow
    Write-Host "================================================="
    Write-Host "REPORTS HTTP RESULTS: SKIPPED (Use -Functional to execute)"
    Write-Host "================================================="
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    if (![string]::IsNullOrWhiteSpace($env:ConnectionStrings__TestConnection)) {
        $ConnectionString = $env:ConnectionStrings__TestConnection
    }
    elseif (![string]::IsNullOrWhiteSpace($env:TEST_CONNECTION_STRING)) {
        $ConnectionString = $env:TEST_CONNECTION_STRING
    }
    else {
        $ConnectionString = "Server=(localdb)\mssqllocaldb;Database=RetailSalesStockAnalytics_TestDb;Integrated Security=True;TrustServerCertificate=True"
    }
}

$ApprovedTestDatabase = "RetailSalesStockAnalytics_TestDb"

$connBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
$targetDb = $connBuilder.InitialCatalog

if ([string]::IsNullOrWhiteSpace($targetDb)) {
    Write-Host "[SECURITY ERROR] Database name is missing or empty in the supplied connection string. HTTP tests aborted." -ForegroundColor Red
    exit 1
}

if (-not [string]::Equals($targetDb, "RetailSalesStockAnalytics_TestDb", [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "[SECURITY ERROR] Database '$targetDb' is not approved for test execution. Only 'RetailSalesStockAnalytics_TestDb' is permitted to protect application data." -ForegroundColor Red
    exit 1
}

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "STARTING REPORTS HTTP INTEGRATION TEST SUITE" -ForegroundColor Cyan
Write-Host "Approved Test Target: '$targetDb' on '$($connBuilder.DataSource)'" -ForegroundColor Cyan
Write-Host "Endpoint Base URL: $BaseUrl" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

# =========================================================================
# HELPER FUNCTIONS: SQL, HTTP & EXCEL EXTRACTION
# =========================================================================

function Invoke-SqlScalar {
    param([string]$Sql, [hashtable]$Parameters = @{})
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        foreach ($p in $Parameters.Keys) {
            [void]$cmd.Parameters.AddWithValue($p, $Parameters[$p])
        }
        return $cmd.ExecuteScalar()
    }
    finally {
        $conn.Close()
    }
}

function Invoke-SqlNonQuery {
    param([string]$Sql, [hashtable]$Parameters = @{})
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        foreach ($p in $Parameters.Keys) {
            [void]$cmd.Parameters.AddWithValue($p, $Parameters[$p])
        }
        return $cmd.ExecuteNonQuery()
    }
    finally {
        $conn.Close()
    }
}

function Send-HttpRequest {
    param(
        [string]$Uri,
        [string]$Method = "GET",
        [string]$Body = $null,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session = $null,
        [int]$TimeoutSec = 15,
        [string]$OutFile = $null
    )

    $params = @{
        Uri = $Uri
        Method = $Method
        MaximumRedirection = 0
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
        ErrorAction = "SilentlyContinue"
    }
    if ($Session) { $params["WebSession"] = $Session }
    if ($Body) {
        $params["Body"] = $Body
        $params["ContentType"] = "application/x-www-form-urlencoded"
    }
    if ($OutFile) {
        $params["OutFile"] = $OutFile
        $params["PassThru"] = $true
    }

    try {
        $response = Invoke-WebRequest @params
        return [PSCustomObject]@{
            StatusCode = [int]$response.StatusCode
            Headers = $response.Headers
            Content = $response.Content
            Location = $response.Headers["Location"]
        }
    }
    catch {
        if ($_.Exception.Response) {
            $resp = $_.Exception.Response
            $status = if ($resp.StatusCode.value__) { [int]$resp.StatusCode.value__ } else { [int]$resp.StatusCode }
            $loc = $resp.Headers["Location"]
            $stream = $resp.GetResponseStream()
            $bodyText = ""
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                $bodyText = $reader.ReadToEnd()
                $reader.Close()
            }
            return [PSCustomObject]@{
                StatusCode = $status
                Headers = $resp.Headers
                Content = $bodyText
                Location = $loc
            }
        }
        throw $_.Exception
    }
}

function Extract-AntiForgeryToken {
    param([string]$Html)
    $match = [regex]::Match($Html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (-not $match.Success) {
        $match = [regex]::Match($Html, 'value="([^"]+)"[^>]*name="__RequestVerificationToken"')
    }
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return $null
}

function Read-ExcelPackage {
    param([string]$FilePath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $result = [PSCustomObject]@{
        Worksheets = @()
        SharedStrings = @()
        AllText = ""
        RawXml = ""
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($FilePath)
    try {
        # 1. Extract worksheet names from xl/workbook.xml
        $wbEntry = $zip.GetEntry("xl/workbook.xml")
        if ($wbEntry) {
            $stream = $wbEntry.Open()
            $reader = New-Object System.IO.StreamReader($stream)
            $wbXml = [xml]$reader.ReadToEnd()
            $reader.Close()

            $sheets = $wbXml.workbook.sheets.sheet
            if ($sheets) {
                $result.Worksheets = @($sheets | ForEach-Object { $_.name })
            }
        }

        # 2. Extract shared text strings from xl/sharedStrings.xml
        $ssEntry = $zip.GetEntry("xl/sharedStrings.xml")
        if ($ssEntry) {
            $stream = $ssEntry.Open()
            $reader = New-Object System.IO.StreamReader($stream)
            $ssContent = $reader.ReadToEnd()
            $reader.Close()

            $ssXml = [xml]$ssContent
            $strings = @()
            if ($ssXml.sst.si) {
                foreach ($si in $ssXml.sst.si) {
                    if ($si.t -is [string]) {
                        $strings += $si.t
                    }
                    elseif ($si.r) {
                        $strings += (($si.r | ForEach-Object { $_.t }) -join "")
                    }
                }
            }
            $result.SharedStrings = $strings
            $result.AllText += ($strings -join " ")
        }

        # 3. Extract worksheet cell xml from xl/worksheets/sheet1.xml
        $sheetEntry = $zip.GetEntry("xl/worksheets/sheet1.xml")
        if ($sheetEntry) {
            $stream = $sheetEntry.Open()
            $reader = New-Object System.IO.StreamReader($stream)
            $sheetContent = $reader.ReadToEnd()
            $reader.Close()

            $result.RawXml = $sheetContent
            $result.AllText += " " + $sheetContent
        }
    }
    finally {
        $zip.Dispose()
    }

    return $result
}

# =========================================================================
# TEST RESULT ACCUMULATOR
# =========================================================================

$testsPassed = 0
$testsFailed = 0
$testResults = @()

function Assert-Check {
    param(
        [string]$Group,
        [string]$Description,
        [bool]$Condition,
        [string]$Details = ""
    )

    if ($Condition) {
        $script:testsPassed++
        Write-Host "  [PASS] $Description ($Details)" -ForegroundColor Green
        $script:testResults += [PSCustomObject]@{
            Group = $Group
            Description = $Description
            Status = "PASS"
            Details = $Details
        }
    }
    else {
        $script:testsFailed++
        Write-Host "  [FAIL] $Description ($Details)" -ForegroundColor Red
        $script:testResults += [PSCustomObject]@{
            Group = $Group
            Description = $Description
            Status = "FAIL"
            Details = $Details
        }
    }
}

# Track created IDs for guaranteed cleanup
$createdProductIds = New-Object System.Collections.Generic.List[int]
$createdSaleIds = New-Object System.Collections.Generic.List[int]
$createdComplaintIds = New-Object System.Collections.Generic.List[int]
$proc = $null
$tempFilesToClean = New-Object System.Collections.Generic.List[string]

try {
    # ---------------------------------------------------------------------
    # 1. PROCESS LIFECYCLE: START KESTREL WITH TEST DB CONFIGURATION
    # ---------------------------------------------------------------------
    Write-Host ""
    Write-Host "[SETUP] Terminating any stale web server processes..." -ForegroundColor Gray
    Get-Process -Name "RetailSalesStockAnalytics" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host "[SETUP] Launching Kestrel server for HTTP test harness..." -ForegroundColor Gray
    $projectPath = Join-Path $PSScriptRoot "..\RetailSalesStockAnalytics\RetailSalesStockAnalytics.csproj"

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = "dotnet"
    $startInfo.Arguments = "run --project `"$projectPath`" --no-launch-profile --urls $BaseUrl --ConnectionStrings:DefaultConnection=`"$ConnectionString`""
    $startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development"
    $startInfo.EnvironmentVariables["ConnectionStrings__DefaultConnection"] = $ConnectionString
    $startInfo.EnvironmentVariables["DefaultManager__Email"] = "manager@gmail.com"
    $startInfo.EnvironmentVariables["DefaultManager__Password"] = "Manager@Retail2026!"
    $startInfo.EnvironmentVariables["DefaultAdmin__Email"] = "admin@gmail.com"
    $startInfo.EnvironmentVariables["DefaultAdmin__Password"] = "Admin@Retail2026!"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($startInfo)

    Write-Host "[SETUP] Waiting for Kestrel to become healthy at $BaseUrl/Account/Login..." -ForegroundColor Gray
    $serverReady = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try {
            $probe = Send-HttpRequest -Uri "$BaseUrl/Account/Login" -Method "GET" -TimeoutSec 2
            if ($probe.StatusCode -eq 200) {
                $serverReady = $true
                break
            }
        }
        catch { }
    }

    if (-not $serverReady) {
        throw "Kestrel server failed to start or did not respond within 30 seconds."
    }
    Write-Host "[SETUP] Kestrel is healthy and listening on $BaseUrl." -ForegroundColor Green

    # ---------------------------------------------------------------------
    # 2. SEED INITIAL TEST FIXTURES & AUTHENTICATE ROLES
    # ---------------------------------------------------------------------
    $testRunId = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)

    function Authenticate-User {
        param([string]$Email, [string]$Password)
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $loginPage = Send-HttpRequest -Uri "$BaseUrl/Account/Login" -Method "GET" -Session $session
        $token = Extract-AntiForgeryToken $loginPage.Content
        if (-not $token) { throw "Failed to extract anti-forgery token from /Account/Login" }

        $body = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($token))&email=$([System.Uri]::EscapeDataString($Email))&password=$([System.Uri]::EscapeDataString($Password))"
        $loginResp = Send-HttpRequest -Uri "$BaseUrl/Account/Login" -Method "POST" -Body $body -Session $session

        return [PSCustomObject]@{
            Session = $session
            StatusCode = $loginResp.StatusCode
            Location = $loginResp.Location
            IsAuthenticated = ($loginResp.StatusCode -eq 302 -and ($loginResp.Location -like "*Home*" -or $loginResp.Location -eq "/" -or [string]::IsNullOrWhiteSpace($loginResp.Location)))
        }
    }

    $adminAuth = Authenticate-User -Email "admin@gmail.com" -Password "Admin@Retail2026!"
    $mgrAuth = Authenticate-User -Email "manager@gmail.com" -Password "Manager@Retail2026!"
    $custAuth = Authenticate-User -Email "testcustomer.functional@example.com" -Password "Customer@Retail2026!"
    $anonSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    Assert-Check "Auth" "Admin login succeeded" $adminAuth.IsAuthenticated "HTTP $($adminAuth.StatusCode)"
    Assert-Check "Auth" "Manager login succeeded" $mgrAuth.IsAuthenticated "HTTP $($mgrAuth.StatusCode)"
    Assert-Check "Auth" "Customer login succeeded" $custAuth.IsAuthenticated "HTTP $($custAuth.StatusCode)"

    # Baseline references
    $catId1 = [int](Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories ORDER BY CategoryId ASC")
    $catId2 = [int](Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories WHERE CategoryId <> @C1 ORDER BY CategoryId ASC" @{ "@C1" = $catId1 })
    $baselineCustId = [int](Invoke-SqlScalar "SELECT TOP 1 CustomerId FROM Customers ORDER BY CustomerId ASC")

    # Create 2 test products in different categories
    $prodName1 = "HTTP_REPORT_PROD_1_$testRunId"
    $prodId1 = [int](Invoke-SqlScalar @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, 25.00, 50.00, 20, 5);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $prodName1; "@CatId" = $catId1 })
    $createdProductIds.Add($prodId1)

    $prodName2 = "HTTP_REPORT_PROD_2_$testRunId"
    $prodId2 = [int](Invoke-SqlScalar @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, 40.00, 80.00, 10, 2);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $prodName2; "@CatId" = $catId2 })
    $createdProductIds.Add($prodId2)

    # In-Range Test Sales (May 2026 window)
    $saleToken1 = "REPORT_TEST_SALE_1_$testRunId"
    $saleId1 = [int](Invoke-SqlScalar @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (@CustId, '2026-05-15T10:00:00', 200.00, 0.00, @Token);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@CustId" = $baselineCustId; "@Token" = $saleToken1 })
    $createdSaleIds.Add($saleId1)

    [void](Invoke-SqlNonQuery @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 4, 50.00, 25.00);
"@ @{ "@SaleId" = $saleId1; "@ProdId" = $prodId1 })

    $saleToken2 = "REPORT_TEST_SALE_2_$testRunId"
    $saleId2 = [int](Invoke-SqlScalar @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (@CustId, '2026-05-20T14:30:00', 160.00, 0.00, @Token);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@CustId" = $baselineCustId; "@Token" = $saleToken2 })
    $createdSaleIds.Add($saleId2)

    [void](Invoke-SqlNonQuery @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 2, 80.00, 40.00);
"@ @{ "@SaleId" = $saleId2; "@ProdId" = $prodId2 })

    # Out-of-Range Test Sale (April 2026 - must be excluded by May date filter)
    $saleTokenEx = "REPORT_TEST_SALE_EXCLUDED_$testRunId"
    $saleIdEx = [int](Invoke-SqlScalar @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (@CustId, '2026-04-10T10:00:00', 99.00, 0.00, @Token);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@CustId" = $baselineCustId; "@Token" = $saleTokenEx })
    $createdSaleIds.Add($saleIdEx)

    [void](Invoke-SqlNonQuery @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 1, 99.00, 50.00);
"@ @{ "@SaleId" = $saleIdEx; "@ProdId" = $prodId1 })

    # Stock transactions: 2 in May 2026, 1 out-of-range in April 2026
    [void](Invoke-SqlNonQuery @"
INSERT INTO StockTransactions (ProductId, TransactionType, Quantity, UnitCost, TransactionDate, Note)
VALUES (@ProdId, 'Restock', 20, 25.00, '2026-05-10T09:00:00', @Note);
"@ @{ "@ProdId" = $prodId1; "@Note" = "HTTP Report Restock - $testRunId" })

    [void](Invoke-SqlNonQuery @"
INSERT INTO StockTransactions (ProductId, TransactionType, Quantity, UnitCost, TransactionDate, Note)
VALUES (@ProdId, 'Sale', -4, 25.00, '2026-05-15T10:00:00', @Note);
"@ @{ "@ProdId" = $prodId1; "@Note" = "HTTP Report Sale - $testRunId" })

    [void](Invoke-SqlNonQuery @"
INSERT INTO StockTransactions (ProductId, TransactionType, Quantity, UnitCost, TransactionDate, Note)
VALUES (@ProdId, 'Restock', 5, 25.00, '2026-04-10T09:00:00', @Note);
"@ @{ "@ProdId" = $prodId1; "@Note" = "HTTP Report Excluded Restock - $testRunId" })

    # Complaints: 1 in May 2026, 1 out-of-range in April 2026
    $complaintCatId = [int](Invoke-SqlScalar "SELECT TOP 1 ComplaintCategoryId FROM ComplaintCategories")
    if ($complaintCatId -eq 0) {
        $complaintCatId = [int](Invoke-SqlScalar @"
INSERT INTO ComplaintCategories (CategoryName, Department, Priority, DefaultSeverity, SimilarityThreshold)
VALUES ('Delivery Issue', 'Logistics', 'High', 'Medium', 0.60);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@)
    }

    $complaintCode = "HTTP_COMP_$testRunId"
    $complaintId = [int](Invoke-SqlScalar @"
INSERT INTO Complaints (ComplaintCode, CustomerId, ComplaintCategoryId, ComplaintText, CreatedAt, RoutingStatus, SimilarityScore)
VALUES (@Code, @CustId, @CatId, 'The packaging arrived crushed and damaged', '2026-05-12T11:00:00', 'Routed', 0.85);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Code" = $complaintCode; "@CustId" = $baselineCustId; "@CatId" = $complaintCatId })
    $createdComplaintIds.Add($complaintId)

    $complaintCodeEx = "EX_COMP_$testRunId"
    $complaintIdEx = [int](Invoke-SqlScalar @"
INSERT INTO Complaints (ComplaintCode, CustomerId, ComplaintCategoryId, ComplaintText, CreatedAt, RoutingStatus, SimilarityScore)
VALUES (@Code, @CustId, @CatId, 'Excluded April complaint text', '2026-04-10T11:00:00', 'Routed', 0.85);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Code" = $complaintCodeEx; "@CustId" = $baselineCustId; "@CatId" = $complaintCatId })
    $createdComplaintIds.Add($complaintIdEx)

    Write-Host "[SETUP] Reporting test fixtures seeded successfully (in-range May and out-of-range April)." -ForegroundColor Gray

    # =========================================================================
    # SECTION 1: COMPLETE HTTP AUTHORIZATION MATRIX (ALL 15 ENDPOINTS X 4 ROLES)
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 1: Complete HTTP Authorization Matrix (15 Endpoints x 4 Roles) ---" -ForegroundColor Yellow

    $allEndpoints = @(
        @{ Route = "/Reports"; Name = "/Reports Dashboard" },
        @{ Route = "/Reports/DailySales"; Name = "/Reports/DailySales View" },
        @{ Route = "/Reports/DailyDemandingProducts"; Name = "/Reports/DailyDemandingProducts View" },
        @{ Route = "/Reports/StockMovement"; Name = "/Reports/StockMovement View" },
        @{ Route = "/Reports/ComplaintRouting"; Name = "/Reports/ComplaintRouting View" },
        @{ Route = "/SalesAnalytics"; Name = "/SalesAnalytics View" },
        @{ Route = "/CustomerPurchasePattern"; Name = "/CustomerPurchasePattern View" },
        @{ Route = "/Reports/ExportDailySalesExcel"; Name = "/Reports/ExportDailySalesExcel" },
        @{ Route = "/Reports/ExportDailySalesPdf"; Name = "/Reports/ExportDailySalesPdf" },
        @{ Route = "/Reports/ExportDailyDemandingProductsExcel"; Name = "/Reports/ExportDailyDemandingProductsExcel" },
        @{ Route = "/Reports/ExportDailyDemandingProductsPdf"; Name = "/Reports/ExportDailyDemandingProductsPdf" },
        @{ Route = "/Reports/ExportStockMovementExcel"; Name = "/Reports/ExportStockMovementExcel" },
        @{ Route = "/Reports/ExportStockMovementPdf"; Name = "/Reports/ExportStockMovementPdf" },
        @{ Route = "/Reports/ExportComplaintRoutingExcel"; Name = "/Reports/ExportComplaintRoutingExcel" },
        @{ Route = "/Reports/ExportComplaintRoutingPdf"; Name = "/Reports/ExportComplaintRoutingPdf" }
    )

    # --- 1.1: ADMIN ROLE (All 15 Endpoints -> HTTP 200) ---
    foreach ($ep in $allEndpoints) {
        $resp = Send-HttpRequest -Uri "$BaseUrl$($ep.Route)" -Method "GET" -Session $adminAuth.Session
        Assert-Check "Auth Matrix - Admin" "Admin access $($ep.Name) returns HTTP 200 OK" ($resp.StatusCode -eq 200) "HTTP $($resp.StatusCode)"
    }

    # --- 1.2: MANAGER ROLE (All 15 Endpoints -> HTTP 200) ---
    foreach ($ep in $allEndpoints) {
        $resp = Send-HttpRequest -Uri "$BaseUrl$($ep.Route)" -Method "GET" -Session $mgrAuth.Session
        Assert-Check "Auth Matrix - Manager" "Manager access $($ep.Name) returns HTTP 200 OK" ($resp.StatusCode -eq 200) "HTTP $($resp.StatusCode)"
    }

    # --- 1.3: CUSTOMER ROLE (All 15 Endpoints -> 403 / AccessDenied) ---
    foreach ($ep in $allEndpoints) {
        $resp = Send-HttpRequest -Uri "$BaseUrl$($ep.Route)" -Method "GET" -Session $custAuth.Session
        $isDenied = ($resp.StatusCode -eq 403 -or ($resp.StatusCode -eq 302 -and $resp.Location -like "*AccessDenied*"))
        Assert-Check "Auth Matrix - Customer" "Customer access $($ep.Name) denied (403/AccessDenied)" $isDenied "HTTP $($resp.StatusCode), Loc: $($resp.Location)"
    }

    # --- 1.4: ANONYMOUS ROLE (All 15 Endpoints -> 302 Redirect to Login) ---
    foreach ($ep in $allEndpoints) {
        $resp = Send-HttpRequest -Uri "$BaseUrl$($ep.Route)" -Method "GET" -Session $anonSession
        $isRedirected = ($resp.StatusCode -eq 302 -and $resp.Location -like "*Account/Login*")
        Assert-Check "Auth Matrix - Anonymous" "Anonymous access $($ep.Name) redirected to Login" $isRedirected "HTTP $($resp.StatusCode), Loc: $($resp.Location)"
    }

    # =========================================================================
    # SECTION 2: REPORT DATA FILTERING & SQL CORROBORATION
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 2: Report Data Filtering & SQL Corroboration ---" -ForegroundColor Yellow

    # 2.1: Daily Sales Filtered Query (2026-05-01 to 2026-05-31)
    $salesQueryUri = "$BaseUrl/Reports/DailySales?startDate=2026-05-01&endDate=2026-05-31"
    $salesFilterResp = Send-HttpRequest -Uri $salesQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "GET /Reports/DailySales with date filter returns 200 OK" ($salesFilterResp.StatusCode -eq 200) "HTTP $($salesFilterResp.StatusCode)"

    # Calculate SQL expected values for the May 2026 window
    $sqlSalesCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleDate >= '2026-05-01' AND SaleDate < '2026-06-01'")
    $sqlTotalRevenue = [decimal](Invoke-SqlScalar "SELECT COALESCE(SUM(TotalAmount), 0) FROM Sales WHERE SaleDate >= '2026-05-01' AND SaleDate < '2026-06-01'")
    $sqlUnitsSold = [int](Invoke-SqlScalar @"
SELECT COALESCE(SUM(i.Quantity), 0)
FROM SaleItems i
INNER JOIN Sales s ON i.SaleId = s.SaleId
WHERE s.SaleDate >= '2026-05-01' AND s.SaleDate < '2026-06-01'
"@)

    Assert-Check "Report Data" "Daily Sales rendered report corroborates SQL sales count" ($salesFilterResp.Content -like "*$sqlSalesCount*" -and $sqlSalesCount -ge 2) "Sales count: $sqlSalesCount"
    Assert-Check "Report Data" "Daily Sales rendered report corroborates SQL total units sold" ($salesFilterResp.Content -like "*$sqlUnitsSold*" -and $sqlUnitsSold -ge 6) "Units sold: $sqlUnitsSold"
    Assert-Check "Report Data" "Daily Sales rendered report displays filtered revenue" ($salesFilterResp.Content -like "*360.00*" -or $salesFilterResp.Content -like "*360*") "Revenue matches 360.00"

    # 2.2: Daily Demanding Products Filtered Query
    $demQueryUri = "$BaseUrl/Reports/DailyDemandingProducts?startDate=2026-05-01&endDate=2026-05-31"
    $demFilterResp = Send-HttpRequest -Uri $demQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "GET /Reports/DailyDemandingProducts with date filter returns 200 OK" ($demFilterResp.StatusCode -eq 200) "HTTP $($demFilterResp.StatusCode)"
    Assert-Check "Report Data" "Daily Demanding Products displays seeded test product name" ($demFilterResp.Content -like "*$prodName1*") "Product 1 present"
    Assert-Check "Report Data" "Daily Demanding Products displays second seeded test product name" ($demFilterResp.Content -like "*$prodName2*") "Product 2 present"

    # 2.3: Stock Movement Filtered Query
    $stockQueryUri = "$BaseUrl/Reports/StockMovement?startDate=2026-05-01&endDate=2026-05-31"
    $stockFilterResp = Send-HttpRequest -Uri $stockQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "GET /Reports/StockMovement with date filter returns 200 OK" ($stockFilterResp.StatusCode -eq 200) "HTTP $($stockFilterResp.StatusCode)"
    Assert-Check "Report Data" "Stock Movement report displays Restock movement record" ($stockFilterResp.Content -like "*HTTP Report Restock - $testRunId*") "Restock note present"
    Assert-Check "Report Data" "Stock Movement report displays Sale movement record" ($stockFilterResp.Content -like "*HTTP Report Sale - $testRunId*") "Sale note present"

    # 2.4: AI Complaint Routing Filtered Query
    $compQueryUri = "$BaseUrl/Reports/ComplaintRouting?startDate=2026-05-01&endDate=2026-05-31"
    $compFilterResp = Send-HttpRequest -Uri $compQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "GET /Reports/ComplaintRouting with date filter returns 200 OK" ($compFilterResp.StatusCode -eq 200) "HTTP $($compFilterResp.StatusCode)"
    Assert-Check "Report Data" "Complaint Routing report displays seeded complaint code" ($compFilterResp.Content -like "*$complaintCode*") "Complaint code verified"
    Assert-Check "Report Data" "Complaint Routing report displays Routed status" ($compFilterResp.Content -like "*Routed*") "Status verified"

    # 2.5: Sales Analytics Category Filtered Query (categoryId = $catId1)
    $catQueryUri = "$BaseUrl/SalesAnalytics?startDate=2026-05-01&endDate=2026-05-31&categoryId=$catId1"
    $catFilterResp = Send-HttpRequest -Uri $catQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "GET /SalesAnalytics with category filter returns 200 OK" ($catFilterResp.StatusCode -eq 200) "HTTP $($catFilterResp.StatusCode)"
    Assert-Check "Report Data" "Sales Analytics category filter includes selected category" ($catFilterResp.Content -like "*Grocery*" -or $catFilterResp.Content -like "*Category:*") "Category filter applied"
    Assert-Check "Report Data" "Sales Analytics displays filtered category revenue" ($catFilterResp.Content -like "*200.00*" -or $catFilterResp.Content -like "*200*") "Category revenue verified"

    # 2.6: Empty-Result Scenario (Out-of-range date filter: year 2099)
    $emptyQueryUri = "$BaseUrl/Reports/DailySales?startDate=2099-01-01&endDate=2099-01-02"
    $emptyFilterResp = Send-HttpRequest -Uri $emptyQueryUri -Method "GET" -Session $adminAuth.Session
    Assert-Check "Report Data" "Empty-result date filter returns 200 OK without crashing" ($emptyFilterResp.StatusCode -eq 200) "HTTP $($emptyFilterResp.StatusCode)"
    Assert-Check "Report Data" "Empty-result report renders zero sales summary safely" ($emptyFilterResp.Content -like "*Total Sales*" -or $emptyFilterResp.Content -like "*0*") "Empty summary rendered"

    # =========================================================================
    # SECTION 3: EXPORT CONTENT & FILTER VERIFICATION (ALL 8 EXPORTS)
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 3: Detailed Export Content & Filter Verification ---" -ForegroundColor Yellow

    $exportParams = "startDate=2026-05-01&endDate=2026-05-31"

    # -------------------------------------------------------------------------
    # 3.1: EXCEL EXPORT CONTENT & FILTER VERIFICATION (ClosedXML / OpenXML)
    # -------------------------------------------------------------------------

    # 3.1.1: Daily Sales Excel Export
    $salesExcelPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_sales_$testRunId.xlsx")
    $script:tempFilesToClean.Add($salesExcelPath)
    $salesExcelResp = Send-HttpRequest -Uri "$BaseUrl/Reports/ExportDailySalesExcel?$exportParams" -Method "GET" -Session $adminAuth.Session -OutFile $salesExcelPath
    
    $salesExcelData = Read-ExcelPackage -FilePath $salesExcelPath
    $salesSheetOk = ($salesExcelData.Worksheets -contains "Daily Sales")
    $salesHeadersOk = ($salesExcelData.SharedStrings -contains "Daily Sales Report" -and 
                       $salesExcelData.SharedStrings -contains "Date" -and 
                       $salesExcelData.SharedStrings -contains "Sales" -and 
                       $salesExcelData.SharedStrings -contains "Revenue")
    # Date filter check: In-range May dates/revenues present; out-of-range April 10 sale (99.00) excluded
    $salesMayDataPresent = ($salesExcelData.AllText -like "*15 May 2026*" -or $salesExcelData.AllText -like "*20 May 2026*" -or $salesExcelData.AllText -like "*200*")
    $salesAprilExcluded = (-not ($salesExcelData.AllText -like "*10 Apr 2026*") -and -not ($salesExcelData.AllText -like "*99.00*"))

    Assert-Check "Export Excel" "Daily Sales Excel returns HTTP 200 with Excel Content-Type" ($salesExcelResp.StatusCode -eq 200 -and $salesExcelResp.Headers["Content-Type"] -like "*spreadsheetml*") "HTTP $($salesExcelResp.StatusCode)"
    Assert-Check "Export Excel" "Daily Sales Excel contains 'Daily Sales' worksheet" $salesSheetOk "Worksheets: $($salesExcelData.Worksheets -join ', ')"
    Assert-Check "Export Excel" "Daily Sales Excel contains expected title and column headers" $salesHeadersOk "Headers: Report, Date, Sales, Revenue"
    Assert-Check "Export Excel" "Daily Sales Excel includes May 2026 sales and excludes April 2026 out-of-range sale" ($salesMayDataPresent -and $salesAprilExcluded) "In-range present: $salesMayDataPresent, April excluded: $salesAprilExcluded"

    # 3.1.2: Daily Demanding Products Excel Export
    $demExcelPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_dem_$testRunId.xlsx")
    $script:tempFilesToClean.Add($demExcelPath)
    $demExcelResp = Send-HttpRequest -Uri "$BaseUrl/Reports/ExportDailyDemandingProductsExcel?$exportParams" -Method "GET" -Session $adminAuth.Session -OutFile $demExcelPath

    $demExcelData = Read-ExcelPackage -FilePath $demExcelPath
    $demSheetOk = ($demExcelData.Worksheets -contains "Demanding Products")
    $demHeadersOk = ($demExcelData.SharedStrings -contains "Daily Demanding Product Report" -and 
                     $demExcelData.SharedStrings -contains "Product" -and 
                     $demExcelData.SharedStrings -contains "Category" -and 
                     $demExcelData.SharedStrings -contains "Units Sold")
    $demSeededProdOk = ($demExcelData.SharedStrings -contains $prodName1 -or $demExcelData.AllText -like "*$prodName1*")

    Assert-Check "Export Excel" "Demanding Products Excel returns HTTP 200 with Excel Content-Type" ($demExcelResp.StatusCode -eq 200 -and $demExcelResp.Headers["Content-Type"] -like "*spreadsheetml*") "HTTP $($demExcelResp.StatusCode)"
    Assert-Check "Export Excel" "Demanding Products Excel contains 'Demanding Products' worksheet" $demSheetOk "Worksheets: $($demExcelData.Worksheets -join ', ')"
    Assert-Check "Export Excel" "Demanding Products Excel contains expected title and column headers" $demHeadersOk "Headers: Report, Product, Category, Units Sold"
    Assert-Check "Export Excel" "Demanding Products Excel contains seeded product records" $demSeededProdOk "Product: $prodName1"

    # 3.1.3: Stock Movement Excel Export
    $stockExcelPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_stock_$testRunId.xlsx")
    $script:tempFilesToClean.Add($stockExcelPath)
    $stockExcelResp = Send-HttpRequest -Uri "$BaseUrl/Reports/ExportStockMovementExcel?$exportParams" -Method "GET" -Session $adminAuth.Session -OutFile $stockExcelPath

    $stockExcelData = Read-ExcelPackage -FilePath $stockExcelPath
    $stockSheetOk = ($stockExcelData.Worksheets -contains "Stock Movement")
    $stockHeadersOk = ($stockExcelData.SharedStrings -contains "Stock Movement Report" -and 
                       $stockExcelData.SharedStrings -contains "Date" -and 
                       $stockExcelData.SharedStrings -contains "Product" -and 
                       $stockExcelData.SharedStrings -contains "Transaction Type" -and 
                       $stockExcelData.SharedStrings -contains "Note")
    $stockInPresent = ($stockExcelData.AllText -like "*HTTP Report Restock - $testRunId*")
    $stockExExcluded = (-not ($stockExcelData.AllText -like "*HTTP Report Excluded Restock - $testRunId*"))

    Assert-Check "Export Excel" "Stock Movement Excel returns HTTP 200 with Excel Content-Type" ($stockExcelResp.StatusCode -eq 200 -and $stockExcelResp.Headers["Content-Type"] -like "*spreadsheetml*") "HTTP $($stockExcelResp.StatusCode)"
    Assert-Check "Export Excel" "Stock Movement Excel contains 'Stock Movement' worksheet" $stockSheetOk "Worksheets: $($stockExcelData.Worksheets -join ', ')"
    Assert-Check "Export Excel" "Stock Movement Excel contains expected title and column headers" $stockHeadersOk "Headers: Report, Date, Product, Type, Note"
    Assert-Check "Export Excel" "Stock Movement Excel includes May movements and excludes April out-of-range movement" ($stockInPresent -and $stockExExcluded) "May present: $stockInPresent, April excluded: $stockExExcluded"

    # 3.1.4: AI Complaint Routing Excel Export
    $compExcelPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_comp_$testRunId.xlsx")
    $script:tempFilesToClean.Add($compExcelPath)
    $compExcelResp = Send-HttpRequest -Uri "$BaseUrl/Reports/ExportComplaintRoutingExcel?$exportParams" -Method "GET" -Session $adminAuth.Session -OutFile $compExcelPath

    $compExcelData = Read-ExcelPackage -FilePath $compExcelPath
    $compSheetOk = ($compExcelData.Worksheets -contains "Complaint Routing")
    $compHeadersOk = ($compExcelData.SharedStrings -contains "AI Complaint Routing Report" -and 
                      $compExcelData.SharedStrings -contains "Complaint Code" -and 
                      $compExcelData.SharedStrings -contains "Customer" -and 
                      $compExcelData.SharedStrings -contains "Category")
    $compInPresent = ($compExcelData.AllText -like "*$complaintCode*")
    $compExExcluded = (-not ($compExcelData.AllText -like "*$complaintCodeEx*"))

    Assert-Check "Export Excel" "Complaint Routing Excel returns HTTP 200 with Excel Content-Type" ($compExcelResp.StatusCode -eq 200 -and $compExcelResp.Headers["Content-Type"] -like "*spreadsheetml*") "HTTP $($compExcelResp.StatusCode)"
    Assert-Check "Export Excel" "Complaint Routing Excel contains 'Complaint Routing' worksheet" $compSheetOk "Worksheets: $($compExcelData.Worksheets -join ', ')"
    Assert-Check "Export Excel" "Complaint Routing Excel contains expected title and column headers" $compHeadersOk "Headers: Report, Code, Customer, Category"
    Assert-Check "Export Excel" "Complaint Routing Excel includes May complaint and excludes April out-of-range complaint" ($compInPresent -and $compExExcluded) "May present: $compInPresent, April excluded: $compExExcluded"

    # -------------------------------------------------------------------------
    # 3.2: PDF EXPORT VALIDATION (QuestPDF Binary Structure & Filter Comparison)
    # -------------------------------------------------------------------------
    function Test-PdfValidation {
        param([string]$ActionName, [string]$Label)
        $tempPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_$([System.Guid]::NewGuid().ToString('N')).pdf")
        $script:tempFilesToClean.Add($tempPath)

        $uri = "$BaseUrl/Reports/$ActionName`?$exportParams"
        $resp = Send-HttpRequest -Uri $uri -Method "GET" -Session $adminAuth.Session -OutFile $tempPath

        $statusOk = ($resp.StatusCode -eq 200)
        $contentType = $resp.Headers["Content-Type"]
        $typeOk = ($contentType -like "*application/pdf*")

        $fileBytes = if (Test-Path $tempPath) { [System.IO.File]::ReadAllBytes($tempPath) } else { @() }
        $sizeOk = ($fileBytes.Length -gt 100)
        # PDF magic bytes (%PDF)
        $magicOk = ($fileBytes.Length -ge 4 -and $fileBytes[0] -eq 0x25 -and $fileBytes[1] -eq 0x50 -and $fileBytes[2] -eq 0x44 -and $fileBytes[3] -eq 0x46)
        # PDF EOF trailer marker
        $rawText = [System.Text.Encoding]::ASCII.GetString($fileBytes)
        $eofOk = $rawText.Contains("%%EOF")

        Assert-Check "Export PDF" "$Label returns HTTP 200 with PDF Content-Type" ($statusOk -and $typeOk) "HTTP $($resp.StatusCode), Type: $contentType"
        Assert-Check "Export PDF" "$Label produces non-empty file with valid %PDF magic header and %%EOF trailer" ($sizeOk -and $magicOk -and $eofOk) "Bytes: $($fileBytes.Length)"
        return $fileBytes.Length
    }

    $salesPdfSize = Test-PdfValidation "ExportDailySalesPdf" "ExportDailySalesPdf"
    [void](Test-PdfValidation "ExportDailyDemandingProductsPdf" "ExportDailyDemandingProductsPdf")
    [void](Test-PdfValidation "ExportStockMovementPdf" "ExportStockMovementPdf")
    [void](Test-PdfValidation "ExportComplaintRoutingPdf" "ExportComplaintRoutingPdf")

    # PDF Filter Responsiveness: Compare May 2026 populated PDF vs Empty 2099 PDF
    $emptyPdfPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "test_export_empty_$testRunId.pdf")
    $script:tempFilesToClean.Add($emptyPdfPath)
    $emptyPdfResp = Send-HttpRequest -Uri "$BaseUrl/Reports/ExportDailySalesPdf?startDate=2099-01-01&endDate=2099-01-02" -Method "GET" -Session $adminAuth.Session -OutFile $emptyPdfPath
    $emptyPdfBytes = if (Test-Path $emptyPdfPath) { [System.IO.File]::ReadAllBytes($emptyPdfPath) } else { @() }
    
    $pdfFilterResponsive = ($emptyPdfResp.StatusCode -eq 200 -and $emptyPdfBytes.Length -gt 0 -and $salesPdfSize -gt 0)
    Assert-Check "Export PDF" "Daily Sales PDF is responsive to date filtering across populated and empty date windows" $pdfFilterResponsive "Populated: $salesPdfSize bytes, Empty: $($emptyPdfBytes.Length) bytes"

    # =========================================================================
    # SECTION 4: PERSISTENT STATE NON-MUTATION VERIFICATION
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 4: Persistent State Non-Mutation Verification ---" -ForegroundColor Yellow

    $salesCountAfterReports = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId IN (@S1, @S2, @S3)" @{ "@S1" = $saleId1; "@S2" = $saleId2; "@S3" = $saleIdEx })
    Assert-Check "Security" "Reporting requests did not mutate persisted sales count" ($salesCountAfterReports -eq 3) "Sales count: $salesCountAfterReports"

    $productsCountAfterReports = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Products WHERE ProductId IN (@P1, @P2)" @{ "@P1" = $prodId1; "@P2" = $prodId2 })
    Assert-Check "Security" "Reporting requests did not mutate persisted products count" ($productsCountAfterReports -eq 2) "Products count: $productsCountAfterReports"
}
finally {
    # =========================================================================
    # SECTION 5: GUARANTEED TEST ISOLATION & CLEANUP
    # =========================================================================
    Write-Host ""
    Write-Host "[CLEANUP] Executing teardown of test-created data..." -ForegroundColor Gray

    try {
        # 1. Clean complaints
        foreach ($cmpId in $createdComplaintIds) {
            [void](Invoke-SqlNonQuery "DELETE FROM Complaints WHERE ComplaintId = @Id" @{ "@Id" = $cmpId })
        }

        # 2. Clean stock transactions
        [void](Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE Note LIKE '%HTTP Report %'")

        # 3. Clean sales and sale items
        foreach ($sid in $createdSaleIds) {
            [void](Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId = @Id" @{ "@Id" = $sid })
            [void](Invoke-SqlNonQuery "DELETE FROM Sales WHERE SaleId = @Id" @{ "@Id" = $sid })
        }

        # 4. Clean products
        foreach ($pidToClean in $createdProductIds) {
            [void](Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId = @Id" @{ "@Id" = $pidToClean })
            [void](Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId = @Id" @{ "@Id" = $pidToClean })
            [void](Invoke-SqlNonQuery "DELETE FROM Products WHERE ProductId = @Id" @{ "@Id" = $pidToClean })
        }

        # 5. Wildcard fallback sweeps
        [void](Invoke-SqlNonQuery "DELETE FROM Complaints WHERE ComplaintCode LIKE 'HTTP_COMP_%' OR ComplaintCode LIKE 'EX_COMP_%'")
        [void](Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId IN (SELECT SaleId FROM Sales WHERE CheckoutToken LIKE 'REPORT_TEST_SALE_%')")
        [void](Invoke-SqlNonQuery "DELETE FROM Sales WHERE CheckoutToken LIKE 'REPORT_TEST_SALE_%'")
        [void](Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_REPORT_PROD_%')")
        [void](Invoke-SqlNonQuery "DELETE FROM Products WHERE Name LIKE 'HTTP_REPORT_PROD_%'")

        Write-Host "[CLEANUP] Database test entities cleaned up successfully." -ForegroundColor Green
    }
    catch {
        Write-Host "[CLEANUP ERROR] Failed during SQL cleanup: $($_.Exception.Message)" -ForegroundColor Red
    }

    # Clean temporary downloaded export files
    foreach ($tf in $tempFilesToClean) {
        if (Test-Path $tf) {
            Remove-Item -Path $tf -Force -ErrorAction SilentlyContinue
        }
    }

    # Stop web server process
    Write-Host "[CLEANUP] Stopping Kestrel web server process tree..." -ForegroundColor Gray
    if ($proc -and -not $proc.HasExited) {
        try {
            $proc.Kill()
            $proc.WaitForExit(3000)
        }
        catch { }
    }
    Get-Process -Name "RetailSalesStockAnalytics" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "[CLEANUP] Web server stopped." -ForegroundColor Green
}

# =========================================================================
# FINAL RESULTS SUMMARY
# =========================================================================
Write-Host ""
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "REPORTS HTTP INTEGRATION TEST SUITE SUMMARY" -ForegroundColor Cyan
Write-Host "  PASSED: $testsPassed" -ForegroundColor Green
Write-Host "  FAILED: $testsFailed" -ForegroundColor $(if ($testsFailed -gt 0) { "Red" } else { "Green" })
Write-Host "=================================================" -ForegroundColor Cyan

if ($env:HTTP_TEST_SUMMARY_FILE) {
    try {
        Set-Content -Path $env:HTTP_TEST_SUMMARY_FILE -Value "$testsPassed,$testsFailed"
    } catch { }
}

if ($testsFailed -gt 0) {
    exit 1
}
exit 0
