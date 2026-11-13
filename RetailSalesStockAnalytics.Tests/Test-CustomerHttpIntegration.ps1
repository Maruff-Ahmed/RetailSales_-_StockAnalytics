<#
.SYNOPSIS
    Repeatable HTTP Integration Test Suite for Customer Workflows.
.DESCRIPTION
    Validates ASP.NET Core MVC Customer endpoints:
      - /Customers (GET - Index Directory)
      - /Customers/Details/{id} (GET)
      - /Customers/Create (GET & POST)
      - /Customers/Edit/{id} (GET & POST)
      - /Customers/Delete/{id} (GET & POST)
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
    Write-Host "[INFO] Customer HTTP integration tests skipped (opt-in switch -Functional was not supplied)." -ForegroundColor Yellow
    Write-Host "================================================="
    Write-Host "CUSTOMER HTTP RESULTS: SKIPPED (Use -Functional to execute)"
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
Write-Host "STARTING CUSTOMER HTTP INTEGRATION TEST SUITE" -ForegroundColor Cyan
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
$createdCustomerIds = New-Object System.Collections.Generic.List[int]
$createdProductIds = New-Object System.Collections.Generic.List[int]
$createdSaleIds = New-Object System.Collections.Generic.List[int]
$createdComplaintIds = New-Object System.Collections.Generic.List[int]
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

    Assert-Check "Auth" "Admin login succeeded" $adminAuth.IsAuthenticated "HTTP $($adminAuth.StatusCode)"
    Assert-Check "Auth" "Manager login succeeded" $mgrAuth.IsAuthenticated "HTTP $($mgrAuth.StatusCode)"
    Assert-Check "Auth" "Customer login succeeded" $custAuth.IsAuthenticated "HTTP $($custAuth.StatusCode)"

    # =========================================================================
    # SECTION 1: CUSTOMER HTTP WORKFLOWS (CRUD & DIRECTORY)
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 1: Customer Creation, Editing, Details & Deletion ---" -ForegroundColor Yellow

    # --- 1.1: GET /Customers/Create via Manager ---
    $custCreateGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Customer Create" "Manager GET /Customers/Create returns 200 OK" ($custCreateGet.StatusCode -eq 200) "HTTP $($custCreateGet.StatusCode)"

    $afTokenCustCreate = Extract-AntiForgeryToken $custCreateGet.Content
    Assert-Check "Customer Create" "Extracted anti-forgery token from /Customers/Create form" (-not [string]::IsNullOrWhiteSpace($afTokenCustCreate)) "Token length: $($afTokenCustCreate.Length)"

    # --- 1.2: POST /Customers/Create (Valid Customer) ---
    $custNameA = "HTTP_CUST_TEST_A_$testRunId"
    $custPhoneA = "01711-000111"
    $custEmailA = "http_cust_a_$testRunId@example.com"

    $custPostBodyA = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCustCreate))" +
        "&Name=$([System.Uri]::EscapeDataString($custNameA))" +
        "&Phone=$([System.Uri]::EscapeDataString($custPhoneA))" +
        "&Email=$([System.Uri]::EscapeDataString($custEmailA))"

    $custCreateResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $custPostBodyA -Session $mgrAuth.Session
    Assert-Check "Customer Create" "POST /Customers/Create redirects (HTTP 302) on success" ($custCreateResp.StatusCode -eq 302) "HTTP $($custCreateResp.StatusCode), Location: $($custCreateResp.Location)"

    # SQL Verification: Customer persisted
    $custIdA = [int](Invoke-SqlScalar "SELECT CustomerId FROM Customers WHERE Name = @Name" @{ "@Name" = $custNameA })
    Assert-Check "Customer Create" "Customer persisted in SQL Server" ($custIdA -gt 0) "CustomerId: $custIdA"
    if ($custIdA -gt 0) { $createdCustomerIds.Add($custIdA) }

    $actualPhoneA = [string](Invoke-SqlScalar "SELECT Phone FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    $actualEmailA = [string](Invoke-SqlScalar "SELECT Email FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    Assert-Check "Customer Create" "Customer contact information persisted accurately" ($actualPhoneA -eq $custPhoneA -and $actualEmailA -eq $custEmailA) "Phone: $actualPhoneA, Email: $actualEmailA"

    # --- 1.3: GET /Customers/Edit/{id} via Manager ---
    $custEditGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Customer Edit" "Manager GET /Customers/Edit/{id} returns 200 OK" ($custEditGet.StatusCode -eq 200) "HTTP $($custEditGet.StatusCode)"

    $afTokenCustEdit = Extract-AntiForgeryToken $custEditGet.Content
    Assert-Check "Customer Edit" "Extracted anti-forgery token from /Customers/Edit form" (-not [string]::IsNullOrWhiteSpace($afTokenCustEdit)) "Token extracted"

    # --- 1.4: POST /Customers/Edit/{id} (Update Customer Info) ---
    $updatedNameA = "HTTP_CUST_TEST_A_Upd_$testRunId"
    $updatedPhoneA = "01822-222333"
    $updatedEmailA = "http_cust_a_upd_$testRunId@example.com"

    $custEditBodyA = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCustEdit))" +
        "&CustomerId=$custIdA" +
        "&Name=$([System.Uri]::EscapeDataString($updatedNameA))" +
        "&Phone=$([System.Uri]::EscapeDataString($updatedPhoneA))" +
        "&Email=$([System.Uri]::EscapeDataString($updatedEmailA))"

    $custEditResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "POST" -Body $custEditBodyA -Session $mgrAuth.Session
    Assert-Check "Customer Edit" "POST /Customers/Edit/{id} redirects (HTTP 302) on success" ($custEditResp.StatusCode -eq 302) "HTTP $($custEditResp.StatusCode), Location: $($custEditResp.Location)"

    # SQL Verification: Updated values
    $dbNameAfterEdit = [string](Invoke-SqlScalar "SELECT Name FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    $dbPhoneAfterEdit = [string](Invoke-SqlScalar "SELECT Phone FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    $dbEmailAfterEdit = [string](Invoke-SqlScalar "SELECT Email FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    Assert-Check "Customer Edit" "Customer properties accurately updated in SQL Server" ($dbNameAfterEdit -eq $updatedNameA -and $dbPhoneAfterEdit -eq $updatedPhoneA -and $dbEmailAfterEdit -eq $updatedEmailA) "Name: $dbNameAfterEdit, Phone: $dbPhoneAfterEdit, Email: $dbEmailAfterEdit"

    # --- 1.5: GET /Customers/Details/{id} ---
    $mgrDetailsGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Details/$custIdA" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Customer Details" "Manager GET /Customers/Details/{id} returns 200 OK" ($mgrDetailsGet.StatusCode -eq 200) "HTTP $($mgrDetailsGet.StatusCode)"
    Assert-Check "Customer Details" "Details HTML displays updated customer attributes" ($mgrDetailsGet.Content -like "*$updatedNameA*" -and $mgrDetailsGet.Content -like "*$updatedPhoneA*") "HTML attributes verified"

    $adminDetailsGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Details/$custIdA" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Customer Details" "Admin GET /Customers/Details/{id} returns 200 OK" ($adminDetailsGet.StatusCode -eq 200) "HTTP $($adminDetailsGet.StatusCode)"

    # --- 1.6: Input Validation Failures ---
    # Missing required name
    $badCustBody1 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCustCreate))&Name=&Phone=555-1234&Email=valid@example.com"
    $badCustResp1 = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $badCustBody1 -Session $mgrAuth.Session
    Assert-Check "Customer Validation" "Missing name rejected with HTTP 200 form re-render" ($badCustResp1.StatusCode -eq 200 -and [string]::IsNullOrEmpty($badCustResp1.Location)) "HTTP $($badCustResp1.StatusCode)"
    Assert-Check "Customer Validation" "Response contains required Name validation message" ($badCustResp1.Content -like "*The Name field is required*") "Validation text present"

    $emptyNameCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE Name = '' OR Name IS NULL")
    Assert-Check "Customer Validation" "No customer with empty name was persisted" ($emptyNameCount -eq 0) "Empty name count: $emptyNameCount"

    # Invalid email format
    $badCustBody2 = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCustCreate))&Name=BadEmailCust&Phone=555-1234&Email=invalid-email-format"
    $badCustResp2 = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $badCustBody2 -Session $mgrAuth.Session
    Assert-Check "Customer Validation" "Invalid email format rejected with HTTP 200 form re-render" ($badCustResp2.StatusCode -eq 200) "HTTP $($badCustResp2.StatusCode)"
    Assert-Check "Customer Validation" "Response contains Email validation error message" ($badCustResp2.Content -like "*The Email field is not a valid e-mail address*") "Validation text present"

    $badEmailCount = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE Email = 'invalid-email-format'")
    Assert-Check "Customer Validation" "No customer with invalid email was persisted" ($badEmailCount -eq 0) "Invalid email count: $badEmailCount"

    # --- 1.7: Customer Deletion (Standard Customer Without Sales) ---
    $delCustName = "HTTP_CUST_TEST_Del_$testRunId"
    $delCustInsertSql = @"
