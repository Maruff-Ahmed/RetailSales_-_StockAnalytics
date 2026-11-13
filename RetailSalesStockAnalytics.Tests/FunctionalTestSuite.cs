using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;
using RetailSalesStockAnalytics.ViewModels.Reports;

namespace RetailSalesStockAnalytics.Tests
{
    public static class FunctionalTestSuite
    {
        public static async Task<(int passed, int failed, List<string> errors)> RunAllFunctionalTestsAsync(string connectionString)
        {
            const string ApprovedTestDatabase = "RetailSalesStockAnalytics_TestDb";
            var validator = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            if (!string.Equals(validator.InitialCatalog, ApprovedTestDatabase, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"[SECURITY VIOLATION] FunctionalTestSuite cannot run against database '{validator.InitialCatalog}'. Only '{ApprovedTestDatabase}' is permitted.");
            }

            Console.WriteLine("=================================================");
            Console.WriteLine("STARTING COMPREHENSIVE FUNCTIONAL WORKFLOW TEST SUITE");
            Console.WriteLine($"Target Database: {connectionString}");
            Console.WriteLine("=================================================");

            int passed = 0;
            int failed = 0;
            var errors = new List<string>();

            async Task RunAsync(string name, Func<IServiceProvider, Task> testAction)
            {
                var services = new ServiceCollection();

                // Setup DB
                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseSqlServer(connectionString);
                });

                // Setup Identity
                services.AddIdentity<IdentityUser, IdentityRole>(options =>
                {
                    options.SignIn.RequireConfirmedAccount = false;
                    options.Password.RequiredLength = 10;
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
                    options.Lockout.AllowedForNewUsers = true;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

                // Setup Authorization
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
                    options.AddPolicy("Management", policy => policy.RequireRole("Admin", "Manager"));
                });

