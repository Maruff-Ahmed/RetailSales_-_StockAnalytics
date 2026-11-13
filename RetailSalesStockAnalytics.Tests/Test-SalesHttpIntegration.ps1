<#
.SYNOPSIS
    Repeatable HTTP Integration Test Suite for Sales Checkout and Sale Cancellation.
.DESCRIPTION
    Validates ASP.NET Core MVC Sales endpoints (/Sales/Create, /Sales/Delete/{id})
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
    Write-Host "[INFO] HTTP integration tests skipped (opt-in switch -Functional was not supplied)." -ForegroundColor Yellow
    Write-Host "================================================="
    Write-Host "HTTP TEST RESULTS: SKIPPED (Use -Functional to execute)"
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
Write-Host "STARTING SALES HTTP INTEGRATION TEST SUITE" -ForegroundColor Cyan
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

function Invoke-SqlQuery {
    param([string]$Sql, [hashtable]$Parameters = @{})
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        foreach ($p in $Parameters.Keys) {
            [void]$cmd.Parameters.AddWithValue($p, $Parameters[$p])
        }
        $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
        $ds = New-Object System.Data.DataSet
        [void]$adapter.Fill($ds)
        return $ds
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

function Extract-CheckoutToken {
    param([string]$Html)
    $match = [regex]::Match($Html, 'id="CheckoutToken"[^>]*value="([^"]*)"')
    if (-not $match.Success) {
        $match = [regex]::Match($Html, 'name="CheckoutToken"[^>]*value="([^"]*)"')
    }
    if (-not $match.Success) {
        $match = [regex]::Match($Html, 'value="([^"]*)"[^>]*name="CheckoutToken"')
    }
    if ($match.Success -and -not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
        return $match.Groups[1].Value
    }
    return [System.Guid]::NewGuid().ToString("N")
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

# Track all created IDs for guaranteed cleanup
$createdProductIds = New-Object System.Collections.Generic.List[int]
$createdCustomerIds = New-Object System.Collections.Generic.List[int]
$createdSaleIds = New-Object System.Collections.Generic.List[int]
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
    $testProductName = "HTTP_TEST_Prod_$testRunId"
    $initialStock = 50
    $sellingPrice = 150.00
    $purchasePrice = 90.00

    # Ensure standard category 1 exists
    $catId = Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories WHERE CategoryId = 1"
    if (-not $catId) {
        $catId = Invoke-SqlScalar "SELECT TOP 1 CategoryId FROM Categories"
    }

    $prodInsertSql = @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES (@Name, @CatId, @PurchasePrice, @SellingPrice, @Stock, 5);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $testProdId = Invoke-SqlScalar $prodInsertSql @{
        "@Name" = $testProductName
        "@CatId" = [int]$catId
        "@PurchasePrice" = [decimal]$purchasePrice
        "@SellingPrice" = [decimal]$sellingPrice
        "@Stock" = [int]$initialStock
    }
    $createdProductIds.Add([int]$testProdId)
    Write-Host "[SETUP] Created test product '$testProductName' (ID: $testProdId) with stock = $initialStock." -ForegroundColor Gray

    # Helper: Authenticate session
    function Authenticate-User {
        param(
            [string]$Email,
            [string]$Password
        )
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

    $mgrAuth = Authenticate-User -Email "manager@gmail.com" -Password "Manager@Retail2026!"
    $adminAuth = Authenticate-User -Email "admin@gmail.com" -Password "Admin@Retail2026!"
    $custAuth = Authenticate-User -Email "testcustomer.functional@example.com" -Password "Customer@Retail2026!"

    Assert-Check "Auth" "Manager login succeeded" $mgrAuth.IsAuthenticated "HTTP $($mgrAuth.StatusCode)"
    Assert-Check "Auth" "Admin login succeeded" $adminAuth.IsAuthenticated "HTTP $($adminAuth.StatusCode)"
    Assert-Check "Auth" "Customer login succeeded" $custAuth.IsAuthenticated "HTTP $($custAuth.StatusCode)"

    # =========================================================================
    # TEST A: SUCCESSFUL SALES CHECKOUT (POST /Sales/Create)
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST A: Successful Sales Checkout Workflow ---" -ForegroundColor Yellow

    # 1. GET /Sales/Create
    $createPage = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Test A" "GET /Sales/Create returns 200 OK" ($createPage.StatusCode -eq 200) "HTTP $($createPage.StatusCode)"
    
    $afTokenA = Extract-AntiForgeryToken $createPage.Content
    $checkoutTokenA = Extract-CheckoutToken $createPage.Content
    Write-Host "  [DEBUG] Extracted AF Token length: $($afTokenA.Length), CheckoutToken: '$checkoutTokenA'" -ForegroundColor Magenta
    Assert-Check "Test A" "Extracted valid anti-forgery and checkout tokens" (-not [string]::IsNullOrWhiteSpace($afTokenA) -and -not [string]::IsNullOrWhiteSpace($checkoutTokenA)) "Token length: $($afTokenA.Length)"

    # 2. POST /Sales/Create with valid payload
    $custNameA = "HTTP_TEST_CustA_$testRunId"
    $qtyA = 3
    $discountA = 20.00
    $expectedTotalA = ($sellingPrice * $qtyA) - $discountA
    $uniquePhoneA = "017" + (Get-Random -Minimum 10000000 -Maximum 99999999)

    $postBodyA = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$([System.Uri]::EscapeDataString($checkoutTokenA))" +
        "&CustomerName=$([System.Uri]::EscapeDataString($custNameA))" +
        "&CustomerPhone=$uniquePhoneA" +
        "&CustomerEmail=testA_$($testRunId)@example.com" +
        "&SaleDate=$([System.Uri]::EscapeDataString((Get-Date).ToString("yyyy-MM-ddTHH:mm")))" +
        "&Discount=$discountA" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=$qtyA"

    $checkoutRespA = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyA -Session $mgrAuth.Session
    Assert-Check "Test A" "POST /Sales/Create redirects (HTTP 302) on success" ($checkoutRespA.StatusCode -eq 302) "HTTP $($checkoutRespA.StatusCode), Location: $($checkoutRespA.Location)"

    # 3. Database verification: Sale persisted
    $saleIdA = [int](Invoke-SqlScalar "SELECT SaleId FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $checkoutTokenA })
    if ($saleIdA -eq 0) {
        $saleIdA = [int](Invoke-SqlScalar "SELECT TOP 1 s.SaleId FROM Sales s INNER JOIN Customers c ON s.CustomerId = c.CustomerId WHERE c.Name LIKE 'HTTP_TEST_CustA%' ORDER BY s.SaleId DESC")
    }

    $saleCountA = if ($saleIdA -gt 0) { [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @Id" @{ "@Id" = $saleIdA }) } else { 0 }
    $saleCreatedA = ($saleIdA -gt 0 -and $saleCountA -eq 1)
    Assert-Check "Test A" "Sale persisted in SQL Server" $saleCreatedA "SaleId: $saleIdA, Exists: $saleCreatedA"

    if ($saleCreatedA) {
        $createdSaleIds.Add($saleIdA)
        $actualTotalA = [decimal](Invoke-SqlScalar "SELECT TotalAmount FROM Sales WHERE SaleId = @Id" @{ "@Id" = $saleIdA })
        $actualCustIdA = [int](Invoke-SqlScalar "SELECT CustomerId FROM Sales WHERE SaleId = @Id" @{ "@Id" = $saleIdA })
        $createdCustomerIds.Add($actualCustIdA)
        Assert-Check "Test A" "Sale TotalAmount calculated correctly" ($actualTotalA -eq [decimal]$expectedTotalA) "Expected: $expectedTotalA, Actual: $actualTotalA"
        
        # Verify SaleItems
        $itemsCountA = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM SaleItems WHERE SaleId = @SaleId AND ProductId = @ProdId AND Quantity = @Qty AND UnitPrice = @Price" @{ "@SaleId" = $saleIdA; "@ProdId" = $testProdId; "@Qty" = $qtyA; "@Price" = [decimal]$sellingPrice })
        Assert-Check "Test A" "SaleItem persisted with correct quantity and prices" ($itemsCountA -eq 1) "Matching items: $itemsCountA"
    }

    # 4. Inventory deduction verification
    $stockAfterA = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    $expectedStockA = $initialStock - $qtyA
    Assert-Check "Test A" "Product stock deducted accurately in database" ($stockAfterA -eq $expectedStockA) "Initial: $initialStock, Deducted: $qtyA, Current: $stockAfterA"

    # 5. Outbound StockTransaction verification
    $txCountA = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND Quantity = @Qty AND TransactionType = 'Sale' AND Note LIKE '%Sale #' + CAST(@SaleId AS VARCHAR) + '%'" @{ "@ProdId" = $testProdId; "@Qty" = (-$qtyA); "@SaleId" = $saleIdA })
    Assert-Check "Test A" "Outbound Sale StockTransaction recorded" ($txCountA -eq 1) "Recorded Sale Tx Count: $txCountA, Expected Qty: -$qtyA"

    # =========================================================================
    # TEST B: INVALID CHECKOUT SUBMISSION
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST B: Invalid Checkout Handling ---" -ForegroundColor Yellow

    # B.1: Non-positive quantity
    $badTokenB1 = [System.Guid]::NewGuid().ToString("N")
    $postBodyB1 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$badTokenB1" +
        "&CustomerName=HTTP_TEST_InvalidQty" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=0"

    $respB1 = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyB1 -Session $mgrAuth.Session
    Assert-Check "Test B" "Zero quantity checkout rejected with HTTP 200 form re-render" ($respB1.StatusCode -eq 200) "HTTP $($respB1.StatusCode)"
    $reRenderedFormB1 = ($respB1.StatusCode -eq 200 -and ($respB1.Content -like "*saleCreateForm*" -or $respB1.Content -like "*New Sale*") -and [string]::IsNullOrEmpty($respB1.Location))
    Assert-Check "Test B" "Zero quantity checkout stays on page and re-renders form" $reRenderedFormB1 "Form re-rendered without redirect"

    $saleCountB1 = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $badTokenB1 })
    Assert-Check "Test B" "No sale persisted for zero quantity submission" ($saleCountB1 -eq 0) "Sales count: $saleCountB1"

    $stockAfterB1 = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    Assert-Check "Test B" "Product stock unchanged after invalid quantity checkout" ($stockAfterB1 -eq $stockAfterA) "Stock: $stockAfterB1 (Expected $stockAfterA)"

    # B.2: Missing required customer name
    $badTokenB2 = [System.Guid]::NewGuid().ToString("N")
    $postBodyB2 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$badTokenB2" +
        "&CustomerName=" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=1"

    $respB2 = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyB2 -Session $mgrAuth.Session
    Assert-Check "Test B" "Missing customer name checkout rejected with HTTP 200" ($respB2.StatusCode -eq 200) "HTTP $($respB2.StatusCode)"
    Assert-Check "Test B" "Response contains CustomerName validation error" ($respB2.Content -like "*Customer name is required*") "Validation present"

    $saleCountB2 = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $badTokenB2 })
    Assert-Check "Test B" "No sale persisted for missing customer name submission" ($saleCountB2 -eq 0) "Sales count: $saleCountB2"

    # B.3: Overselling / insufficient stock
    $badTokenB3 = [System.Guid]::NewGuid().ToString("N")
    $postBodyB3 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$badTokenB3" +
        "&CustomerName=HTTP_TEST_Oversell" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=9999"

    $respB3 = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyB3 -Session $mgrAuth.Session
    Assert-Check "Test B" "Insufficient stock checkout rejected with HTTP 200" ($respB3.StatusCode -eq 200) "HTTP $($respB3.StatusCode)"
    Assert-Check "Test B" "Response contains insufficient stock error message" ($respB3.Content -like "*Not enough stock*") "Validation present"

    $saleCountB3 = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $badTokenB3 })
    Assert-Check "Test B" "No sale persisted for oversell attempt" ($saleCountB3 -eq 0) "Sales count: $saleCountB3"

    $stockAfterB3 = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    Assert-Check "Test B" "Product stock completely protected against overselling" ($stockAfterB3 -eq $stockAfterA) "Stock: $stockAfterB3"

    # =========================================================================
    # TEST C: SALE CANCELLATION WORKFLOW (POST /Sales/Delete/{id})
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST C: Sale Cancellation & Stock Restoration Workflow ---" -ForegroundColor Yellow

    # 1. Create a sale specifically designated for cancellation
    $checkoutTokenC = [System.Guid]::NewGuid().ToString("N")
    $qtyC = 5
    $postBodyC = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$([System.Uri]::EscapeDataString($checkoutTokenC))" +
        "&CustomerName=HTTP_TEST_CancelCust_$testRunId" +
        "&Discount=0" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=$qtyC"

    $checkoutRespC = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyC -Session $mgrAuth.Session
    Assert-Check "Test C" "Setup: Sale created for cancellation test (HTTP 302)" ($checkoutRespC.StatusCode -eq 302) "HTTP $($checkoutRespC.StatusCode)"

    $saleIdC = [int](Invoke-SqlScalar "SELECT SaleId FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $checkoutTokenC })
    $createdSaleIds.Add($saleIdC)
    $stockBeforeCancel = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    Write-Host "  [INFO] Sale #$saleIdC created. Stock before cancellation: $stockBeforeCancel" -ForegroundColor Gray

    # 2. GET /Sales/Delete/{id} as Admin
    $deleteGetResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$saleIdC" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test C" "Admin GET /Sales/Delete/{id} returns 200 OK" ($deleteGetResp.StatusCode -eq 200) "HTTP $($deleteGetResp.StatusCode)"
    Assert-Check "Test C" "Delete confirmation page displays Sale ID and Notice" ($deleteGetResp.Content -like "*#$saleIdC*" -and $deleteGetResp.Content -like "*Inventory Restoration Notice*") "HTML verified"

    $afTokenDelete = Extract-AntiForgeryToken $deleteGetResp.Content
    Assert-Check "Test C" "Extracted anti-forgery token from delete confirmation form" (-not [string]::IsNullOrWhiteSpace($afTokenDelete)) "Token extracted"

    # 3. POST /Sales/Delete/{id} as Admin
    $deleteBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenDelete))&SaleId=$saleIdC"
    $deletePostResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$saleIdC" -Method "POST" -Body $deleteBody -Session $adminAuth.Session
    Assert-Check "Test C" "POST /Sales/Delete/{id} succeeds with redirect (HTTP 302)" ($deletePostResp.StatusCode -eq 302) "HTTP $($deletePostResp.StatusCode), Location: $($deletePostResp.Location)"

    # 4. Database verification: Sale deleted
    $saleExistsC = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @Id" @{ "@Id" = $saleIdC })
    Assert-Check "Test C" "Sale record removed from SQL Server" ($saleExistsC -eq 0) "Sales count: $saleExistsC"

    $itemsExistC = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM SaleItems WHERE SaleId = @Id" @{ "@Id" = $saleIdC })
    Assert-Check "Test C" "Associated SaleItems cascade-deleted" ($itemsExistC -eq 0) "SaleItems count: $itemsExistC"

    # 5. Inventory restored verification
    $stockAfterCancel = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    $expectedStockAfterCancel = $stockBeforeCancel + $qtyC
    Assert-Check "Test C" "Product inventory accurately restored (+ $qtyC units)" ($stockAfterCancel -eq $expectedStockAfterCancel) "Before: $stockBeforeCancel, Restored: $qtyC, After: $stockAfterCancel"

    # 6. Audit transaction recorded
    $cancelTxCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Sale Cancellation' AND Note LIKE '%#' + CAST(@SaleId AS VARCHAR)" @{ "@ProdId" = $testProdId; "@SaleId" = $saleIdC })
    Assert-Check "Test C" "'Sale Cancellation' stock audit transaction recorded in database" ($cancelTxCount -eq 1) "Cancel Tx Count: $cancelTxCount"

    # =========================================================================
    # TEST D: DUPLICATE SALE CANCELLATION
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST D: Duplicate Cancellation Protection ---" -ForegroundColor Yellow

    # Attempt to cancel the exact same already-deleted sale again
    $dupDeleteResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$saleIdC" -Method "POST" -Body $deleteBody -Session $adminAuth.Session
    Assert-Check "Test D" "Duplicate cancellation redirected safely without crashing (HTTP 302)" ($dupDeleteResp.StatusCode -eq 302) "HTTP $($dupDeleteResp.StatusCode)"

    # Verify inventory was NOT restored a second time
    $stockAfterDupCancel = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })
    Assert-Check "Test D" "Inventory was NOT restored a second time" ($stockAfterDupCancel -eq $stockAfterCancel) "Stock: $stockAfterDupCancel (Expected $stockAfterCancel)"

    # Verify no second audit transaction recorded
    $cancelTxCountDup = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM StockTransactions WHERE ProductId = @ProdId AND TransactionType = 'Sale Cancellation' AND Note LIKE '%#' + CAST(@SaleId AS VARCHAR)" @{ "@ProdId" = $testProdId; "@SaleId" = $saleIdC })
    Assert-Check "Test D" "Duplicate cancellation did not record duplicate audit transaction" ($cancelTxCountDup -eq 1) "Cancel Tx Count: $cancelTxCountDup"

    # =========================================================================
    # TEST E: AUTHORIZATION AND ANTI-FORGERY SECURITY ENFORCEMENT
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST E: Authorization and Anti-Forgery Security Enforcement ---" -ForegroundColor Yellow

    # Setup an active sale for authorization tests
    $checkoutTokenE = [System.Guid]::NewGuid().ToString("N")
    $postBodyE = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenA))" +
        "&CheckoutToken=$([System.Uri]::EscapeDataString($checkoutTokenE))" +
        "&CustomerName=HTTP_TEST_AuthCust_$testRunId" +
        "&Discount=0" +
        "&Items%5B0%5D.ProductId=$testProdId" +
        "&Items%5B0%5D.Quantity=2"

    [void](Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyE -Session $mgrAuth.Session)
    $activeSaleIdE = [int](Invoke-SqlScalar "SELECT SaleId FROM Sales WHERE CheckoutToken = @Token" @{ "@Token" = $checkoutTokenE })
    $createdSaleIds.Add($activeSaleIdE)
    $stockAtE = [int](Invoke-SqlScalar "SELECT StockQuantity FROM Products WHERE ProductId = @Id" @{ "@Id" = $testProdId })

    # E.1: Anonymous Checkout Blocked
    $anonSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $anonCheckoutResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyE -Session $anonSession
    $anonBlocked = ($anonCheckoutResp.StatusCode -eq 302 -and $anonCheckoutResp.Location -like "*Account/Login*")
    Assert-Check "Test E" "Anonymous user checkout blocked with redirect to Login" $anonBlocked "HTTP $($anonCheckoutResp.StatusCode), Location: $($anonCheckoutResp.Location)"

    # E.2: Anonymous Cancellation Blocked
    $anonDeleteResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$activeSaleIdE" -Method "POST" -Body "SaleId=$activeSaleIdE" -Session $anonSession
    $anonDeleteBlocked = ($anonDeleteResp.StatusCode -eq 302 -and $anonDeleteResp.Location -like "*Account/Login*")
    Assert-Check "Test E" "Anonymous user cancellation blocked with redirect to Login" $anonDeleteBlocked "HTTP $($anonDeleteResp.StatusCode), Location: $($anonDeleteResp.Location)"

    # E.3: Customer Role Checkout Blocked
    $custCheckoutResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $postBodyE -Session $custAuth.Session
    $custBlocked = ($custCheckoutResp.StatusCode -eq 403 -or ($custCheckoutResp.StatusCode -eq 302 -and $custCheckoutResp.Location -like "*AccessDenied*"))
    Assert-Check "Test E" "Customer role checkout blocked with AccessDenied/403" $custBlocked "HTTP $($custCheckoutResp.StatusCode), Location: $($custCheckoutResp.Location)"

    # E.4: Manager Role Cancellation Blocked (Admin-only action)
    $mgrDeleteResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$activeSaleIdE" -Method "POST" -Body $deleteBody -Session $mgrAuth.Session
    $mgrDeleteBlocked = ($mgrDeleteResp.StatusCode -eq 403 -or ($mgrDeleteResp.StatusCode -eq 302 -and $mgrDeleteResp.Location -like "*AccessDenied*"))
    Assert-Check "Test E" "Manager role cancellation blocked (Admin-only policy enforced)" $mgrDeleteBlocked "HTTP $($mgrDeleteResp.StatusCode), Location: $($mgrDeleteResp.Location)"

    # Verify sale still exists and stock untouched after manager deletion attempt
    $saleStillExists = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @Id" @{ "@Id" = $activeSaleIdE })
    Assert-Check "Test E" "Sale remains intact after unauthorized cancellation attempt" ($saleStillExists -eq 1) "Sale count: $saleStillExists"

    # E.5: Missing Anti-Forgery Token Rejected (HTTP 400)
    $noAfBody = "CheckoutToken=$([System.Guid]::NewGuid().ToString('N'))&CustomerName=NoTokenCust&Items%5B0%5D.ProductId=$testProdId&Items%5B0%5D.Quantity=1"
    $noAfResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Create" -Method "POST" -Body $noAfBody -Session $mgrAuth.Session
    Assert-Check "Test E" "POST /Sales/Create without anti-forgery token rejected with HTTP 400" ($noAfResp.StatusCode -eq 400) "HTTP $($noAfResp.StatusCode)"

    # E.6: Invalid Anti-Forgery Token on Cancellation Rejected (HTTP 400)
    $badAfDeleteBody = "__RequestVerificationToken=INVALID_TAMPERED_TOKEN_99999&SaleId=$activeSaleIdE"
    $badAfResp = Send-HttpRequest -Uri "$BaseUrl/Sales/Delete/$activeSaleIdE" -Method "POST" -Body $badAfDeleteBody -Session $adminAuth.Session
    Assert-Check "Test E" "POST /Sales/Delete with invalid anti-forgery token rejected with HTTP 400" ($badAfResp.StatusCode -eq 400) "HTTP $($badAfResp.StatusCode)"

    # Verify sale still exists
    $saleStillExists2 = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @Id" @{ "@Id" = $activeSaleIdE })
    Assert-Check "Test E" "Sale remains intact after tampered anti-forgery token attempt" ($saleStillExists2 -eq 1) "Sale count: $saleStillExists2"

    # =========================================================================
    # TEST F: SERVER-SIDE PAGINATION, FILTERING & NAVIGATION (Task 4.5.3)
    # =========================================================================
    Write-Host ""
    Write-Host "--- TEST F: Server-Side Pagination, Filtering & Navigation ---" -ForegroundColor Yellow

    # F.1: Authorization check on GET /Sales
    # Admin and Manager can access
    $adminSalesIndex = Send-HttpRequest -Uri "$BaseUrl/Sales" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Admin GET /Sales loads successfully with HTTP 200" ($adminSalesIndex.StatusCode -eq 200) "HTTP $($adminSalesIndex.StatusCode)"

    $mgrSalesIndex = Send-HttpRequest -Uri "$BaseUrl/Sales" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Test F" "Manager GET /Sales loads successfully with HTTP 200" ($mgrSalesIndex.StatusCode -eq 200) "HTTP $($mgrSalesIndex.StatusCode)"

    # Anonymous user is redirected to Login
    $anonSalesIndex = Send-HttpRequest -Uri "$BaseUrl/Sales" -Method "GET" -Session $anonSession
    $anonSalesBlocked = ($anonSalesIndex.StatusCode -eq 302 -and $anonSalesIndex.Location -like "*Account/Login*")
    Assert-Check "Test F" "Anonymous GET /Sales redirected to Login" $anonSalesBlocked "HTTP $($anonSalesIndex.StatusCode), Location: $($anonSalesIndex.Location)"

    # Customer user is blocked (AccessDenied / 403)
    $custSalesIndex = Send-HttpRequest -Uri "$BaseUrl/Sales" -Method "GET" -Session $custAuth.Session
    $custSalesBlocked = ($custSalesIndex.StatusCode -eq 403 -or ($custSalesIndex.StatusCode -eq 302 -and $custSalesIndex.Location -like "*AccessDenied*"))
    Assert-Check "Test F" "Customer GET /Sales blocked with AccessDenied/403" $custSalesBlocked "HTTP $($custSalesIndex.StatusCode), Location: $($custSalesIndex.Location)"

    # F.2: Setup Dedicated Test Fixture for Pagination (26 distinct Sales)
    $pagCustName = "HTTP_PAG_Cust_$testRunId"
    $pagCustId = [int](Invoke-SqlScalar @"
INSERT INTO Customers (Name, Phone, Email)
VALUES (@Name, '555-4444', 'pag_test@example.com');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $pagCustName })
    $createdCustomerIds.Add($pagCustId)

    # Insert 26 sales for this customer with distinct dates and tokens
    $baseDate = (Get-Date).AddDays(-30)
    for ($i = 1; $i -le 26; $i++) {
        $token = "PAG_TOKEN_${testRunId}_$i"
        $saleDate = $baseDate.AddHours($i)
        $amount = 100.00 + ($i * 5.00)
        $sId = [int](Invoke-SqlScalar @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (@CustId, @SaleDate, @Amount, 0.00, @Token);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@CustId" = $pagCustId; "@SaleDate" = $saleDate; "@Amount" = $amount; "@Token" = $token })
        $createdSaleIds.Add($sId)

        [void](Invoke-SqlNonQuery @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 1, @Amount, 50.00);
"@ @{ "@SaleId" = $sId; "@ProdId" = $testProdId; "@Amount" = $amount })
    }

    $totalSalesInDb = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales")
    Write-Host "  [DEBUG] Seeded 26 pagination sales. Total sales in DB: $totalSalesInDb" -ForegroundColor Magenta

    # F.3: Default Page Size is 25
    $defaultIndex = Send-HttpRequest -Uri "$BaseUrl/Sales" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Default Sales Index returns HTTP 200 OK" ($defaultIndex.StatusCode -eq 200) "HTTP $($defaultIndex.StatusCode)"
    Assert-Check "Test F" "Default Sales Index displays page size 25 in summary" ($defaultIndex.Content -like "*Showing <strong>1</strong> to <strong>25</strong> of*") "Default page size 25 verified"

    # F.4: Explicit Page Size 10
    $pageSize10Resp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "GET /Sales?pageSize=10 returns HTTP 200 OK" ($pageSize10Resp.StatusCode -eq 200) "HTTP $($pageSize10Resp.StatusCode)"
    Assert-Check "Test F" "GET /Sales?pageSize=10 displays Showing 1 to 10" ($pageSize10Resp.Content -like "*Showing <strong>1</strong> to <strong>10</strong> of*") "Page size 10 verified"

    # F.5: Explicit Page Size 50
    $pageSize50Resp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=50" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "GET /Sales?pageSize=50 returns HTTP 200 OK" ($pageSize50Resp.StatusCode -eq 200) "HTTP $($pageSize50Resp.StatusCode)"
    Assert-Check "Test F" "GET /Sales?pageSize=50 shows all records on page 1" ($pageSize50Resp.Content -like "*Showing <strong>1</strong> to <strong>$totalSalesInDb</strong> of <strong>$totalSalesInDb</strong>*") "Page size 50 displays all $totalSalesInDb records"

    # F.6: Unsupported Page Sizes Normalized to Default 25
    $badPageSizeResp1 = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=99" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Unsupported pageSize=99 normalized safely to 25" ($badPageSizeResp1.StatusCode -eq 200 -and $badPageSizeResp1.Content -like "*Showing <strong>1</strong> to <strong>25</strong> of*") "Normalized to 25"

    $badPageSizeResp2 = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=-5" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Negative pageSize=-5 normalized safely to 25" ($badPageSizeResp2.StatusCode -eq 200 -and $badPageSizeResp2.Content -like "*Showing <strong>1</strong> to <strong>25</strong> of*") "Normalized to 25"

    # F.7: Page 1 and Page 2 Return Distinct Records
    $p1Resp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&pageNumber=1&searchTerm=$pagCustName" -Method "GET" -Session $adminAuth.Session
    $p2Resp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&pageNumber=2&searchTerm=$pagCustName" -Method "GET" -Session $adminAuth.Session

    $p1Matches = [regex]::Matches($p1Resp.Content, '/Sales/Details/(\d+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $p2Matches = [regex]::Matches($p2Resp.Content, '/Sales/Details/(\d+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique

    $intersection = $p1Matches | Where-Object { $p2Matches -contains $_ }
    $hasDistinctPages = ($p1Matches.Count -eq 10 -and $p2Matches.Count -eq 10 -and (@($intersection).Count -eq 0))
    Assert-Check "Test F" "Page 1 and Page 2 return distinct non-overlapping records" $hasDistinctPages "Page 1: $($p1Matches.Count) items, Page 2: $($p2Matches.Count) items, Overlap: $(@($intersection).Count)"

    # F.8: Final Page Returns Correct Remaining Records
    $p3Resp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&pageNumber=3&searchTerm=$pagCustName" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Final page 3 returns HTTP 200 OK" ($p3Resp.StatusCode -eq 200) "HTTP $($p3Resp.StatusCode)"
    Assert-Check "Test F" "Final page displays Showing 21 to 26 of 26" ($p3Resp.Content -like "*Showing <strong>21</strong> to <strong>26</strong> of <strong>26</strong>*") "Remaining 6 items rendered"

    # F.9: Invalid Page Numbers Handled Safely
    $zeroPageResp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageNumber=0" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Invalid pageNumber=0 normalizes safely to page 1" ($zeroPageResp.StatusCode -eq 200 -and $zeroPageResp.Content -like "*Page <strong>1</strong> of*") "Normalized to page 1"

    $negPageResp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageNumber=-10" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Negative pageNumber=-10 normalizes safely to page 1" ($negPageResp.StatusCode -eq 200 -and $negPageResp.Content -like "*Page <strong>1</strong> of*") "Normalized to page 1"

    # F.10: Page Number Beyond Last Page Handled Safely
    $beyondLastPageResp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&pageNumber=9999&searchTerm=$pagCustName" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Page number beyond last page handled safely without error (HTTP 200)" ($beyondLastPageResp.StatusCode -eq 200) "HTTP $($beyondLastPageResp.StatusCode)"
    Assert-Check "Test F" "Request beyond last page clamped to final page 3" ($beyondLastPageResp.Content -like "*Page <strong>3</strong> of <strong>3</strong>*") "Clamped to Page 3"

    # F.11: Total Record Count and Total Page Count Corroboration
    $custFilterResp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&searchTerm=$pagCustName" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Total matching count matches 26 seeded customer sales" ($custFilterResp.Content -like "*of <strong>26</strong> transactions*") "Total items verified"
    Assert-Check "Test F" "Total pages matches 3 for 26 records with pageSize 10" ($custFilterResp.Content -like "*Page <strong>1</strong> of <strong>3</strong>*") "Total pages verified"

    # F.12: Filter and Sort Parameters Preserved in Pagination Links
    $sortAndFilterResp = Send-HttpRequest -Uri "$BaseUrl/Sales?pageSize=10&pageNumber=1&searchTerm=$pagCustName&sortOrder=amount_asc" -Method "GET" -Session $adminAuth.Session
    $hasPreservedLinks = ($sortAndFilterResp.Content -like "*searchTerm=$pagCustName*" -and 
                          $sortAndFilterResp.Content -like "*pageSize=10*" -and 
                          $sortAndFilterResp.Content -like "*sortOrder=amount_asc*")
    Assert-Check "Test F" "Pagination links preserve active searchTerm, pageSize, and sortOrder" $hasPreservedLinks "Preserved filter query parameters in links"

    # F.13: Empty Search Results Display Empty State Safely
    $emptySearchResp = Send-HttpRequest -Uri "$BaseUrl/Sales?searchTerm=NON_EXISTENT_SALE_TERM_99999" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Test F" "Empty search returns HTTP 200 OK" ($emptySearchResp.StatusCode -eq 200) "HTTP $($emptySearchResp.StatusCode)"
    Assert-Check "Test F" "Empty search displays 'No sales transactions found' empty state" ($emptySearchResp.Content -like "*No sales transactions found*") "Empty state rendered"
    Assert-Check "Test F" "Empty search displays '0 transactions found'" ($emptySearchResp.Content -like "*0 transactions found*") "Zero count summary rendered"
}
finally {
    # =========================================================================
    # PHASE 3: GUARANTEED TEST ISOLATION & CLEANUP
    # =========================================================================
    Write-Host ""
    Write-Host "[CLEANUP] Executing teardown of test-created data..." -ForegroundColor Gray

    try {
        # 1. Clean up created sales and cascade sale items
        foreach ($sid in $createdSaleIds) {
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId = @Id" @{ "@Id" = $sid }
            Invoke-SqlNonQuery "DELETE FROM Sales WHERE SaleId = @Id" @{ "@Id" = $sid }
        }

        # 2. Clean up test customers
        foreach ($cid in $createdCustomerIds) {
            Invoke-SqlNonQuery "DELETE FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $cid }
        }
        Invoke-SqlNonQuery "DELETE FROM Customers WHERE Name LIKE 'HTTP_TEST_%' OR Name LIKE 'HTTP_PAG_%'"

        # 3. Clean up test products and related stock transactions
        foreach ($prodIdToClean in $createdProductIds) {
            Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId = @Id" @{ "@Id" = $prodIdToClean }
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId = @Id" @{ "@Id" = $prodIdToClean }
            Invoke-SqlNonQuery "DELETE FROM Products WHERE ProductId = @Id" @{ "@Id" = $prodIdToClean }
        }

        # Wildcard cleanup for any leftover test products/transactions/sales
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId IN (SELECT SaleId FROM Sales WHERE CheckoutToken LIKE 'PAG_TOKEN_%')"
        Invoke-SqlNonQuery "DELETE FROM Sales WHERE CheckoutToken LIKE 'PAG_TOKEN_%'"
        Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM Products WHERE Name LIKE 'HTTP_TEST_%'"

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
Write-Host "SALES HTTP INTEGRATION TEST SUITE SUMMARY" -ForegroundColor Cyan
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