INSERT INTO Customers (Name, Phone, Email)
VALUES (@Name, '555-9999', 'del_test@example.com');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@
    $delCustId = [int](Invoke-SqlScalar $delCustInsertSql @{ "@Name" = $delCustName })
    $createdCustomerIds.Add($delCustId)

    $custDelGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$delCustId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Customer Delete" "Admin GET /Customers/Delete/{id} returns 200 OK" ($custDelGet.StatusCode -eq 200) "HTTP $($custDelGet.StatusCode)"

    $afTokenCustDel = Extract-AntiForgeryToken $custDelGet.Content
    Assert-Check "Customer Delete" "Extracted anti-forgery token from /Customers/Delete form" (-not [string]::IsNullOrWhiteSpace($afTokenCustDel)) "Token extracted"

    $custDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCustDel))&CustomerId=$delCustId"
    $custDelResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$delCustId" -Method "POST" -Body $custDelBody -Session $adminAuth.Session
    Assert-Check "Customer Delete" "POST /Customers/Delete/{id} succeeds with redirect (HTTP 302)" ($custDelResp.StatusCode -eq 302) "HTTP $($custDelResp.StatusCode), Location: $($custDelResp.Location)"

    $delCustExists = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $delCustId })
    Assert-Check "Customer Delete" "Customer record cleanly removed from SQL Server" ($delCustExists -eq 0) "Customer count: $delCustExists"

    # --- 1.8: Customer Deletion with Existing Sales (Referential Integrity Check) ---
    # Create a dedicated customer with a sale to test referential integrity behavior
    $cascadeCustName = "HTTP_CUST_TEST_WithSale_$testRunId"
    $cascadeCustId = [int](Invoke-SqlScalar @"
INSERT INTO Customers (Name, Phone, Email)
VALUES (@Name, '555-7777', 'cascade_test@example.com');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $cascadeCustName })
    $createdCustomerIds.Add($cascadeCustId)

    # Insert a test product and test sale for this customer
    $cascadeProdId = [int](Invoke-SqlScalar @"
INSERT INTO Products (Name, CategoryId, PurchasePrice, SellingPrice, StockQuantity, ReorderLevel)
VALUES ('HTTP_CUST_TEST_CascadeProd_$testRunId', 1, 10.00, 25.00, 50, 5);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@)
    $createdProductIds.Add($cascadeProdId)

    $cascadeSaleId = [int](Invoke-SqlScalar @"
INSERT INTO Sales (CustomerId, SaleDate, TotalAmount, Discount, CheckoutToken)
VALUES (@CustId, GETDATE(), 50.00, 0.00, 'CASCADE_TEST_TOKEN_$testRunId');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@CustId" = $cascadeCustId })
    $createdSaleIds.Add($cascadeSaleId)

    [void](Invoke-SqlNonQuery @"
INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice, UnitCost)
VALUES (@SaleId, @ProdId, 2, 25.00, 10.00);
"@ @{ "@SaleId" = $cascadeSaleId; "@ProdId" = $cascadeProdId })

    $saleExistsBefore = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @SaleId" @{ "@SaleId" = $cascadeSaleId })
    Assert-Check "Referential Integrity" "Setup: Linked sale created for customer deletion test" ($saleExistsBefore -eq 1) "SaleId: $cascadeSaleId"

    $saleItemExistsBefore = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM SaleItems WHERE SaleId = @SaleId" @{ "@SaleId" = $cascadeSaleId })
    Assert-Check "Referential Integrity" "Setup: Associated SaleItem created for customer deletion test" ($saleItemExistsBefore -eq 1) "SaleId: $cascadeSaleId, SaleItems: $saleItemExistsBefore"

    # Capture unrelated sales count before cascade delete
    $unrelatedSalesBefore = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId <> @SaleId" @{ "@SaleId" = $cascadeSaleId })

    # Attempt to delete customer with sales via Admin POST
    $cascadeDelGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$cascadeCustId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "GET /Customers/Delete on customer with sales returns 200 OK" ($cascadeDelGet.StatusCode -eq 200) "HTTP $($cascadeDelGet.StatusCode)"
    Assert-Check "Historical Preservation" "GET /Customers/Delete displays deletion blocked alert" ($cascadeDelGet.Content -like "*Deletion Blocked*" -or $cascadeDelGet.Content -like "*historical sales*") "Warning present on delete view"

    $afTokenCascadeDel = Extract-AntiForgeryToken $cascadeDelGet.Content

    $cascadeDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenCascadeDel))&CustomerId=$cascadeCustId"
    $cascadeDelResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$cascadeCustId" -Method "POST" -Body $cascadeDelBody -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "POST /Customers/Delete on customer with sales redirects to Index (HTTP 302)" ($cascadeDelResp.StatusCode -eq 302) "HTTP $($cascadeDelResp.StatusCode)"

    # Verify user-friendly error message on redirect to /Customers
    $custIndexAfterDel = Send-HttpRequest -Uri "$BaseUrl/Customers" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "Redirected index displays user-friendly historical preservation error message" ($custIndexAfterDel.Content -like "*Cannot delete customer*") "Error message rendered on Index"

    # SQL Verification: Customer and historical sales records remain intact
    $cascadeCustExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $cascadeCustId })
    Assert-Check "Historical Preservation" "Customer record with historical sales remains in database" ($cascadeCustExistsAfter -eq 1) "Customer count: $cascadeCustExistsAfter (preserved)"

    $cascadeSaleExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId = @SaleId" @{ "@SaleId" = $cascadeSaleId })
    Assert-Check "Historical Preservation" "Associated historical sales records remain completely intact in database" ($cascadeSaleExistsAfter -eq 1) "Sales remaining: $cascadeSaleExistsAfter (preserved)"

    $cascadeSaleItemsExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM SaleItems WHERE SaleId = @SaleId" @{ "@SaleId" = $cascadeSaleId })
    Assert-Check "Historical Preservation" "Associated historical SaleItems records remain completely intact in database" ($cascadeSaleItemsExistsAfter -eq 1) "SaleItems remaining: $cascadeSaleItemsExistsAfter (preserved)"

    $unrelatedSalesAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Sales WHERE SaleId <> @SaleId" @{ "@SaleId" = $cascadeSaleId })
    Assert-Check "Historical Preservation" "Unrelated baseline sales records completely preserved after blocked customer delete" ($unrelatedSalesAfter -eq $unrelatedSalesBefore) "Before: $unrelatedSalesBefore, After: $unrelatedSalesAfter"

    $baselineCustomer1Exists = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = 1")
    Assert-Check "Historical Preservation" "Unrelated baseline customer #1 remains completely intact" ($baselineCustomer1Exists -eq 1) "Baseline customer count: $baselineCustomer1Exists"

    # --- 1.9: Customer Deletion with Existing Complaints (Referential Integrity Check) ---
    $complaintCustName = "HTTP_CUST_TEST_WithComplaint_$testRunId"
    $complaintCustId = [int](Invoke-SqlScalar @"
INSERT INTO Customers (Name, Phone, Email)
VALUES (@Name, '555-8888', 'complaint_test@example.com');
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $complaintCustName })
    $createdCustomerIds.Add($complaintCustId)

    $complaintCode = "CMP_$testRunId"
    if ($complaintCode.Length -gt 20) {
        $complaintCode = $complaintCode.Substring(0, 20)
    }

    $complaintId = [int](Invoke-SqlScalar @"
INSERT INTO Complaints (ComplaintCode, CustomerId, ComplaintText, SimilarityScore, RoutingStatus, CreatedAt)
VALUES (@Code, @CustId, 'Customer complaint text for historical preservation test', 0.90, 'Pending', GETDATE());
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Code" = $complaintCode; "@CustId" = $complaintCustId })

    $complaintExistsBefore = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Complaints WHERE ComplaintId = @ComplaintId" @{ "@ComplaintId" = $complaintId })
    Assert-Check "Referential Integrity" "Setup: Linked complaint created for customer deletion test" ($complaintExistsBefore -eq 1) "ComplaintId: $complaintId"

    $unrelatedComplaintsBefore = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Complaints WHERE ComplaintId <> @ComplaintId" @{ "@ComplaintId" = $complaintId })

    # Attempt to delete customer with complaints via Admin GET & POST
    $complaintDelGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$complaintCustId" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "GET /Customers/Delete on customer with complaints returns 200 OK" ($complaintDelGet.StatusCode -eq 200) "HTTP $($complaintDelGet.StatusCode)"
    Assert-Check "Historical Preservation" "GET /Customers/Delete displays deletion blocked alert for complaints" ($complaintDelGet.Content -like "*Deletion Blocked*" -and $complaintDelGet.Content -like "*complaint record*") "Warning present on delete view"

    $afTokenComplaintDel = Extract-AntiForgeryToken $complaintDelGet.Content

    $complaintDelBody = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenComplaintDel))&CustomerId=$complaintCustId"
    $complaintDelResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$complaintCustId" -Method "POST" -Body $complaintDelBody -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "POST /Customers/Delete on customer with complaints redirects to Index (HTTP 302)" ($complaintDelResp.StatusCode -eq 302) "HTTP $($complaintDelResp.StatusCode)"

    # Verify user-friendly error message on redirect to /Customers
    $custIndexAfterComplaintDel = Send-HttpRequest -Uri "$BaseUrl/Customers" -Method "GET" -Session $adminAuth.Session
    Assert-Check "Historical Preservation" "Redirected index displays complaint preservation error message" ($custIndexAfterComplaintDel.Content -like "*Cannot delete customer*" -and $custIndexAfterComplaintDel.Content -like "*complaint record*") "Error message rendered on Index"

    # SQL Verification: Customer and complaint records remain intact
    $complaintCustExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $complaintCustId })
    Assert-Check "Historical Preservation" "Customer record with historical complaints remains in database" ($complaintCustExistsAfter -eq 1) "Customer count: $complaintCustExistsAfter (preserved)"

    $complaintExistsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Complaints WHERE ComplaintId = @ComplaintId" @{ "@ComplaintId" = $complaintId })
    Assert-Check "Historical Preservation" "Associated complaint record remains completely intact in database" ($complaintExistsAfter -eq 1) "Complaints remaining: $complaintExistsAfter (preserved)"

    $complaintCustIdFk = [int](Invoke-SqlScalar "SELECT CustomerId FROM Complaints WHERE ComplaintId = @ComplaintId" @{ "@ComplaintId" = $complaintId })
    Assert-Check "Historical Preservation" "Associated complaint CustomerId foreign key still references customer" ($complaintCustIdFk -eq $complaintCustId) "Complaint CustomerId: $complaintCustIdFk"

    $unrelatedComplaintsAfter = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Complaints WHERE ComplaintId <> @ComplaintId" @{ "@ComplaintId" = $complaintId })
    Assert-Check "Historical Preservation" "Unrelated baseline complaint records completely preserved after blocked customer delete" ($unrelatedComplaintsAfter -eq $unrelatedComplaintsBefore) "Before: $unrelatedComplaintsBefore, After: $unrelatedComplaintsAfter"

    # =========================================================================
    # SECTION 1.3: CONCURRENCY-SAFE COMPLAINT CODE GENERATION VIA HTTP
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 1.3: Concurrency-Safe Complaint Code Generation via HTTP ---" -ForegroundColor Yellow

    # Manager GET /ComplaintRouting/Create
    $complaintCreateGet = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Complaint Code" "Manager GET /ComplaintRouting/Create returns 200 OK" ($complaintCreateGet.StatusCode -eq 200) "HTTP $($complaintCreateGet.StatusCode)"

    $afTokenComplaintCreate = Extract-AntiForgeryToken $complaintCreateGet.Content
    Assert-Check "Complaint Code" "Extracted anti-forgery token from /ComplaintRouting/Create form" (![string]::IsNullOrWhiteSpace($afTokenComplaintCreate)) "Token present"

    # POST first complaint
    $comp1Text = "HTTP Test Complaint One - $($testRunId)"
    $comp1Body = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenComplaintCreate))&CustomerId=$custIdA&ProductId=$cascadeProdId&ComplaintText=$([System.Uri]::EscapeDataString($comp1Text))"
    $comp1Resp = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "POST" -Body $comp1Body -Session $mgrAuth.Session
    Assert-Check "Complaint Code" "POST /ComplaintRouting/Create succeeds with redirect to Result (HTTP 302)" ($comp1Resp.StatusCode -eq 302 -and $comp1Resp.Location -like "*/ComplaintRouting/Result/*") "HTTP $($comp1Resp.StatusCode), Loc: $($comp1Resp.Location)"

    $comp1Id = 0
    if ($comp1Resp.Location -match "/ComplaintRouting/Result/(\d+)") {
        $comp1Id = [int]$matches[1]
        $createdComplaintIds.Add($comp1Id)
    }

    $comp1Code = [string](Invoke-SqlScalar "SELECT ComplaintCode FROM Complaints WHERE ComplaintId = @Id" @{ "@Id" = $comp1Id })
    Assert-Check "Complaint Code" "First generated complaint code matches format 'CMP-XXX'" ($comp1Code -match "^CMP-\d+$") "ComplaintCode: $comp1Code"

    # POST second complaint to verify sequential, non-colliding generation
    $comp2Get = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "GET" -Session $mgrAuth.Session
    $afTokenComp2 = Extract-AntiForgeryToken $comp2Get.Content

    $comp2Text = "HTTP Test Complaint Two - $($testRunId)"
    $comp2Body = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($afTokenComp2))&CustomerId=$custIdA&ProductId=$cascadeProdId&ComplaintText=$([System.Uri]::EscapeDataString($comp2Text))"
    $comp2Resp = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "POST" -Body $comp2Body -Session $mgrAuth.Session
    Assert-Check "Complaint Code" "Second POST /ComplaintRouting/Create succeeds with redirect (HTTP 302)" ($comp2Resp.StatusCode -eq 302 -and $comp2Resp.Location -like "*/ComplaintRouting/Result/*") "HTTP $($comp2Resp.StatusCode)"

    $comp2Id = 0
    if ($comp2Resp.Location -match "/ComplaintRouting/Result/(\d+)") {
        $comp2Id = [int]$matches[1]
        $createdComplaintIds.Add($comp2Id)
    }

    $comp2Code = [string](Invoke-SqlScalar "SELECT ComplaintCode FROM Complaints WHERE ComplaintId = @Id" @{ "@Id" = $comp2Id })
    Assert-Check "Complaint Code" "Second generated complaint code matches format 'CMP-XXX'" ($comp2Code -match "^CMP-\d+$") "ComplaintCode: $comp2Code"
    Assert-Check "Complaint Code" "Sequential complaint codes are distinct and non-colliding" ($comp1Code -ne $comp2Code) "$comp1Code != $comp2Code"

    $num1 = [int]($comp1Code -replace "CMP-", "")
    $num2 = [int]($comp2Code -replace "CMP-", "")
    Assert-Check "Complaint Code" "Complaint code sequence numbers are monotonically increasing" ($num2 -gt $num1) "$num1 < $num2"

    # Security: Anonymous GET /ComplaintRouting/Create blocked
    $anonCompGet = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /ComplaintRouting/Create redirected to Login" ($anonCompGet.StatusCode -eq 302 -and $anonCompGet.Location -like "*Account/Login*") "HTTP $($anonCompGet.StatusCode)"

    # Security: Customer role GET /ComplaintRouting/Create blocked
    $custRoleCompGet = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "GET" -Session $custAuth.Session
    Assert-Check "Security" "Customer role GET /ComplaintRouting/Create blocked (AccessDenied)" ($custRoleCompGet.StatusCode -eq 302 -and $custRoleCompGet.Location -like "*Account/AccessDenied*") "HTTP $($custRoleCompGet.StatusCode)"

    # Security: POST /ComplaintRouting/Create without anti-forgery token rejected with HTTP 400
    $noAfCompBody = "CustomerId=$custIdA&ProductId=$cascadeProdId&ComplaintText=NoAfComplaint"
    $noAfCompResp = Send-HttpRequest -Uri "$BaseUrl/ComplaintRouting/Create" -Method "POST" -Body $noAfCompBody -Session $mgrAuth.Session
    Assert-Check "Security" "POST /ComplaintRouting/Create without anti-forgery token rejected with HTTP 400" ($noAfCompResp.StatusCode -eq 400) "HTTP $($noAfCompResp.StatusCode)"

    # =========================================================================
    # SECTION 2: AUTHORIZATION & ANTI-FORGERY SECURITY ENFORCEMENT
    # =========================================================================
    Write-Host ""
    Write-Host "--- SECTION 2: Authorization and Anti-Forgery Security Enforcement ---" -ForegroundColor Yellow

    $anonSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    # --- 2.1: Anonymous User Blocked on All Customer Endpoints ---
    $anonGetIndex = Send-HttpRequest -Uri "$BaseUrl/Customers" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /Customers redirected to Login" ($anonGetIndex.StatusCode -eq 302 -and $anonGetIndex.Location -like "*Account/Login*") "HTTP $($anonGetIndex.StatusCode), Location: $($anonGetIndex.Location)"

    $anonGetCreate = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /Customers/Create redirected to Login" ($anonGetCreate.StatusCode -eq 302 -and $anonGetCreate.Location -like "*Account/Login*") "HTTP $($anonGetCreate.StatusCode)"

    $anonPostCreate = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $custPostBodyA -Session $anonSession
    Assert-Check "Security" "Anonymous POST /Customers/Create redirected to Login" ($anonPostCreate.StatusCode -eq 302 -and $anonPostCreate.Location -like "*Account/Login*") "HTTP $($anonPostCreate.StatusCode)"

    $anonGetEdit = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /Customers/Edit/{id} redirected to Login" ($anonGetEdit.StatusCode -eq 302 -and $anonGetEdit.Location -like "*Account/Login*") "HTTP $($anonGetEdit.StatusCode)"

    $anonPostEdit = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "POST" -Body $custEditBodyA -Session $anonSession
    Assert-Check "Security" "Anonymous POST /Customers/Edit/{id} redirected to Login" ($anonPostEdit.StatusCode -eq 302 -and $anonPostEdit.Location -like "*Account/Login*") "HTTP $($anonPostEdit.StatusCode)"

    $anonGetDel = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /Customers/Delete/{id} redirected to Login" ($anonGetDel.StatusCode -eq 302 -and $anonGetDel.Location -like "*Account/Login*") "HTTP $($anonGetDel.StatusCode)"

    $anonPostDel = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "POST" -Body "CustomerId=$custIdA" -Session $anonSession
    Assert-Check "Security" "Anonymous POST /Customers/Delete/{id} redirected to Login" ($anonPostDel.StatusCode -eq 302 -and $anonPostDel.Location -like "*Account/Login*") "HTTP $($anonPostDel.StatusCode)"

    $anonGetDetails = Send-HttpRequest -Uri "$BaseUrl/Customers/Details/$custIdA" -Method "GET" -Session $anonSession
    Assert-Check "Security" "Anonymous GET /Customers/Details/{id} redirected to Login" ($anonGetDetails.StatusCode -eq 302 -and $anonGetDetails.Location -like "*Account/Login*") "HTTP $($anonGetDetails.StatusCode)"

    # --- 2.2: Customer Role Access Restrictions ---
    # Customer is blocked from general management directory
    $custGetIndex = Send-HttpRequest -Uri "$BaseUrl/Customers" -Method "GET" -Session $custAuth.Session
    $custIndexBlocked = ($custGetIndex.StatusCode -eq 403 -or ($custGetIndex.StatusCode -eq 302 -and $custGetIndex.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role GET /Customers blocked (AccessDenied/403)" $custIndexBlocked "HTTP $($custGetIndex.StatusCode), Location: $($custGetIndex.Location)"

    # Customer is blocked from creating customers
    $custGetCreate = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "GET" -Session $custAuth.Session
    $custCreateGetBlocked = ($custGetCreate.StatusCode -eq 403 -or ($custGetCreate.StatusCode -eq 302 -and $custGetCreate.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role GET /Customers/Create blocked (AccessDenied/403)" $custCreateGetBlocked "HTTP $($custGetCreate.StatusCode)"

    $custPostCreate = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $custPostBodyA -Session $custAuth.Session
    $custCreatePostBlocked = ($custPostCreate.StatusCode -eq 403 -or ($custPostCreate.StatusCode -eq 302 -and $custPostCreate.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Customers/Create blocked (AccessDenied/403)" $custCreatePostBlocked "HTTP $($custPostCreate.StatusCode)"

    # Customer is blocked from editing customers
    $custGetEdit = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "GET" -Session $custAuth.Session
    $custEditGetBlocked = ($custGetEdit.StatusCode -eq 403 -or ($custGetEdit.StatusCode -eq 302 -and $custGetEdit.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role GET /Customers/Edit/{id} blocked (AccessDenied/403)" $custEditGetBlocked "HTTP $($custGetEdit.StatusCode)"

    $custPostEdit = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "POST" -Body $custEditBodyA -Session $custAuth.Session
    $custEditPostBlocked = ($custPostEdit.StatusCode -eq 403 -or ($custPostEdit.StatusCode -eq 302 -and $custPostEdit.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Customers/Edit/{id} blocked (AccessDenied/403)" $custEditPostBlocked "HTTP $($custPostEdit.StatusCode)"

    # Customer is blocked from deleting customers
    $custGetDel = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "GET" -Session $custAuth.Session
    $custDelGetBlocked = ($custGetDel.StatusCode -eq 403 -or ($custGetDel.StatusCode -eq 302 -and $custGetDel.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role GET /Customers/Delete/{id} blocked (AccessDenied/403)" $custDelGetBlocked "HTTP $($custGetDel.StatusCode)"

    $custPostDel = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "POST" -Body "CustomerId=$custIdA" -Session $custAuth.Session
    $custDelPostBlocked = ($custPostDel.StatusCode -eq 403 -or ($custPostDel.StatusCode -eq 302 -and $custPostDel.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Customer role POST /Customers/Delete/{id} blocked (AccessDenied/403)" $custDelPostBlocked "HTTP $($custPostDel.StatusCode)"

    # --- 2.3: Customer Resource Ownership Verification on Details ---
    # Customer attempting to access ANOTHER customer's details (e.g. $custIdA) must be redirected to AccessDenied
    $custOtherDetails = Send-HttpRequest -Uri "$BaseUrl/Customers/Details/$custIdA" -Method "GET" -Session $custAuth.Session
    $custOtherBlocked = ($custOtherDetails.StatusCode -eq 302 -and $custOtherDetails.Location -like "*AccessDenied*")
    Assert-Check "Security" "Customer role requesting another customer's Details blocked (AccessDenied)" $custOtherBlocked "HTTP $($custOtherDetails.StatusCode), Location: $($custOtherDetails.Location)"

    # Seed a customer record belonging to the logged-in Customer user
    $ownCustEmail = "testcustomer.functional@example.com"
    $ownCustName = "HTTP_CUST_TEST_Own_$testRunId"
    $ownCustId = [int](Invoke-SqlScalar @"
INSERT INTO Customers (Name, Phone, Email)
VALUES (@Name, '555-4444', @Email);
SELECT CAST(SCOPE_IDENTITY() AS INT);
"@ @{ "@Name" = $ownCustName; "@Email" = $ownCustEmail })
    $createdCustomerIds.Add($ownCustId)

    # Customer requesting THEIR OWN details must succeed with HTTP 200
    $custOwnDetails = Send-HttpRequest -Uri "$BaseUrl/Customers/Details/$ownCustId" -Method "GET" -Session $custAuth.Session
    Assert-Check "Security" "Customer role requesting their OWN customer Details succeeds (HTTP 200)" ($custOwnDetails.StatusCode -eq 200) "HTTP $($custOwnDetails.StatusCode)"
    Assert-Check "Security" "Own details page correctly renders customer's profile info" ($custOwnDetails.Content -like "*$ownCustName*" -and $custOwnDetails.Content -like "*$ownCustEmail*") "Profile info verified"

    # --- 2.4: Manager Role Permissions and Admin-Only Delete Enforcement ---
    # Manager can access Index directory
    $mgrIndexGet = Send-HttpRequest -Uri "$BaseUrl/Customers" -Method "GET" -Session $mgrAuth.Session
    Assert-Check "Security" "Manager role GET /Customers succeeds (HTTP 200)" ($mgrIndexGet.StatusCode -eq 200) "HTTP $($mgrIndexGet.StatusCode)"

    # Manager is blocked from Delete (Admin-only policy)
    $mgrDelGet = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "GET" -Session $mgrAuth.Session
    $mgrDelGetBlocked = ($mgrDelGet.StatusCode -eq 403 -or ($mgrDelGet.StatusCode -eq 302 -and $mgrDelGet.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role GET /Customers/Delete/{id} blocked (Admin-only policy)" $mgrDelGetBlocked "HTTP $($mgrDelGet.StatusCode)"

    $mgrDelPost = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "POST" -Body "CustomerId=$custIdA" -Session $mgrAuth.Session
    $mgrDelPostBlocked = ($mgrDelPost.StatusCode -eq 403 -or ($mgrDelPost.StatusCode -eq 302 -and $mgrDelPost.Location -like "*AccessDenied*"))
    Assert-Check "Security" "Manager role POST /Customers/Delete/{id} blocked (Admin-only policy)" $mgrDelPostBlocked "HTTP $($mgrDelPost.StatusCode)"

    $custAStillExists = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    Assert-Check "Security" "Customer record remains intact after unauthorized delete attempts" ($custAStillExists -eq 1) "Customer count: $custAStillExists"

    # --- 2.5: Anti-Forgery Token Enforcement ---
    # POST /Customers/Create without token -> 400
    $noAfCreateBody = "Name=NoAfCust&Phone=555-0000&Email=noaf@example.com"
    $noAfCreateResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Create" -Method "POST" -Body $noAfCreateBody -Session $mgrAuth.Session
    Assert-Check "Security" "POST /Customers/Create without anti-forgery token rejected with HTTP 400" ($noAfCreateResp.StatusCode -eq 400) "HTTP $($noAfCreateResp.StatusCode)"

    # POST /Customers/Edit/{id} without token -> 400
    $noAfEditBody = "CustomerId=$custIdA&Name=NoAfEdit&Phone=555-0000&Email=noafedit@example.com"
    $noAfEditResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Edit/$custIdA" -Method "POST" -Body $noAfEditBody -Session $mgrAuth.Session
    Assert-Check "Security" "POST /Customers/Edit/{id} without anti-forgery token rejected with HTTP 400" ($noAfEditResp.StatusCode -eq 400) "HTTP $($noAfEditResp.StatusCode)"

    # POST /Customers/Delete/{id} with tampered token -> 400
    $tamperedDelBody = "__RequestVerificationToken=TAMPERED_TOKEN_ABCD1234XYZ&CustomerId=$custIdA"
    $tamperedDelResp = Send-HttpRequest -Uri "$BaseUrl/Customers/Delete/$custIdA" -Method "POST" -Body $tamperedDelBody -Session $adminAuth.Session
    Assert-Check "Security" "POST /Customers/Delete/{id} with tampered anti-forgery token rejected with HTTP 400" ($tamperedDelResp.StatusCode -eq 400) "HTTP $($tamperedDelResp.StatusCode)"

    $custAFinalCheck = [int](Invoke-SqlScalar "SELECT COUNT(*) FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $custIdA })
    Assert-Check "Security" "Customer record remains completely intact after anti-forgery rejections" ($custAFinalCheck -eq 1) "Customer count: $custAFinalCheck"
}
finally {
    # =========================================================================
    # PHASE 4: GUARANTEED TEST ISOLATION & CLEANUP
    # =========================================================================
    Write-Host ""
    Write-Host "[CLEANUP] Executing teardown of test-created data..." -ForegroundColor Gray

    try {
        # 1. Clean sales and sale items created by test
        foreach ($sidToClean in $createdSaleIds) {
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId = @Id" @{ "@Id" = $sidToClean }
            Invoke-SqlNonQuery "DELETE FROM Sales WHERE SaleId = @Id" @{ "@Id" = $sidToClean }
        }

        # 2. Clean products created by test
        foreach ($pidToClean in $createdProductIds) {
            Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
            Invoke-SqlNonQuery "DELETE FROM Products WHERE ProductId = @Id" @{ "@Id" = $pidToClean }
        }

        # 3. Clean customers created by test
        foreach ($cidToClean in $createdCustomerIds) {
            Invoke-SqlNonQuery "DELETE FROM Complaints WHERE CustomerId = @Id" @{ "@Id" = $cidToClean }
            Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId IN (SELECT SaleId FROM Sales WHERE CustomerId = @Id)" @{ "@Id" = $cidToClean }
            Invoke-SqlNonQuery "DELETE FROM Sales WHERE CustomerId = @Id" @{ "@Id" = $cidToClean }
            Invoke-SqlNonQuery "DELETE FROM Customers WHERE CustomerId = @Id" @{ "@Id" = $cidToClean }
        }

        # 3.1 Clean complaints created by complaint code tests
        foreach ($cmpIdToClean in $createdComplaintIds) {
            Invoke-SqlNonQuery "DELETE FROM Complaints WHERE ComplaintId = @Id" @{ "@Id" = $cmpIdToClean }
        }

        # 4. Fallback wildcard sweep for test fixtures
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE SaleId IN (SELECT SaleId FROM Sales WHERE CheckoutToken LIKE 'CASCADE_TEST_TOKEN_%')"
        Invoke-SqlNonQuery "DELETE FROM Sales WHERE CheckoutToken LIKE 'CASCADE_TEST_TOKEN_%'"
        Invoke-SqlNonQuery "DELETE FROM StockTransactions WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_CUST_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM SaleItems WHERE ProductId IN (SELECT ProductId FROM Products WHERE Name LIKE 'HTTP_CUST_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM Products WHERE Name LIKE 'HTTP_CUST_TEST_%'"
        Invoke-SqlNonQuery "DELETE FROM Complaints WHERE CustomerId IN (SELECT CustomerId FROM Customers WHERE Name LIKE 'HTTP_CUST_TEST_%')"
        Invoke-SqlNonQuery "DELETE FROM Customers WHERE Name LIKE 'HTTP_CUST_TEST_%' OR Email LIKE 'http_cust_%'"

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
Write-Host "CUSTOMER HTTP INTEGRATION TEST SUITE SUMMARY" -ForegroundColor Cyan
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