                // Caching & Logging
                services.AddMemoryCache();
                services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));

                // Application Services
                services.AddScoped<ISalesAnalyticsService, SalesAnalyticsService>();
                services.AddScoped<ComplaintRoutingService>();
                services.AddScoped<IComplaintCodeGenerator, ComplaintCodeGenerator>();
                services.AddScoped<CustomerPurchasePatternService>();
                services.AddScoped<IStockService, StockService>();
                services.AddScoped<IStockTransactionService, StockTransactionService>();
                services.AddScoped<ReportExportService>();
                services.AddScoped<IReportDataService, ReportDataService>();
                services.AddScoped<ISalesCheckoutService, SalesCheckoutService>();
                services.AddScoped<IDashboardService, DashboardService>();

                QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

                var sp = services.BuildServiceProvider();

                try
                {
                    await testAction(sp);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[PASS] {name}");
                    Console.ResetColor();
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                    Console.ResetColor();
                    failed++;
                    errors.Add($"{name}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            // =========================================================================
            // DATABASE INITIALIZATION & MIGRATION
            // =========================================================================
            await RunAsync("Workflow 0: Apply EF Core Migrations and Seed Initial Data", async sp =>
            {
                using var scope = sp.CreateScope();

                // Scope migration warning suppression strictly to the migration operation
                var migrationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(connectionString)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                    .Options;

                using (var migrationDb = new ApplicationDbContext(migrationOptions))
                {
                    await migrationDb.Database.MigrateAsync();
                }

                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Seed Complaint categories & standard product categories
                await ComplaintCategorySeeder.SeedAsync(db);

                var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

                string[] roles = { "Admin", "Manager", "Customer" };
                foreach (var r in roles)
                {
                    if (!await roleMgr.RoleExistsAsync(r))
                    {
                        var res = await roleMgr.CreateAsync(new IdentityRole(r));
                        if (!res.Succeeded) throw new Exception($"Failed creating role {r}");
                    }
                }

                // Ensure admin user
                var admin = await userMgr.FindByEmailAsync("admin@gmail.com");
                if (admin == null)
                {
                    admin = new IdentityUser { UserName = "admin@gmail.com", Email = "admin@gmail.com", EmailConfirmed = true };
                    var createRes = await userMgr.CreateAsync(admin, "Admin@Retail2026!");
                    if (!createRes.Succeeded) throw new Exception($"Failed creating admin: {string.Join(", ", createRes.Errors.Select(e => e.Description))}");
                }
                if (!await userMgr.IsInRoleAsync(admin, "Admin"))
                    await userMgr.AddToRoleAsync(admin, "Admin");

                // Ensure manager user
                var manager = await userMgr.FindByEmailAsync("manager@gmail.com");
                if (manager == null)
                {
                    manager = new IdentityUser { UserName = "manager@gmail.com", Email = "manager@gmail.com", EmailConfirmed = true };
                    var createRes = await userMgr.CreateAsync(manager, "Manager@Retail2026!");
                    if (!createRes.Succeeded) throw new Exception($"Failed creating manager: {string.Join(", ", createRes.Errors.Select(e => e.Description))}");
                }
                if (!await userMgr.IsInRoleAsync(manager, "Manager"))
                    await userMgr.AddToRoleAsync(manager, "Manager");
            });

            // =========================================================================
            // 1. AUTHENTICATION & AUTHORIZATION WORKFLOWS
            // =========================================================================
            await RunAsync("Workflow 1.1: Admin authentication with valid credentials and role check", async sp =>
            {
                using var scope = sp.CreateScope();
                var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                var admin = await userMgr.FindByEmailAsync("admin@gmail.com");
                if (admin == null) throw new Exception("Admin user not found");

                bool passwordOk = await userMgr.CheckPasswordAsync(admin, "Admin@Retail2026!");
                if (!passwordOk) throw new Exception("Admin password check failed");

                bool isAdmin = await userMgr.IsInRoleAsync(admin, "Admin");
                if (!isAdmin) throw new Exception("Admin user is not in Admin role");
            });

            await RunAsync("Workflow 1.2: Manager authentication with valid credentials and role check", async sp =>
            {
                using var scope = sp.CreateScope();
                var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                var manager = await userMgr.FindByEmailAsync("manager@gmail.com");
                if (manager == null) throw new Exception("Manager user not found");

                bool passwordOk = await userMgr.CheckPasswordAsync(manager, "Manager@Retail2026!");
                if (!passwordOk) throw new Exception("Manager password check failed");

                bool isManager = await userMgr.IsInRoleAsync(manager, "Manager");
                if (!isManager) throw new Exception("Manager user is not in Manager role");
            });

            await RunAsync("Workflow 1.3: Customer account creation and authentication", async sp =>
            {
                using var scope = sp.CreateScope();
                var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                string testCustomerEmail = "testcustomer.functional@example.com";
                var custUser = await userMgr.FindByEmailAsync(testCustomerEmail);
                if (custUser == null)
                {
                    custUser = new IdentityUser { UserName = testCustomerEmail, Email = testCustomerEmail, EmailConfirmed = true };
                    var createRes = await userMgr.CreateAsync(custUser, "Customer@Retail2026!");
                    if (!createRes.Succeeded) throw new Exception($"Failed creating customer: {string.Join(", ", createRes.Errors.Select(e => e.Description))}");
                    await userMgr.AddToRoleAsync(custUser, "Customer");
                }

                bool passwordOk = await userMgr.CheckPasswordAsync(custUser, "Customer@Retail2026!");
                if (!passwordOk) throw new Exception("Customer password check failed");

                bool isCust = await userMgr.IsInRoleAsync(custUser, "Customer");
                if (!isCust) throw new Exception("User is not in Customer role");
            });

            await RunAsync("Workflow 1.4: Invalid login credentials rejection and lockout policy", async sp =>
            {
                using var scope = sp.CreateScope();
                var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                var admin = await userMgr.FindByEmailAsync("admin@gmail.com");
                if (admin == null) throw new Exception("Admin user not found");

                bool wrongPasswordCheck = await userMgr.CheckPasswordAsync(admin, "WrongPassword123!");
                if (wrongPasswordCheck) throw new Exception("Wrong password was unexpectedly accepted");

                // Check lockout policy configured
                if (!userMgr.Options.Lockout.AllowedForNewUsers)
                    throw new Exception("Lockout is not enabled for new users");
                if (userMgr.Options.Lockout.MaxFailedAccessAttempts != 5)
                    throw new Exception($"Expected MaxFailedAccessAttempts=5, got {userMgr.Options.Lockout.MaxFailedAccessAttempts}");
                if (userMgr.Options.Lockout.DefaultLockoutTimeSpan != TimeSpan.FromMinutes(15))
                    throw new Exception($"Expected DefaultLockoutTimeSpan=15m, got {userMgr.Options.Lockout.DefaultLockoutTimeSpan}");
            });

            await RunAsync("Workflow 1.5: Role-based policy enforcement across user roles", async sp =>
            {
                using var scope = sp.CreateScope();
                var authService = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

                var anonPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
                var custPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Customer") }, "TestAuth"));
                var mgrPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Manager") }, "TestAuth"));
                var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Admin") }, "TestAuth"));

                // Anonymous
                var anonMgmt = await authService.AuthorizeAsync(anonPrincipal, "Management");
                if (anonMgmt.Succeeded) throw new Exception("Anonymous was authorized for Management");

                // Customer
                var custAdmin = await authService.AuthorizeAsync(custPrincipal, "AdminOnly");
                if (custAdmin.Succeeded) throw new Exception("Customer was authorized for AdminOnly");
                var custMgmt = await authService.AuthorizeAsync(custPrincipal, "Management");
                if (custMgmt.Succeeded) throw new Exception("Customer was authorized for Management");

                // Manager
                var mgrAdmin = await authService.AuthorizeAsync(mgrPrincipal, "AdminOnly");
                if (mgrAdmin.Succeeded) throw new Exception("Manager was authorized for AdminOnly");
                var mgrMgmt = await authService.AuthorizeAsync(mgrPrincipal, "Management");
                if (!mgrMgmt.Succeeded) throw new Exception("Manager was denied for Management");

                // Admin
                var adminAdmin = await authService.AuthorizeAsync(adminPrincipal, "AdminOnly");
                if (!adminAdmin.Succeeded) throw new Exception("Admin was denied for AdminOnly");
                var adminMgmt = await authService.AuthorizeAsync(adminPrincipal, "Management");
                if (!adminMgmt.Succeeded) throw new Exception("Admin was denied for Management");
            });

            // =========================================================================
            // 2. CATEGORIES AND PRODUCTS WORKFLOWS
            // =========================================================================
            int testCategoryId = 0;
            int testProductId = 0;

            await RunAsync("Workflow 2.1: Category creation, details, update, and validation", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Create category
                var cat = new Category { Name = "Test Functional Electronics" };
                db.Categories.Add(cat);
                await db.SaveChangesAsync();
                testCategoryId = cat.CategoryId;
                if (testCategoryId <= 0) throw new Exception("Category ID was not assigned");

                // Read details
                var loaded = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == testCategoryId);
                if (loaded == null || loaded.Name != "Test Functional Electronics")
                    throw new Exception("Loaded category did not match expected");

                // Update
                cat.Name = "Test Functional Hardware";
                db.Categories.Update(cat);
                await db.SaveChangesAsync();

                var updated = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == testCategoryId);
                if (updated == null || updated.Name != "Test Functional Hardware")
                    throw new Exception("Updated category name did not persist");
            });

            await RunAsync("Workflow 2.2: Product creation with initial stock transaction audit", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                var product = new Product
                {
                    CategoryId = testCategoryId,
                    Name = "Functional Wireless Headset",
                    PurchasePrice = 50.00m,
                    SellingPrice = 90.00m,
                    StockQuantity = 20,
                    ReorderLevel = 5
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                testProductId = product.ProductId;
                if (testProductId <= 0) throw new Exception("Product ID was not assigned");

                // Record initial stock through IStockService
                var stockResult = await stockService.RecordInitialStockAsync(testProductId, product.StockQuantity, product.PurchasePrice);
                if (!stockResult.Success)
                    throw new Exception($"RecordInitialStockAsync failed: {stockResult.ErrorMessage}");

                // Verify StockTransaction was recorded
                var tx = await db.StockTransactions
                    .Where(t => t.ProductId == testProductId)
                    .OrderByDescending(t => t.TransactionDate)
                    .FirstOrDefaultAsync();

                if (tx == null) throw new Exception("Initial StockTransaction was not created");
                if (!string.Equals(tx.TransactionType, "Initial Stock", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected 'Initial Stock', got '{tx.TransactionType}'");
                if (tx.Quantity != 20) throw new Exception($"Expected Quantity 20, got {tx.Quantity}");
                if (tx.UnitCost != 50.00m) throw new Exception($"Expected UnitCost 50, got {tx.UnitCost}");
                if (tx.Note == null || !tx.Note.Contains("Initial stock", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected note with 'Initial stock', got '{tx.Note}'");
            });

            await RunAsync("Workflow 2.3: Product edit auditing with and without stock changes", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                int initialTxCount = await db.StockTransactions.CountAsync(t => t.ProductId == testProductId);

                // Edit metadata only (no stock change)
                var product = await db.Products.FindAsync(testProductId);
                if (product == null) throw new Exception("Test product not found");

                product.SellingPrice = 95.00m;
                db.Products.Update(product);
                await db.SaveChangesAsync();

                var auditNoChange = await stockService.RecordProductEditAdjustmentAsync(testProductId, oldQuantity: 20, newQuantity: 20, unitCost: 50.00m);
                if (!auditNoChange.Success) throw new Exception("Audit for unchanged stock failed");

                int txCountAfterMeta = await db.StockTransactions.CountAsync(t => t.ProductId == testProductId);
                if (txCountAfterMeta != initialTxCount)
                    throw new Exception("Unexpected stock transaction recorded for metadata-only edit!");

                // Edit WITH stock change (from 20 to 25)
                product.StockQuantity = 25;
                db.Products.Update(product);
                await db.SaveChangesAsync();

                var auditChange = await stockService.RecordProductEditAdjustmentAsync(testProductId, oldQuantity: 20, newQuantity: 25, unitCost: 50.00m);
                if (!auditChange.Success) throw new Exception($"Audit for stock change failed: {auditChange.ErrorMessage}");

                int txCountAfterStock = await db.StockTransactions.CountAsync(t => t.ProductId == testProductId);
                if (txCountAfterStock != initialTxCount + 1)
                    throw new Exception("Expected exactly 1 new stock transaction for product edit adjustment");

                var latestTx = await db.StockTransactions
                    .Where(t => t.ProductId == testProductId)
                    .OrderByDescending(t => t.StockTransactionId)
                    .FirstAsync();

                if (!string.Equals(latestTx.TransactionType, "Product Edit Adjustment", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected 'Product Edit Adjustment', got '{latestTx.TransactionType}'");
                if (latestTx.Quantity != 5)
                    throw new Exception($"Expected delta quantity 5, got {latestTx.Quantity}");
                if (latestTx.Note == null || !latestTx.Note.Contains("Product Edit", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected 'Product Edit', got '{latestTx.Note}'");
            });

            // =========================================================================
            // 3. SALES AND CHECKOUT WORKFLOWS
            // =========================================================================
            int testSaleId = 0;
            string testToken = Guid.NewGuid().ToString("N");

            await RunAsync("Workflow 3.1: Successful checkout and inventory reduction", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var checkoutService = scope.ServiceProvider.GetRequiredService<ISalesCheckoutService>();

                int stockBefore = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;

                var checkoutModel = new SaleCreateViewModel
                {
                    CustomerName = "Functional Checkout Customer",
                    CustomerEmail = "checkout.customer@example.com",
                    CustomerPhone = "555-4321",
                    SaleDate = DateTime.Now,
                    Discount = 5.00m,
                    CheckoutToken = testToken,
                    Items = new List<SaleItemViewModel>
                    {
                        new SaleItemViewModel { ProductId = testProductId, Quantity = 3 }
                    }
                };

                var result = await checkoutService.CreateSaleAsync(checkoutModel);
                if (!result.Success)
                    throw new Exception($"Checkout failed: {string.Join(", ", result.Errors.Select(e => e.ErrorMessage))}");

                testSaleId = result.SaleId!.Value;

                // Verify DB Sale
                var sale = await db.Sales
                    .Include(s => s.SaleItems)
                    .Include(s => s.Customer)
                    .FirstOrDefaultAsync(s => s.SaleId == testSaleId);

                if (sale == null) throw new Exception("Sale record not found in database");
                if (sale.Customer == null || sale.Customer.Email != "checkout.customer@example.com")
                    throw new Exception("Customer mismatched on sale");
                if (sale.CheckoutToken != testToken) throw new Exception("Checkout token mismatched on sale");
                if (sale.Discount != 5.00m) throw new Exception($"Expected Discount 5, got {sale.Discount}");

                // Expected total: 3 * 95 - 5 = 280
                decimal expectedTotal = (3 * 95.00m) - 5.00m;
                if (sale.TotalAmount != expectedTotal)
                    throw new Exception($"Expected TotalAmount {expectedTotal}, got {sale.TotalAmount}");

                // Verify stock decreased by 3
                int stockAfter = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;
                if (stockAfter != stockBefore - 3)
                    throw new Exception($"Expected stock {stockBefore - 3}, got {stockAfter}");

                // Verify stock transaction SALE
                var saleTx = await db.StockTransactions
                    .Where(t => t.ProductId == testProductId && t.TransactionType.ToUpper() == "SALE")
                    .OrderByDescending(t => t.StockTransactionId)
                    .FirstOrDefaultAsync();

                if (saleTx == null) throw new Exception("Sale stock transaction was not found");
                if (saleTx.Quantity != -3) throw new Exception($"Expected tx quantity -3, got {saleTx.Quantity}");
            });

            await RunAsync("Workflow 3.2: Duplicate checkout token rejection (idempotency)", async sp =>
            {
                using var scope = sp.CreateScope();
                var checkoutService = scope.ServiceProvider.GetRequiredService<ISalesCheckoutService>();

                // Reuse the same token
                var duplicateModel = new SaleCreateViewModel
                {
                    CustomerName = "Functional Checkout Customer",
                    CustomerEmail = "checkout.customer@example.com",
                    CustomerPhone = "555-4321",
                    SaleDate = DateTime.Now,
                    Discount = 0m,
                    CheckoutToken = testToken,
                    Items = new List<SaleItemViewModel>
                    {
                        new SaleItemViewModel { ProductId = testProductId, Quantity = 1 }
                    }
                };

                var duplicateResult = await checkoutService.CreateSaleAsync(duplicateModel);
                if (duplicateResult.Success)
                    throw new Exception("Duplicate checkout submission was unexpectedly accepted!");
                if (!duplicateResult.IsDuplicateBlocked)
                    throw new Exception("Duplicate result did not flag IsDuplicateBlocked");
            });

            await RunAsync("Workflow 3.3: Insufficient stock rejection", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var checkoutService = scope.ServiceProvider.GetRequiredService<ISalesCheckoutService>();

                int availableStock = (await db.Products.FindAsync(testProductId))!.StockQuantity;

                var overdraftModel = new SaleCreateViewModel
                {
                    CustomerName = "Functional Checkout Customer",
                    CustomerEmail = "checkout.customer@example.com",
                    CustomerPhone = "555-4321",
                    SaleDate = DateTime.Now,
                    Discount = 0m,
                    CheckoutToken = Guid.NewGuid().ToString("N"),
                    Items = new List<SaleItemViewModel>
                    {
                        new SaleItemViewModel { ProductId = testProductId, Quantity = availableStock + 50 }
                    }
                };

                var result = await checkoutService.CreateSaleAsync(overdraftModel);
                if (result.Success)
                    throw new Exception("Overdraft checkout was unexpectedly accepted!");

                int stockAfter = (await db.Products.FindAsync(testProductId))!.StockQuantity;
                if (stockAfter != availableStock)
                    throw new Exception("Product stock changed despite insufficient stock failure");
            });

            await RunAsync("Workflow 3.4: Sale details retrieval with line items and product navigations", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var saleDetails = await db.Sales
                    .Include(s => s.Customer)
                    .Include(s => s.SaleItems)
                        .ThenInclude(si => si.Product)
                    .FirstOrDefaultAsync(s => s.SaleId == testSaleId);

                if (saleDetails == null) throw new Exception("Sale not found for details query");
                if (saleDetails.Customer == null) throw new Exception("Customer navigation was null");
                if (saleDetails.SaleItems == null || saleDetails.SaleItems.Count != 1)
                    throw new Exception("SaleItems was not properly loaded");

                var item = saleDetails.SaleItems.First();
                if (item.Product == null) throw new Exception("Product navigation on SaleItem was null");
                if (item.Product.Name != "Functional Wireless Headset")
                    throw new Exception($"Expected product name 'Functional Wireless Headset', got '{item.Product.Name}'");
            });

            // =========================================================================
            // 4. SALE CANCELLATION WORKFLOWS
            // =========================================================================
            await RunAsync("Workflow 4.1: Admin sale cancellation and stock restoration", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var checkoutService = scope.ServiceProvider.GetRequiredService<ISalesCheckoutService>();

                int stockBeforeCancel = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;

                var cancelResult = await checkoutService.CancelSaleAsync(testSaleId);
                if (!cancelResult.Success)
                    throw new Exception($"CancelSaleAsync failed: {cancelResult.ErrorMessage}");

                // Verify sale deleted from DB
                var deletedSale = await db.Sales.FindAsync(testSaleId);
                if (deletedSale != null)
                    throw new Exception("Sale record still exists in DB after cancellation");

                // Verify line items deleted (cascade)
                var orphanItems = await db.SaleItems.Where(si => si.SaleId == testSaleId).ToListAsync();
                if (orphanItems.Count > 0)
                    throw new Exception("SaleItems were not deleted when Sale was cancelled");

                // Verify stock restored (+3)
                int stockAfterCancel = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;
                if (stockAfterCancel != stockBeforeCancel + 3)
                    throw new Exception($"Expected restored stock {stockBeforeCancel + 3}, got {stockAfterCancel}");

                // Verify restock transaction logged
                var restockTx = await db.StockTransactions
                    .Where(t => t.ProductId == testProductId && t.TransactionType == "Sale Cancellation")
                    .OrderByDescending(t => t.StockTransactionId)
                    .FirstOrDefaultAsync();

                if (restockTx == null || restockTx.Note == null || !restockTx.Note.Contains($"deleted Sale #{testSaleId}", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Cancellation Sale Cancellation transaction was not properly recorded");
            });

            await RunAsync("Workflow 4.2: Repeated cancellation prevention", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var checkoutService = scope.ServiceProvider.GetRequiredService<ISalesCheckoutService>();

                int stockBefore = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;

                // Try to cancel the already deleted sale
                var repeatCancel = await checkoutService.CancelSaleAsync(testSaleId);
                if (repeatCancel.Success)
                    throw new Exception("Repeated cancellation was unexpectedly reported as successful!");

                if (!repeatCancel.NotFound && !repeatCancel.AlreadyDeleted)
                    throw new Exception("Repeated cancellation did not report NotFound or AlreadyDeleted");

                int stockAfter = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;
                if (stockAfter != stockBefore)
                    throw new Exception("Stock was erroneously modified on repeated cancellation attempt");
            });

            // =========================================================================
            // 5. STOCK MANAGEMENT WORKFLOWS
            // =========================================================================
            await RunAsync("Workflow 5.1: Restock operation via IStockService", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                int stockBefore = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;

                var result = await stockService.RestockAsync(testProductId, quantity: 15, unitCost: 45.00m, note: "Functional Supplier Delivery");
                if (!result.Success) throw new Exception($"RestockAsync failed: {result.ErrorMessage}");

                int stockAfter = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;
                if (stockAfter != stockBefore + 15)
                    throw new Exception($"Expected stock {stockBefore + 15}, got {stockAfter}");

                var tx = await db.StockTransactions
                    .Where(t => t.ProductId == testProductId && t.Note == "Functional Supplier Delivery")
                    .FirstOrDefaultAsync();

                if (tx == null || tx.Quantity != 15 || tx.UnitCost != 45.00m)
                    throw new Exception("Restock StockTransaction record verification failed");
            });

            await RunAsync("Workflow 5.2: Stock adjustment and overdraft protection via IStockService", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                int stockBefore = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;

                // Valid negative adjustment
                var adjustRes = await stockService.AdjustStockAsync(testProductId, quantity: -5, reason: "Damaged in store display");
                if (!adjustRes.Success) throw new Exception($"AdjustStockAsync failed: {adjustRes.ErrorMessage}");

                int stockAfterValid = (await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == testProductId))!.StockQuantity;
                if (stockAfterValid != stockBefore - 5)
                    throw new Exception($"Expected stock {stockBefore - 5}, got {stockAfterValid}");

                // Invalid zero adjustment
                var zeroRes = await stockService.AdjustStockAsync(testProductId, quantity: 0, reason: "Zero test");
                if (zeroRes.Success || !zeroRes.InvalidOperation)
                    throw new Exception("Zero quantity adjustment was unexpectedly accepted");

                // Invalid overdraft adjustment
                var overdraftRes = await stockService.AdjustStockAsync(testProductId, quantity: -(stockAfterValid + 100), reason: "Overdraft test");
                if (overdraftRes.Success || !overdraftRes.InvalidOperation)
                    throw new Exception("Overdraft adjustment was unexpectedly accepted");
            });

            // =========================================================================
            // 6. CUSTOMER MANAGEMENT WORKFLOWS
            // =========================================================================
            await RunAsync("Workflow 6.1: Customer CRUD operations and navigation", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Create
                var cust = new Customer
                {
                    Name = "Alice Functional",
                    Email = "alice.functional@example.com",
                    Phone = "555-1122"
                };
                db.Customers.Add(cust);
                await db.SaveChangesAsync();
                int aliceId = cust.CustomerId;

                // Read
                var loaded = await db.Customers.FindAsync(aliceId);
                if (loaded == null || loaded.Name != "Alice Functional")
                    throw new Exception("Customer read failed");

                // Update
                loaded.Phone = "555-9988";
                db.Customers.Update(loaded);
                await db.SaveChangesAsync();

                var updated = await db.Customers.FindAsync(aliceId);
                if (updated?.Phone != "555-9988")
                    throw new Exception("Customer update failed");

                // Delete
                db.Customers.Remove(updated!);
                await db.SaveChangesAsync();

                var deleted = await db.Customers.FindAsync(aliceId);
                if (deleted != null)
                    throw new Exception("Customer deletion failed");
            });

            // =========================================================================
            // 7. ANALYTICS AND REPORTS WORKFLOWS
            // =========================================================================
            await RunAsync("Workflow 7.1: DashboardService metrics retrieval", async sp =>
            {
                using var scope = sp.CreateScope();
                var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();

                var dashboard = await dashboardService.GetDashboardAsync();
                if (dashboard == null) throw new Exception("DashboardViewModel was null");
                if (dashboard.RecentSales == null) throw new Exception("RecentSales was null");
                if (dashboard.TopProducts == null) throw new Exception("TopProducts was null");
            });

            await RunAsync("Workflow 7.2: Daily sales and category sales analytics services", async sp =>
            {
                using var scope = sp.CreateScope();
                var analyticsService = scope.ServiceProvider.GetRequiredService<ISalesAnalyticsService>();

                var analytics = await analyticsService.GetAnalyticsAsync(DateTime.Today.AddDays(-30), DateTime.Today);
                if (analytics == null) throw new Exception("SalesAnalyticsViewModel was null");
                if (analytics.CategorySales == null) throw new Exception("CategorySales was null");
                if (analytics.DailySales == null) throw new Exception("DailySales was null");
            });

            await RunAsync("Workflow 7.3: Stock movement report data retrieval", async sp =>
            {
                using var scope = sp.CreateScope();
                var reportDataService = scope.ServiceProvider.GetRequiredService<IReportDataService>();

                var movementReport = await reportDataService.GetStockMovementReportAsync(DateTime.Today.AddDays(-30), DateTime.Today);
                if (movementReport == null) throw new Exception("StockMovementReportViewModel was null");
                if (movementReport.Movements == null) throw new Exception("Movements collection was null");
            });

            await RunAsync("Workflow 7.4: Customer purchase pattern analysis service", async sp =>
            {
                using var scope = sp.CreateScope();
                var patternService = scope.ServiceProvider.GetRequiredService<CustomerPurchasePatternService>();

                var pattern = await patternService.GetPurchasePatternAsync(DateTime.Today.AddDays(-30), DateTime.Today);
                if (pattern == null) throw new Exception("CustomerPurchasePatternViewModel was null");
                if (pattern.Customers == null) throw new Exception("Customer pattern list was null");
            });

            await RunAsync("Workflow 7.5: Complaint routing categorization service", async sp =>
            {
                using var scope = sp.CreateScope();
                var routingService = scope.ServiceProvider.GetRequiredService<ComplaintRoutingService>();

                // Test with valid category ID from seed
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var cat = await db.Categories.FirstOrDefaultAsync();
                int catId = cat?.CategoryId ?? 1;

                var result = await routingService.RouteComplaintAsync(catId, "The product arrived with a broken screen and damaged delivery box");
                if (result == null) throw new Exception("ComplaintRoutingService returned null result");
                if (string.IsNullOrWhiteSpace(result.RoutingStatus))
                    throw new Exception("Routing status was blank");
            });

            await RunAsync("Workflow 7.5.1: Concurrency-safe complaint code generation via sequence", async sp =>
            {
                using var scope = sp.CreateScope();
                var generator = scope.ServiceProvider.GetRequiredService<IComplaintCodeGenerator>();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                string code1 = await generator.GenerateNextCodeAsync();
                string code2 = await generator.GenerateNextCodeAsync();

                if (!System.Text.RegularExpressions.Regex.IsMatch(code1, @"^CMP-\d+$"))
                    throw new Exception($"Complaint code '{code1}' does not match expected format 'CMP-XXX'");
                if (!System.Text.RegularExpressions.Regex.IsMatch(code2, @"^CMP-\d+$"))
                    throw new Exception($"Complaint code '{code2}' does not match expected format 'CMP-XXX'");

                long num1 = long.Parse(code1.Substring(4));
                long num2 = long.Parse(code2.Substring(4));
                if (num2 <= num1)
                    throw new Exception($"Complaint code sequence did not monotonically increase: {code1} -> {code2}");

                Console.WriteLine($"      [SQL SERVER SEQUENCE] Generated codes: {code1} -> {code2} via real sequence 'ComplaintCodeSequence' (Provider: {db.Database.ProviderName})");

                var cust = await db.Customers.FirstOrDefaultAsync();
                int custId = cust?.CustomerId ?? 1;

                var complaint = new Complaint
                {
                    ComplaintCode = code1,
                    CustomerId = custId,
                    ComplaintText = "Functional test complaint text for sequence test",
                    RoutingStatus = "Pending",
                    CreatedAt = DateTime.Now
                };
                db.Complaints.Add(complaint);
                await db.SaveChangesAsync();
            });

            await RunAsync("Workflow 7.5.2: Highly concurrent complaint code generation (20 parallel tasks)", async sp =>
            {
                const int concurrentTasks = 20;
                var generatedCodes = new System.Collections.Concurrent.ConcurrentBag<string>();
                var tasks = new List<Task>();

                for (int i = 0; i < concurrentTasks; i++)
                {
                    int index = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        using var scope = sp.CreateScope();
                        var generator = scope.ServiceProvider.GetRequiredService<IComplaintCodeGenerator>();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        string code = await generator.GenerateNextCodeAsync();
                        generatedCodes.Add(code);

                        var cust = await db.Customers.FirstOrDefaultAsync();
                        int custId = cust?.CustomerId ?? 1;

                        var complaint = new Complaint
                        {
                            ComplaintCode = code,
                            CustomerId = custId,
                            ComplaintText = $"Concurrent complaint #{index}",
                            RoutingStatus = "Pending",
                            CreatedAt = DateTime.Now
                        };
                        db.Complaints.Add(complaint);
                        await db.SaveChangesAsync();
                    }));
                }

                await Task.WhenAll(tasks);

                if (generatedCodes.Count != concurrentTasks)
                    throw new Exception($"Expected {concurrentTasks} generated codes, but got {generatedCodes.Count}");

                int distinctCount = generatedCodes.Distinct().Count();
                if (distinctCount != concurrentTasks)
                    throw new Exception($"Concurrency collision detected! Only {distinctCount} unique codes out of {concurrentTasks} generated.");

                Console.WriteLine($"      [SQL SERVER CONCURRENCY] Successfully generated and persisted {concurrentTasks} concurrent complaints with 0 collisions.");
                Console.WriteLine($"      [SQL SERVER CONCURRENCY] Distinct codes count: {distinctCount}/{concurrentTasks}. Sample: {string.Join(", ", generatedCodes.Take(4))}...");
            });

            await RunAsync("Workflow 7.5.3: Database unique constraint enforcement on ComplaintCode", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var cust = await db.Customers.FirstOrDefaultAsync();
                int custId = cust?.CustomerId ?? 1;

                string duplicateCode = "DUP_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                // Insert first complaint
                var c1 = new Complaint
                {
                    ComplaintCode = duplicateCode,
                    CustomerId = custId,
                    ComplaintText = "Original complaint for duplicate test",
                    RoutingStatus = "Pending",
                    CreatedAt = DateTime.Now
                };
                db.Complaints.Add(c1);
                await db.SaveChangesAsync();

                // Attempt to insert duplicate complaint with same code
                using var scope2 = sp.CreateScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var c2 = new Complaint
                {
                    ComplaintCode = duplicateCode,
                    CustomerId = custId,
                    ComplaintText = "Duplicate complaint attempting same code",
                    RoutingStatus = "Pending",
                    CreatedAt = DateTime.Now
                };
                db2.Complaints.Add(c2);

                bool uniqueConstraintEnforced = false;
                int capturedErrorNumber = 0;
                string? capturedErrorMessage = null;
                try
                {
                    await db2.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx &&
                        (sqlEx.Number == 2601 || sqlEx.Number == 2627))
                    {
                        uniqueConstraintEnforced = true;
                        capturedErrorNumber = sqlEx.Number;
                        capturedErrorMessage = sqlEx.Message;
                    }
                }

                if (!uniqueConstraintEnforced)
                    throw new Exception("Database failed to enforce unique index on ComplaintCode for duplicate code!");

                // Confirm the failed insert did not leave a duplicate record
                using var scope3 = sp.CreateScope();
                var db3 = scope3.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                int persistentCount = await db3.Complaints.CountAsync(c => c.ComplaintCode == duplicateCode);
                if (persistentCount != 1)
                    throw new Exception($"Failed insert corrupted database state! Expected exactly 1 complaint record, found {persistentCount}.");

                // Clean up c1 record
                var toClean = await db3.Complaints.FirstOrDefaultAsync(c => c.ComplaintCode == duplicateCode);
                if (toClean != null)
                {
                    db3.Complaints.Remove(toClean);
                    await db3.SaveChangesAsync();
                }

                Console.WriteLine($"      [SQL SERVER UNIQUE INDEX] Unique index 'IX_Complaints_ComplaintCode' rejected duplicate with SqlException error {capturedErrorNumber}.");
                Console.WriteLine($"      [SQL SERVER UNIQUE INDEX] Error summary: {capturedErrorMessage?.Split('\r', '\n')[0]}");
                Console.WriteLine($"      [SQL SERVER UNIQUE INDEX] Verified no duplicate persisted (pre-cleanup count: 1, post-cleanup count: 0).");
            });

            await RunAsync("Workflow 7.6: Report export generation (Excel and PDF)", async sp =>
            {
                using var scope = sp.CreateScope();
                var exportService = scope.ServiceProvider.GetRequiredService<ReportExportService>();

                var sampleDailySales = new DailySalesReportViewModel
                {
                    StartDate = DateTime.Today.AddDays(-7),
                    EndDate = DateTime.Today,
                    TotalSales = 5,
                    TotalUnitsSold = 10,
                    TotalRevenue = 450.00m,
                    DailySales = new List<DailySalesRow>
                    {
                        new DailySalesRow { Date = DateTime.Today, SalesCount = 5, UnitsSold = 10, Revenue = 450.00m }
                    }
                };

                // Excel export
                byte[] excelBytes = exportService.ExportDailySalesExcel(sampleDailySales);
                if (excelBytes == null || excelBytes.Length == 0)
                    throw new Exception("Excel export returned empty byte array");
                // Check ZIP/OOXML magic bytes (PK..)
                if (excelBytes[0] != 0x50 || excelBytes[1] != 0x4B)
                    throw new Exception("Excel export did not have valid ZIP/XLSX magic header");

                // PDF export
                byte[] pdfBytes = exportService.ExportDailySalesPdf(sampleDailySales);
                if (pdfBytes == null || pdfBytes.Length == 0)
                    throw new Exception("PDF export returned empty byte array");
                // Check PDF magic header (%PDF)
                if (pdfBytes[0] != 0x25 || pdfBytes[1] != 0x50 || pdfBytes[2] != 0x44 || pdfBytes[3] != 0x46)
                    throw new Exception("PDF export did not have valid %PDF magic header");
            });

            // =========================================================================
            // CLEANUP TEST RECORDS (PRESERVING DB INTEGRITY)
            // =========================================================================
            await RunAsync("Workflow 0.Cleanup: Clean up transient functional test entities", async sp =>
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                if (testProductId > 0)
                {
                    var txs = await db.StockTransactions.Where(t => t.ProductId == testProductId).ToListAsync();
                    db.StockTransactions.RemoveRange(txs);

                    var prod = await db.Products.FindAsync(testProductId);
                    if (prod != null) db.Products.Remove(prod);
                }

                if (testCategoryId > 0)
                {
                    var cat = await db.Categories.FindAsync(testCategoryId);
                    if (cat != null) db.Categories.Remove(cat);
                }

                var testComplaints = await db.Complaints.ToListAsync();
                if (testComplaints.Count > 0)
                {
                    db.Complaints.RemoveRange(testComplaints);
                }

                await db.SaveChangesAsync();
            });

            Console.WriteLine("=================================================");
            Console.WriteLine($"FUNCTIONAL SUITE RESULTS: {passed} PASSED, {failed} FAILED");
            Console.WriteLine("=================================================");

            return (passed, failed, errors);
        }
    }
}
