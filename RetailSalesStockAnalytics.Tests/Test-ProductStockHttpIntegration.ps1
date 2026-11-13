<#
.SYNOPSIS
    Repeatable HTTP Integration Test Suite for Product and Stock Workflows.
.DESCRIPTION
    Validates ASP.NET Core MVC Product and Stock endpoints:
      - /Products/Create (POST)
      - /Products/Edit/{id} (POST)
      - /Products/Delete/{id} (POST)
      - /Stock/Create (POST) - Restock
      - /Stock/Adjust (POST) - Stock Adjustment
    via live HTTP requests against a local Kestrel instance connected to
    the approved test database (RetailSalesStockAnalytics_TestDb).
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
    Write-Host "[INFO] Product and Stock HTTP integration tests skipped (opt-in switch -Functional was not supplied)." -ForegroundColor Yellow
    Write-Host "================================================="
    Write-Host "PRODUCT & STOCK HTTP RESULTS: SKIPPED (Use -Functional to execute)"
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
Write-Host "STARTING PRODUCT & STOCK HTTP INTEGRATION TEST SUITE" -ForegroundColor Cyan
Write-Host "Approved Test Target: '$targetDb' on '$($connBuilder.DataSource)'" -ForegroundColor Cyan
Write-Host "Endpoint Base URL: $BaseUrl" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

# =========================================================================
# HELPER FUNCTIONS: SQL & HTTP
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
        [int]$TimeoutSec = 15
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
$proc = $null

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
    # 2. SEED INITIAL TEST FIXTURES IN RetailSalesStockAnalytics_TestDb
    # ---------------------------------------------------------------------
    $testRunId = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)

    # Ensure Category 1 exists
    $catId = Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories WHERE CategoryId = 1"
    if (-not $catId) {
        $catId = Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories"
    }

    # Helper: Authenticate session
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

    Assert-Check "Auth" "Admin login succeeded" $adminAuth.IsAuthenticated "HTTP $($adminAuth.StatusCode)"
    Assert-Check "Auth" "Manager login succeeded" $mgrAuth.IsAuthenticated "HTTP $($mgrAuth.StatusCode)"
    Assert-Check "Auth" "Customer login succeeded" $custAuth.IsAuthenticated "HTTP $($custAuth.StatusCode)"

    # =========================================================================
    # SECTION 1: PRODUCT HTTP WORKFLOWS
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 1: Product Creation, Editing, Auditing & Deletion ---" -ForegroundColor Yellow

    # --- 1.1: GET /Products/Create ---
    $prodCreateGet = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Product Create" "GET /Products/Create returns 200 OK for Admin" ($prodCreateGet.StatusCode -eq 200) "HTTP $($prodCreateGet.StatusCode)"

    $afTokenProdCreate = Extract-AntiForgeryToken $prodCreateGet.Content
    Assert-Check "Product Create" "Extracted anti-forgery token from /Products/Create form" (-not [string]::IsNullOrWhiteSpace($afTokenProdCreate)) "Token length: $($afTokenProdCreate.Length)"

    # --- 1.2: POST /Products/Create (Valid Product with Initial Stock) ---
    $prodNameA = "HTTP_PROD_TEST_A_$testRunId"
    $purchasePriceA = 80.00
    $sellingPriceA = 120.00
    $initialStockA = 25
    $reorderLevelA = 5

    $prodPostBodyA = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdCreate))" +
        "&Name=$([System.Uri]::EscapeDataString($prodNameA))" +
        "&CategoryId=$catId" +
        "&PurchasePrice=$purchasePriceA" +
        "&SellingPrice=$sellingPriceA" +
        "&StockQuantity=$initialStockA" +
        "&ReorderLevel=$reorderLevelA"

    $prodCreateResp = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $prodPostBodyA -Session $adminAuth.Session
    Assert-Check "Product Create" "POST /Products/Create redirects (HTTP 302) on success" ($prodCreateResp.StatusCode -eq 302) "HTTP $($prodCreateResp.StatusCode), Location: $($prodCreateResp.Location)"

    # SQL Verification: Product row
    $prodIdA = [int](Invoke-SqlScalar "SELECT ProductId FROM Products WHERE Name = @Name" @{ "@Name" = $prodNameA })
    Assert-Check "Product Create" "Product persisted in SQL Server" ($prodIdA -gt 0) "ProductId: $prodIdA"
    if ($prodIdA -gt 0) { $createdProductIds.Add($prodIdA) }

    $actualStockA = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $actualBuyA = [decimal](Invoke-SqlScalar "SELECT PurchasePrice FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $actualSellA = [decimal](Invoke-SqlScalar "SELECT SellingPrice FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    Assert-Check "Product Create" "Product attributes (Stock, Prices) persisted accurately" ($actualStockA -eq $initialStockA -and $actualBuyA -eq [decimal]$purchasePriceA -and $actualSellA -eq [decimal]$sellingPriceA) "Stock: $actualStockA, Buy: $actualBuyA, Sell: $actualSellA"

    # SQL Verification: Initial Stock Audit Transaction
    $initialTxCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Initial Stock' AND Quantity = @Qty" @{ "@ProdId" = $prodIdA; "@Qty" = $initialStockA })
    Assert-Check "Product Create" "Initial Stock audit transaction recorded in database" ($initialTxCount -eq 1) "Initial Stock Tx Count: $initialTxCount"

    # --- 1.3: GET /Products/Edit/{id} ---
    $prodEditGet = Send-HttpRequest -Uri "$BaseUrl/Products/Edit/$prodIdA" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Product Edit" "GET /Products/Edit/{id} returns 200 OK for Admin" ($prodEditGet.StatusCode -eq 200) "HTTP $($prodEditGet.StatusCode)"

    $afTokenProdEdit = Extract-AntiForgeryToken $prodEditGet.Content
    Assert-Check "Product Edit" "Extracted anti-forgery token from /Products/Edit form" (-not [string]::IsNullOrWhiteSpace($afTokenProdEdit)) "Token extracted"

    # --- 1.4: POST /Products/Edit/{id} (With Stock Change: 25 -> 35, delta +10) ---
    $updatedProdNameA = "HTTP_PROD_TEST_A_Updated_$testRunId"
    $updatedBuyA = 85.00
    $updatedSellA = 130.00
    $updatedStockA = 35
    $updatedReorderA = 10

    $prodEditBodyA = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdEdit))" +
        "&ProductId=$prodIdA" +
        "&Name=$([System.Uri]::EscapeDataString($updatedProdNameA))" +
        "&CategoryId=$catId" +
        "&PurchasePrice=$updatedBuyA" +
        "&SellingPrice=$updatedSellA" +
        "&StockQuantity=$updatedStockA" +
        "&ReorderLevel=$updatedReorderA"

    $prodEditResp = Send-HttpRequest -Uri "$BaseUrl/Products/Edit/$prodIdA" -Method "POST" -Body $prodEditBodyA -Session $adminAuth.Session
    Assert-Check "Product Edit" "POST /Products/Edit/{id} redirects (HTTP 302) on success" ($prodEditResp.StatusCode -eq 302) "HTTP $($prodEditResp.StatusCode), Location: $($prodEditResp.Location)"

    # SQL Verification: Updated product attributes
    $dbNameAfterEdit = [string](Invoke-SqlScalar "SELECT Name FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $dbStockAfterEdit = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $dbBuyAfterEdit = [decimal](Invoke-SqlScalar "SELECT PurchasePrice FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    Assert-Check "Product Edit" "Product properties updated in SQL Server" ($dbNameAfterEdit -eq $updatedProdNameA -and $dbStockAfterEdit -eq $updatedStockA -and $dbBuyAfterEdit -eq [decimal]$updatedBuyA) "Name: $dbNameAfterEdit, Stock: $dbStockAfterEdit, Buy: $dbBuyAfterEdit"

    # SQL Verification: Stock Edit Audit Transaction recorded (delta +10)
    $editTxCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Product Edit Adjustment' AND Quantity = 10" @{ "@ProdId" = $prodIdA })
    Assert-Check "Product Edit" "'Product Edit Adjustment' audit transaction recorded in database" ($editTxCount -eq 1) "Edit Tx Count: $editTxCount, Delta: +10"

    # --- 1.5: POST /Products/Edit/{id} Without Stock Change (No Audit Tx) ---
    $prodEditGet2 = Send-HttpRequest -Uri "$BaseUrl/Products/Edit/$prodIdA" -Method "GET" -Session $adminAuth.Session
    $afTokenProdEdit2 = Extract-AntiForgeryToken $prodEditGet2.Content

    $noStockChangeBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdEdit2))" +
        "&ProductId=$prodIdA" +
        "&Name=$([System.Uri]::EscapeDataString($updatedProdNameA))" +
        "&CategoryId=$catId" +
        "&PurchasePrice=$updatedBuyA" +
        "&SellingPrice=135.00" +
        "&StockQuantity=$updatedStockA" +
        "&ReorderLevel=$updatedReorderA"

    $prodEditResp2 = Send-HttpRequest -Uri "$BaseUrl/Products/Edit/$prodIdA" -Method "POST" -Body $noStockChangeBody -Session $adminAuth.Session
    Assert-Check "Product Edit" "POST /Products/Edit with no stock change redirects (HTTP 302)" ($prodEditResp2.StatusCode -eq 302) "HTTP $($prodEditResp2.StatusCode)"

    $editTxCountAfter2 = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Product Edit Adjustment'" @{ "@ProdId" = $prodIdA })
    Assert-Check "Product Edit" "No duplicate 'Product Edit Adjustment' created when stock unchanged" ($editTxCountAfter2 -eq $editTxCount) "Total Edit Txs: $editTxCountAfter2"

    # --- 1.6: Invalid Product Data Validation ---
    # Negative Purchase Price
    $badProdBody1 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdCreate))" +
        "&Name=BadPriceProd&CategoryId=$catId&PurchasePrice=-10.00&SellingPrice=50.00&StockQuantity=5&ReorderLevel=2"
    $badProdResp1 = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $badProdBody1 -Session $adminAuth.Session
    Assert-Check "Product Validation" "Negative purchase price rejected with HTTP 200 form re-render" ($badProdResp1.StatusCode -eq 200 -and [string]::IsNullOrEmpty($badProdResp1.Location)) "HTTP $($badProdResp1.StatusCode)"
    Assert-Check "Product Validation" "Response contains purchase price validation error" ($badProdResp1.Content -like "*Purchase price cannot be negative*") "Validation present"

    # Missing Name
    $badProdBody2 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdCreate))" +
        "&Name=&CategoryId=$catId&PurchasePrice=10.00&SellingPrice=20.00&StockQuantity=5&ReorderLevel=2"
    $badProdResp2 = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $badProdBody2 -Session $adminAuth.Session
    Assert-Check "Product Validation" "Missing name rejected with HTTP 200 form re-render" ($badProdResp2.StatusCode -eq 200) "HTTP $($badProdResp2.StatusCode)"

    # Non-existent CategoryId
    $badProdBody3 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdCreate))" +
        "&Name=BadCatProd&CategoryId=99999&PurchasePrice=10.00&SellingPrice=20.00&StockQuantity=5&ReorderLevel=2"
    $badProdResp3 = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $badProdBody3 -Session $adminAuth.Session
    Assert-Check "Product Validation" "Non-existent category rejected with HTTP 200" ($badProdResp3.StatusCode -eq 200) "HTTP $($badProdResp3.StatusCode)"
    Assert-Check "Product Validation" "Response contains category validation error" ($badProdResp3.Content -like "*Selected category does not exist*") "Validation present"

    # --- 1.7: Product Deletion Workflow (POST /Products/Delete/{id}) ---
    $delProdName = "HTTP_PROD_TEST_Del_$testRunId"
    $delProdInsertSql = @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, 10.00, 20.00, 0, 1);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $delProdId = [int](Invoke-SqlScalar $delProdInsertSql @{ "@Name" = $delProdName; "@CatId" = [int]$catId })
    $createdProductIds.Add($delProdId)

    $prodDelGet = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$delProdId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Product Delete" "Admin GET /Products/Delete/{id} returns 200 OK" ($prodDelGet.StatusCode -eq 200) "HTTP $($prodDelGet.StatusCode)"

    $afTokenProdDel = Extract-AntiForgeryToken $prodDelGet.Content
    $prodDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenProdDel))&ProductId=$delProdId"
    $prodDelResp = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$delProdId" -Method "POST" -Body $prodDelBody -Session $adminAuth.Session
    Assert-Check "Product Delete" "POST /Products/Delete/{id} succeeds with redirect (HTTP 302)" ($prodDelResp.StatusCode -eq 302) "HTTP $($prodDelResp.StatusCode), Location: $($prodDelResp.Location)"

    $delExists = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Products WHERE ProductId = @Id" @{ "@Id" = $delProdId })
    Assert-Check "Product Delete" "Product successfully removed from database" ($delExists -eq 0) "Product count: $delExists"

    # --- 1.8: Product Referenced by Historical SaleItems Cannot Be Deleted ---
    $saleProdName = "HTTP_PROD_TEST_SaleRef_$testRunId"
    $saleProdInsertSql = @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, 25.00, 50.00, 10, 2);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $saleProdId = [int](Invoke-SqlScalar $saleProdInsertSql @{ "@Name" = $saleProdName; "@CatId" = [int]$catId })
    $createdProductIds.Add($saleProdId)

    # Insert a sale and sale item referencing this product
    $saleInsertSql = @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (1, GETUTCDATE(), 100.00, 0.00, 'HTTP_PROD_SALE_TOKEN_$testRunId');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $prodSaleId = [int](Invoke-SqlScalar $saleInsertSql)

    $saleItemInsertSql = @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 2, 50.00, 25.00);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $prodSaleItemId = [int](Invoke-SqlScalar $saleItemInsertSql @{ "@SaleId" = $prodSaleId; "@ProdId" = $saleProdId })

    # Attempt to delete the product via GET (checks warning banner)
    $saleProdDelGet = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$saleProdId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "GET /Products/Delete on product with sales returns 200 OK" ($saleProdDelGet.StatusCode -eq 200) "HTTP $($saleProdDelGet.StatusCode)"
    Assert-Check "Historical Preservation" "GET /Products/Delete displays historical sales dependency alert" ($saleProdDelGet.Content -like "*Deletion Blocked*" -or $saleProdDelGet.Content -like "*sales item*") "Warning banner present"

    # Attempt to delete the product via POST
    $afTokenSaleProdDel = Extract-AntiForgeryToken $saleProdDelGet.Content
    $saleProdDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenSaleProdDel))&ProductId=$saleProdId"
    $saleProdDelResp = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$saleProdId" -Method "POST" -Body $saleProdDelBody -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "POST /Products/Delete on product with sales redirects to Index (HTTP 302)" ($saleProdDelResp.StatusCode -eq 302) "HTTP $($saleProdDelResp.StatusCode)"

    # Verify user-friendly error message on redirect to /Products
    $prodIndexAfterDel = Send-HttpRequest -Uri "$BaseUrl/Products" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "Redirected products index displays user-friendly historical preservation error message" ($prodIndexAfterDel.Content -like "*Cannot delete product*") "Error message rendered on Products Index"

    # SQL Server Assertions: Product, SaleItem, and Sale remain intact
    $saleProdExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Products WHERE ProductId = @Id" @{ "@Id" = $saleProdId })
    Assert-Check "Historical Preservation" "Product referenced by historical SaleItems remains in database" ($saleProdExistsAfter -eq 1) "Product count: $saleProdExistsAfter (preserved)"

    $saleItemExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM SaleItems WHERE SaleItemId = @Id" @{ "@Id" = $prodSaleItemId })
    Assert-Check "Historical Preservation" "Historical SaleItems record remains completely intact in database" ($saleItemExistsAfter -eq 1) "SaleItem count: $saleItemExistsAfter (preserved)"

    $saleExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @Id" @{ "@Id" = $prodSaleId })
    Assert-Check "Historical Preservation" "Associated historical Sales record remains completely intact in database" ($saleExistsAfter -eq 1) "Sale count: $saleExistsAfter (preserved)"

    # --- 1.9: Product Referenced by StockTransactions Cannot Be Deleted ---
    $stockTxProdName = "HTTP_PROD_TEST_StockTxRef_$testRunId"
    $stockTxProdInsertSql = @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, 30.00, 60.00, 20, 5);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $stockTxProdId = [int](Invoke-SqlScalar $stockTxProdInsertSql @{ "@Name" = $stockTxProdName; "@CatId" = [int]$catId })
    $createdProductIds.Add($stockTxProdId)

    # Insert a stock transaction referencing this product
    $stockTxInsertSql = @"
INSERT INTO StockTransactions (ProductId, Quantity, TransactionType, Note, TransactionDate, UnitCost)
VALUES (@ProdId, 20, 'Initial Stock', 'Test initial stock transaction', GETUTCDATE(), 30.00);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $stockTxId = [int](Invoke-SqlScalar $stockTxInsertSql @{ "@ProdId" = $stockTxProdId })

    # Attempt to delete product via GET
    $stockTxDelGet = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$stockTxProdId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "GET /Products/Delete on product with stock transactions returns 200 OK" ($stockTxDelGet.StatusCode -eq 200) "HTTP $($stockTxDelGet.StatusCode)"
    Assert-Check "Historical Preservation" "GET /Products/Delete displays stock transaction dependency alert" ($stockTxDelGet.Content -like "*Deletion Blocked*" -or $stockTxDelGet.Content -like "*stock audit transaction*") "Warning banner present"

    # Attempt to delete product via POST
    $afTokenStockTxDel = Extract-AntiForgeryToken $stockTxDelGet.Content
    $stockTxDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenStockTxDel))&ProductId=$stockTxProdId"
    $stockTxDelResp = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$stockTxProdId" -Method "POST" -Body $stockTxDelBody -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "POST /Products/Delete on product with stock transactions redirects to Index (HTTP 302)" ($stockTxDelResp.StatusCode -eq 302) "HTTP $($stockTxDelResp.StatusCode)"

    # SQL Server Assertions: Product and StockTransaction remain intact
    $stockTxProdExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Products WHERE ProductId = @Id" @{ "@Id" = $stockTxProdId })
    Assert-Check "Historical Preservation" "Product referenced by StockTransactions remains in database" ($stockTxProdExistsAfter -eq 1) "Product count: $stockTxProdExistsAfter (preserved)"

    $stockTxRecordExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE StockTransactionId = @Id" @{ "@Id" = $stockTxId })
    Assert-Check "Historical Preservation" "Historical StockTransactions record remains completely intact in database" ($stockTxRecordExistsAfter -eq 1) "StockTransaction count: $stockTxRecordExistsAfter (preserved)"

    # =========================================================================
    # SECTION 2: STOCK HTTP WORKFLOWS (RESTOCK & STOCK ADJUSTMENT)
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 2: Restock and Stock Adjustment Workflows ---" -ForegroundColor Yellow

    # Current stock of $prodIdA is 35
    $stockBeforeRestock = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    Write-Host "  [INFO] Product #$prodIdA stock before restock: $stockBeforeRestock" -ForegroundColor Gray

    # --- 2.1: Restock (GET /Stock/Create & POST /Stock/Create) via Manager Session ---
    # Manager is permitted to restock!
    $restockGet = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Stock Restock" "Manager GET /Stock/Create returns 200 OK" ($restockGet.StatusCode -eq 200) "HTTP $($restockGet.StatusCode)"

    $afTokenRestock = Extract-AntiForgeryToken $restockGet.Content
    Assert-Check "Stock Restock" "Extracted anti-forgery token from /Stock/Create form" (-not [string]::IsNullOrWhiteSpace($afTokenRestock)) "Token extracted"

    $restockQty = 15
    $restockUnitCost = 85.00
    $restockNote = "HTTP Test Restock Batch 1 - $testRunId"

    $restockBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenRestock))" +
        "&ProductId=$prodIdA" +
        "&Quantity=$restockQty" +
        "&UnitCost=$restockUnitCost" +
        "&Note=$([System.Uri]::EscapeDataString($restockNote))"

    $restockResp = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "POST" -Body $restockBody -Session $mgrAuth.Session
    Assert-Check "Stock Restock" "POST /Stock/Create succeeds with redirect (HTTP 302)" ($restockResp.StatusCode -eq 302) "HTTP $($restockResp.StatusCode), Location: $($restockResp.Location)"

    # SQL Verification: Stock quantity incremented
    $stockAfterRestock = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $expectedStockAfterRestock = $stockBeforeRestock + $restockQty
    Assert-Check "Stock Restock" "Product stock quantity accurately increased in SQL Server" ($stockAfterRestock -eq $expectedStockAfterRestock) "Before: $stockBeforeRestock, Added: $restockQty, Current: $stockAfterRestock"

    # SQL Verification: Restock Audit Transaction
    $restockTxCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Restock' AND Quantity = @Qty AND Note = @Note" @{ "@ProdId" = $prodIdA; "@Qty" = $restockQty; "@Note" = $restockNote })
    Assert-Check "Stock Restock" "'Restock' stock transaction audit record recorded in database" ($restockTxCount -eq 1) "Restock Tx Count: $restockTxCount"

    # --- 2.2: Stock Adjustment (GET /Stock/Adjust & POST /Stock/Adjust) via Admin Session ---
    # Adjustment is Admin-only!
    $adjustGet = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Stock Adjust" "Admin GET /Stock/Adjust returns 200 OK" ($adjustGet.StatusCode -eq 200) "HTTP $($adjustGet.StatusCode)"

    $afTokenAdjust = Extract-AntiForgeryToken $adjustGet.Content
    Assert-Check "Stock Adjust" "Extracted anti-forgery token from /Stock/Adjust form" (-not [string]::IsNullOrWhiteSpace($afTokenAdjust)) "Token extracted"

    # Downward adjustment (-8 units, e.g. damaged goods correction)
    $adjustQty = -8
    $adjustReason = "HTTP Test Damaged goods correction - $testRunId"

    $adjustBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenAdjust))" +
        "&ProductId=$prodIdA" +
        "&Quantity=$adjustQty" +
        "&Reason=$([System.Uri]::EscapeDataString($adjustReason))"

    $adjustResp = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $adjustBody -Session $adminAuth.Session
    Assert-Check "Stock Adjust" "POST /Stock/Adjust succeeds with redirect (HTTP 302)" ($adjustResp.StatusCode -eq 302) "HTTP $($adjustResp.StatusCode), Location: $($adjustResp.Location)"

    # SQL Verification: Stock quantity decremented
    $stockAfterAdjust = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    $expectedStockAfterAdjust = $stockAfterRestock + $adjustQty
    Assert-Check "Stock Adjust" "Product stock quantity accurately adjusted in SQL Server" ($stockAfterAdjust -eq $expectedStockAfterAdjust) "Before: $stockAfterRestock, Adjusted: $adjustQty, Current: $stockAfterAdjust"

    # SQL Verification: Adjustment Audit Transaction
    $adjustTxCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Adjustment' AND Quantity = @Qty AND Note = @Reason" @{ "@ProdId" = $prodIdA; "@Qty" = $adjustQty; "@Reason" = $adjustReason })
    Assert-Check "Stock Adjust" "'Adjustment' stock transaction audit record recorded in database" ($adjustTxCount -eq 1) "Adjustment Tx Count: $adjustTxCount"

    # --- 2.3: Invalid Stock Workflows ---
    # Zero quantity restock
    $badRestockBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenRestock))&ProductId=$prodIdA&Quantity=0&UnitCost=50.00"
    $badRestockResp = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "POST" -Body $badRestockBody -Session $mgrAuth.Session
    Assert-Check "Stock Validation" "Zero quantity restock rejected with HTTP 200 form re-render" ($badRestockResp.StatusCode -eq 200 -and [string]::IsNullOrEmpty($badRestockResp.Location)) "HTTP $($badRestockResp.StatusCode)"
    Assert-Check "Stock Validation" "Stock unchanged after invalid zero quantity restock" ([int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA }) -eq $stockAfterAdjust) "Stock remains: $stockAfterAdjust"

    # Zero quantity adjustment
    $badAdjustBody1 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenAdjust))&ProductId=$prodIdA&Quantity=0&Reason=ZeroCorrection"
    $badAdjustResp1 = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $badAdjustBody1 -Session $adminAuth.Session
    Assert-Check "Stock Validation" "Zero quantity adjustment rejected with HTTP 200" ($badAdjustResp1.StatusCode -eq 200) "HTTP $($badAdjustResp1.StatusCode)"
    Assert-Check "Stock Validation" "Response contains zero quantity adjustment error" ($badAdjustResp1.Content -like "*Adjustment quantity cannot be zero*") "Validation present"

    # Overdraft adjustment causing negative stock
    $overdraftQty = -($stockAfterAdjust + 100)
    $badAdjustBody2 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenAdjust))&ProductId=$prodIdA&Quantity=$overdraftQty&Reason=OverdraftCorrection"
    $badAdjustResp2 = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $badAdjustBody2 -Session $adminAuth.Session
    Assert-Check "Stock Validation" "Overdraft adjustment causing negative stock rejected with HTTP 200" ($badAdjustResp2.StatusCode -eq 200) "HTTP $($badAdjustResp2.StatusCode)"
    Assert-Check "Stock Validation" "Response contains negative stock overdraft error message" ($badAdjustResp2.Content -like "*Stock cannot become negative*") "Validation present"
    Assert-Check "Stock Validation" "Stock quantity unchanged after overdraft adjustment attempt" ([int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA }) -eq $stockAfterAdjust) "Stock remains: $stockAfterAdjust"

    # =========================================================================
    # SECTION 3: AUTHORIZATION & ANTI-FORGERY SECURITY ENFORCEMENT
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 3: Authorization and Anti-Forgery Security Enforcement ---" -ForegroundColor Yellow

    $anonSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    # --- 3.1: Anonymous Access Blocked ---
    $anonProdCreate = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $prodPostBodyA -Session $anonSession
    $anonProdBlocked = ($anonProdCreate.StatusCode -eq 302 -and $anonProdCreate.Location -like "*Account/Login*")
    Assert-Check "Security" "Anonymous POST /Products/Create blocked with redirect to Login" $anonProdBlocked "HTTP $($anonProdCreate.StatusCode), Location: $($anonProdCreate.Location)"

    $anonStockRestock = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "POST" -Body $restockBody -Session $anonSession
    $anonStockBlocked = ($anonStockRestock.StatusCode -eq 302 -and $anonStockRestock.Location -like "*Account/Login*")
    Assert-Check "Security" "Anonymous POST /Stock/Create blocked with redirect to Login" $anonStockBlocked "HTTP $($anonStockRestock.StatusCode), Location: $($anonStockRestock.Location)"

    $anonStockAdjust = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $adjustBody -Session $anonSession
    $anonAdjustBlocked = ($anonStockAdjust.StatusCode -eq 302 -and $anonStockAdjust.Location -like "*Account/Login*")
    Assert-Check "Security" "Anonymous POST /Stock/Adjust blocked with redirect to Login" $anonAdjustBlocked "HTTP $($anonStockAdjust.StatusCode), Location: $($anonStockAdjust.Location)"

    # --- 3.2: Customer Access Blocked ---
    $custProdCreate = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $prodPostBodyA -Session $custAuth.Session
    $custProdBlocked = ($custProdCreate.StatusCode -eq 403 -or ($custProdCreate.StatusCode -eq 302 -and $custProdCreate.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Products/Create blocked (AccessDenied/403)" $custProdBlocked "HTTP $($custProdCreate.StatusCode), Location: $($custProdCreate.Location)"

    $custStockRestock = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "POST" -Body $restockBody -Session $custAuth.Session
    $custRestockBlocked = ($custStockRestock.StatusCode -eq 403 -or ($custStockRestock.StatusCode -eq 302 -and $custStockRestock.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Stock/Create blocked (AccessDenied/403)" $custRestockBlocked "HTTP $($custStockRestock.StatusCode), Location: $($custStockRestock.Location)"

    $custStockAdjust = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $adjustBody -Session $custAuth.Session
    $custAdjustBlocked = ($custStockAdjust.StatusCode -eq 403 -or ($custStockAdjust.StatusCode -eq 302 -and $custStockAdjust.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Stock/Adjust blocked (AccessDenied/403)" $custAdjustBlocked "HTTP $($custStockAdjust.StatusCode), Location: $($custStockAdjust.Location)"

    # --- 3.3: Manager Access Restrictions (Admin-Only Endpoints) ---
    # Manager is blocked from /Products/Create
    $mgrProdCreate = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $prodPostBodyA -Session $mgrAuth.Session
    $mgrProdBlocked = ($mgrProdCreate.StatusCode -eq 403 -or ($mgrProdCreate.StatusCode -eq 302 -and $mgrProdCreate.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role blocked from POST /Products/Create (Admin-only policy enforced)" $mgrProdBlocked "HTTP $($mgrProdCreate.StatusCode), Location: $($mgrProdCreate.Location)"

    # Manager is blocked from /Products/Edit/{id}
    $mgrProdEdit = Send-HttpRequest -Uri "$BaseUrl/Products/Edit/$prodIdA" -Method "POST" -Body $prodEditBodyA -Session $mgrAuth.Session
    $mgrEditBlocked = ($mgrProdEdit.StatusCode -eq 403 -or ($mgrProdEdit.StatusCode -eq 302 -and $mgrProdEdit.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role blocked from POST /Products/Edit (Admin-only policy enforced)" $mgrEditBlocked "HTTP $($mgrProdEdit.StatusCode), Location: $($mgrProdEdit.Location)"

    # Manager is blocked from /Products/Delete/{id}
    $mgrProdDel = Send-HttpRequest -Uri "$BaseUrl/Products/Delete/$prodIdA" -Method "POST" -Body "ProductId=$prodIdA" -Session $mgrAuth.Session
    $mgrDelBlocked = ($mgrProdDel.StatusCode -eq 403 -or ($mgrProdDel.StatusCode -eq 302 -and $mgrProdDel.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role blocked from POST /Products/Delete (Admin-only policy enforced)" $mgrDelBlocked "HTTP $($mgrProdDel.StatusCode), Location: $($mgrProdDel.Location)"

    # Manager is blocked from /Stock/Adjust
    $mgrStockAdjust = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $adjustBody -Session $mgrAuth.Session
    $mgrAdjustBlocked = ($mgrStockAdjust.StatusCode -eq 403 -or ($mgrStockAdjust.StatusCode -eq 302 -and $mgrStockAdjust.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role blocked from POST /Stock/Adjust (Admin-only policy enforced)" $mgrAdjustBlocked "HTTP $($mgrStockAdjust.StatusCode), Location: $($mgrStockAdjust.Location)"

    # Verify product remained untouched after manager unauthorized mutation attempts
    $prodStillOk = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdA })
    Assert-Check "Security" "Product record remains intact after unauthorized mutation attempts" ($prodStillOk -eq 1) "Product count: $prodStillOk"

    # --- 3.4: Anti-Forgery Token Validation ---
    # Missing anti-forgery token on Product Create
    $noAfProdBody = "Name=NoAfProd&CategoryId=$catId&PurchasePrice=10&SellingPrice=20&StockQuantity=5&ReorderLevel=1"
    $noAfProdResp = Send-HttpRequest -Uri "$BaseUrl/Products/Create" -Method "POST" -Body $noAfProdBody -Session $adminAuth.Session
    Assert-Check "Security" "POST /Products/Create without anti-forgery token rejected with HTTP 400" ($noAfProdResp.StatusCode -eq 400) "HTTP $($noAfProdResp.StatusCode)"

    # Missing anti-forgery token on Stock Adjust
    $noAfAdjustBody = "ProductId=$prodIdA&Quantity=5&Reason=NoTokenAdjust"
    $noAfAdjustResp = Send-HttpRequest -Uri "$BaseUrl/Stock/Adjust" -Method "POST" -Body $noAfAdjustBody -Session $adminAuth.Session
    Assert-Check "Security" "POST /Stock/Adjust without anti-forgery token rejected with HTTP 400" ($noAfAdjustResp.StatusCode -eq 400) "HTTP $($noAfAdjustResp.StatusCode)"

    # Tampered anti-forgery token on Stock Restock
    $badAfRestockBody = "__RequestVerificationToken=TAMPERED_AF_TOKEN_12345&ProductId=$prodIdA&Quantity=5&UnitCost=85.00"
    $badAfRestockResp = Send-HttpRequest -Uri "$BaseUrl/Stock/Create" -Method "POST" -Body $badAfRestockBody -Session $mgrAuth.Session
    Assert-Check "Security" "POST /Stock/Create with tampered anti-forgery token rejected with HTTP 400" ($badAfRestockResp.StatusCode -eq 400) "HTTP $($badAfRestockResp.StatusCode)"
}
finally {
    # =========================================================================
    # PHASE 4: GUARANTEED TEST ISOLATION & CLEANUP
    # =========================================================================
    Write-Host ""
    Write-Host "[CLEANUP] Executing teardown of test-created data..." -ForegroundColor Gray

    try {
        # Clean sales created during test
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId IN (SELECT SaleId FROM Sales WHERE CheckoutToken LIKE 'HTTP_PROD_SALE_TOKEN_%')"
        Invoke-SqlNonQuery "DELETE FROM Sales WHERE CheckoutToken LIKE 'HTTP_PROD_SALE_TOKEN_%'"

        foreach ($pidToClean in $createdProductIds) {
            Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
            Invoke-SqlNonQuery "DELETE FROM Products WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
        }

        # Wildcard cleanup for any leftover test products/transactions
        Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_PROD_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_PROD_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM Products WHERE Name LIKE 'HTTP_PROD_TEST_%'"

        Write-Host "[CLEANUP] Database test entities cleaned up successfully." -ForegroundColor Green
    }
    catch {
        Write-Host "[CLEANUP ERROR] Failed during SQL cleanup: $($_.Exception.Message)" -ForegroundColor Red
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
Write-Host "PRODUCT & STOCK HTTP INTEGRATION TEST SUITE SUMMARY" -ForegroundColor Cyan
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

