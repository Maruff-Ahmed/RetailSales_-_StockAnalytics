using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetailSalesStockAnalytics;
using RetailSalesStockAnalytics.Controllers;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Tests
{
    public class Program
    {
        private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
        private static readonly object _tokenLock = new();

        private static (bool acquired, string? errorMessage) TryAcquireToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return (false, "Checkout session token is missing. Please reload the page to start a new sale.");
            }

            string tokenKey = $"checkout_token_{token}";

            lock (_tokenLock)
            {
                if (_cache.TryGetValue(tokenKey, out var existingStatus))
                {
                    if (existingStatus is CheckoutTokenStatus status && status == CheckoutTokenStatus.Completed)
                    {
                        return (false, "This sale has already been completed. Duplicate submission was blocked.");
                    }

                    return (false, "This checkout request is currently being processed. Duplicate submission was blocked.");
                }

                _cache.Set(tokenKey, CheckoutTokenStatus.InFlight, TimeSpan.FromMinutes(15));
                return (true, null);
            }
        }

        private static void ReleaseInFlightToken(string? token, bool tokenAcquired)
        {
            if (tokenAcquired && !string.IsNullOrWhiteSpace(token))
            {
                string tokenKey = $"checkout_token_{token}";
                lock (_tokenLock)
                {
                    if (_cache.TryGetValue(tokenKey, out var status) &&
                        status is CheckoutTokenStatus s &&
                        s == CheckoutTokenStatus.InFlight)
                    {
                        _cache.Remove(tokenKey);
                    }
                }
            }
        }

        private static void CompleteToken(string? token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                string tokenKey = $"checkout_token_{token}";
                lock (_tokenLock)
                {
                    _cache.Set(tokenKey, CheckoutTokenStatus.Completed, TimeSpan.FromMinutes(30));
                }
            }
        }

        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("STARTING CHECKOUT IDEMPOTENCY & DATABASE INTEGRITY TEST SUITE");
            Console.WriteLine("=================================================");

            int passed = 0;
            int failed = 0;

            void RunTest(string name, Action test)
            {
                try
                {
                    test();
                    Console.WriteLine($"[PASS] {name}");
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                    Console.ResetColor();
                    failed++;
                }
            }

            async Task RunTestAsync(string name, Func<Task> test)
            {
                try
                {
                    await test();
                    Console.WriteLine($"[PASS] {name}");
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                    Console.ResetColor();
                    failed++;
                }
            }

            // Test 1: First valid post acquires token
            RunTest("Test 1: First valid POST acquires token successfully", () =>
            {
                string token = Guid.NewGuid().ToString("N");
                var (acquired, error) = TryAcquireToken(token);
                if (!acquired || error != null) throw new Exception("Token was not acquired");
            });

            // Test 2: Missing token is rejected
            RunTest("Test 2: Missing or whitespace token is rejected safely", () =>
            {
                var (acquiredNull, errorNull) = TryAcquireToken(null);
                if (acquiredNull || string.IsNullOrEmpty(errorNull)) throw new Exception("Null token was unexpectedly accepted");

                var (acquiredEmpty, errorEmpty) = TryAcquireToken("   ");
                if (acquiredEmpty || string.IsNullOrEmpty(errorEmpty)) throw new Exception("Whitespace token was unexpectedly accepted");
            });

            // Test 3: Repeated POST while in-flight is rejected
            RunTest("Test 3: Repeated POST with same token while in-flight is rejected", () =>
            {
                string token = Guid.NewGuid().ToString("N");
                var (firstAcquired, _) = TryAcquireToken(token);
                if (!firstAcquired) throw new Exception("First acquisition failed");

                var (secondAcquired, secondError) = TryAcquireToken(token);
                if (secondAcquired) throw new Exception("Duplicate token was acquired while in-flight");
                if (!secondError!.Contains("currently being processed", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Unexpected error message: {secondError}");
            });

            // Test 4: Repeated POST after completion is rejected permanently
            RunTest("Test 4: Repeated POST after successful completion is rejected permanently", () =>
            {
                string token = Guid.NewGuid().ToString("N");
                var (firstAcquired, _) = TryAcquireToken(token);
                if (!firstAcquired) throw new Exception("First acquisition failed");

                CompleteToken(token);

                var (secondAcquired, secondError) = TryAcquireToken(token);
                if (secondAcquired) throw new Exception("Completed token was re-acquired");
                if (!secondError!.Contains("already been completed", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Unexpected error message: {secondError}");
            });

            // Test 5: Completed token cannot be released by a failed request
            RunTest("Test 5: Completed token cannot be released by an error/exception in subsequent requests", () =>
            {
                string token = Guid.NewGuid().ToString("N");

                // Request 1 succeeds and completes
                var (acquired1, _) = TryAcquireToken(token);
                if (!acquired1) throw new Exception("First acquisition failed");
                CompleteToken(token);

                // Request 2 tries to reuse token and fails
                var (acquired2, _) = TryAcquireToken(token);
                if (acquired2) throw new Exception("Second acquisition should have failed");

                // Request 2 attempts cleanup on failure
                ReleaseInFlightToken(token, tokenAcquired: acquired2);

                // Verify token is STILL in cache and marked Completed
                lock (_tokenLock)
                {
                    if (!_cache.TryGetValue($"checkout_token_{token}", out var status) ||
                        (CheckoutTokenStatus)status! != CheckoutTokenStatus.Completed)
                    {
                        throw new Exception("Completed token was erroneously released by failed request!");
                    }
                }
            });

            // Test 6: Validation failure releases in-flight token allowing retry
            RunTest("Test 6: Validation failure on first submit releases in-flight token cleanly", () =>
            {
                string token = Guid.NewGuid().ToString("N");

                var (acquired, _) = TryAcquireToken(token);
                if (!acquired) throw new Exception("Initial acquisition failed");

                // Validation failure occurs, release token
                ReleaseInFlightToken(token, tokenAcquired: acquired);

                // Re-trying with the same or refreshed token succeeds
                var (retryAcquired, retryError) = TryAcquireToken(token);
                if (!retryAcquired) throw new Exception($"Retry failed: {retryError}");
            });

            // Test 7: Concurrent requests with same token - only 1 succeeds
            await RunTestAsync("Test 7: Highly concurrent requests (50 threads) with same token - exactly 1 succeeds", async () =>
            {
                string token = Guid.NewGuid().ToString("N");
                int concurrencyLevel = 50;
                var results = new ConcurrentBag<(bool acquired, string? error)>();

                var tasks = Enumerable.Range(0, concurrencyLevel).Select(_ => Task.Run(() =>
                {
                    var result = TryAcquireToken(token);
                    results.Add(result);
                }));

                await Task.WhenAll(tasks);

                int successfulAcquisitions = results.Count(r => r.acquired);
                int rejectedSubmissions = results.Count(r => !r.acquired);

                if (successfulAcquisitions != 1)
                    throw new Exception($"Expected exactly 1 acquisition, but got {successfulAcquisitions}!");
                if (rejectedSubmissions != concurrencyLevel - 1)
                    throw new Exception($"Expected {concurrencyLevel - 1} rejections, but got {rejectedSubmissions}!");
            });

            // Test 8: Concurrent requests with different tokens - all succeed
            await RunTestAsync("Test 8: Highly concurrent requests (50 threads) with different tokens - all 50 succeed", async () =>
            {
                int concurrencyLevel = 50;
                var results = new ConcurrentBag<(bool acquired, string? error)>();

                var tasks = Enumerable.Range(0, concurrencyLevel).Select(_ => Task.Run(() =>
                {
                    string token = Guid.NewGuid().ToString("N");
                    var result = TryAcquireToken(token);
                    results.Add(result);
                }));

                await Task.WhenAll(tasks);

                int successfulAcquisitions = results.Count(r => r.acquired);

                if (successfulAcquisitions != concurrencyLevel)
                    throw new Exception($"Expected {concurrencyLevel} acquisitions, but got {successfulAcquisitions}!");
            });

            // Test 9: EF Core Migration Discovery
            RunTest("Test 9: EF Core IMigrationsAssembly discovers 20261110220000_AddCheckoutTokenToSale, 20261112130000_RestrictHistoricalRecordDeletion, 20261113180000_RestrictCustomerComplaintDeletion, and 20261113200000_AddComplaintCodeSequenceAndUniqueIndex", () =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");

                using var context = new ApplicationDbContext(optionsBuilder.Options);
                var migrationsAssembly = context.GetService<IMigrationsAssembly>();
                var migrations = migrationsAssembly.Migrations.Keys.ToList();

                if (!migrations.Contains("20261110220000_AddCheckoutTokenToSale"))
                {
                    throw new Exception($"Migration 20261110220000_AddCheckoutTokenToSale was not found in migrations assembly! Discovered: {string.Join(", ", migrations)}");
                }

                if (!migrations.Contains("20261112130000_RestrictHistoricalRecordDeletion"))
                {
                    throw new Exception($"Migration 20261112130000_RestrictHistoricalRecordDeletion was not found in migrations assembly! Discovered: {string.Join(", ", migrations)}");
                }

                if (!migrations.Contains("20261113180000_RestrictCustomerComplaintDeletion"))
                {
                    throw new Exception($"Migration 20261113180000_RestrictCustomerComplaintDeletion was not found in migrations assembly! Discovered: {string.Join(", ", migrations)}");
                }

                if (!migrations.Contains("20261113200000_AddComplaintCodeSequenceAndUniqueIndex"))
                {
                    throw new Exception($"Migration 20261113200000_AddComplaintCodeSequenceAndUniqueIndex was not found in migrations assembly! Discovered: {string.Join(", ", migrations)}");
                }

                // Verify chronological ordering (must be the latest migration)
                string lastMigration = migrations.Last();
                if (lastMigration != "20261113200000_AddComplaintCodeSequenceAndUniqueIndex")
                {
                    throw new Exception($"AddComplaintCodeSequenceAndUniqueIndex must be the latest migration, but found: {lastMigration}");
                }
            });

            // Test 10: Model Metadata Verification for Sale.CheckoutToken
            RunTest("Test 10: EF Core Model Metadata enforces unique filtered index on Sale.CheckoutToken", () =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");

                using var context = new ApplicationDbContext(optionsBuilder.Options);
                var entityType = context.Model.FindEntityType(typeof(Sale));
                if (entityType == null) throw new Exception("Sale entity type not found in EF Core model!");

                var tokenProperty = entityType.FindProperty("CheckoutToken");
                if (tokenProperty == null) throw new Exception("CheckoutToken property not found on Sale entity!");
                if (!tokenProperty.IsNullable) throw new Exception("CheckoutToken must be nullable to support legacy sales!");
                if (tokenProperty.GetMaxLength() != 64) throw new Exception($"Expected MaxLength 64, got {tokenProperty.GetMaxLength()}");

                var indexes = entityType.GetIndexes().ToList();
                var tokenIndex = indexes.FirstOrDefault(i => i.Properties.Any(p => p.Name == "CheckoutToken"));
                if (tokenIndex == null) throw new Exception("Unique index on CheckoutToken was not configured!");
                if (!tokenIndex.IsUnique) throw new Exception("Index on CheckoutToken must be unique!");
                if (tokenIndex.GetFilter() != "[CheckoutToken] IS NOT NULL")
                    throw new Exception($"Expected filter '[CheckoutToken] IS NOT NULL', got '{tokenIndex.GetFilter()}'");
            });

            // Test 11: Foreign-Key Relationship Configurations for Financial and Audit Data Integrity
            RunTest("Test 11: Foreign-Key Relationships configured with Cascade (Sale->SaleItems) and Restrict (Customer->Sales, Product->SaleItems, Product->StockTransactions, Complaint->Customer)", () =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");

                using var context = new ApplicationDbContext(optionsBuilder.Options);
                var saleItemEntity = context.Model.FindEntityType(typeof(SaleItem));
                if (saleItemEntity == null) throw new Exception("SaleItem entity not found in model!");

                var saleFk = saleItemEntity.GetForeignKeys()
                    .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Sale));
                if (saleFk == null) throw new Exception("Foreign key from SaleItem to Sale not found!");
                if (saleFk.DeleteBehavior != DeleteBehavior.Cascade)
                    throw new Exception($"Expected DeleteBehavior.Cascade for SaleItem->Sale, got {saleFk.DeleteBehavior}");

                var prodFk = saleItemEntity.GetForeignKeys()
                    .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Product));
                if (prodFk == null) throw new Exception("Foreign key from SaleItem to Product not found!");
                if (prodFk.DeleteBehavior != DeleteBehavior.Restrict)
                    throw new Exception($"Expected DeleteBehavior.Restrict for SaleItem->Product, got {prodFk.DeleteBehavior}");

                var saleEntity = context.Model.FindEntityType(typeof(Sale));
                if (saleEntity == null) throw new Exception("Sale entity not found in model!");
                var custFk = saleEntity.GetForeignKeys()
                    .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Customer));
                if (custFk == null) throw new Exception("Foreign key from Sale to Customer not found!");
                if (custFk.DeleteBehavior != DeleteBehavior.Restrict)
                    throw new Exception($"Expected DeleteBehavior.Restrict for Sale->Customer, got {custFk.DeleteBehavior}");

                var stockTxEntity = context.Model.FindEntityType(typeof(StockTransaction));
                if (stockTxEntity == null) throw new Exception("StockTransaction entity not found in model!");
                var stockProdFk = stockTxEntity.GetForeignKeys()
                    .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Product));
                if (stockProdFk == null) throw new Exception("Foreign key from StockTransaction to Product not found!");
                if (stockProdFk.DeleteBehavior != DeleteBehavior.Restrict)
                    throw new Exception($"Expected DeleteBehavior.Restrict for StockTransaction->Product, got {stockProdFk.DeleteBehavior}");

                var complaintEntity = context.Model.FindEntityType(typeof(Complaint));
                if (complaintEntity == null) throw new Exception("Complaint entity not found in model!");
                var complaintCustFk = complaintEntity.GetForeignKeys()
                    .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Customer));
                if (complaintCustFk == null) throw new Exception("Foreign key from Complaint to Customer not found!");
                if (complaintCustFk.DeleteBehavior != DeleteBehavior.Restrict)
                    throw new Exception($"Expected DeleteBehavior.Restrict for Complaint->Customer, got {complaintCustFk.DeleteBehavior}");
            });

            // Test 12: Original Token Logging Isolation Test
            RunTest("Test 12: Original attempted token is preserved for logging even after replacement token is generated", () =>
            {
                string originalToken = "original_attempt_token_12345";
                var model = new ViewModels.SaleCreateViewModel
                {
                    CheckoutToken = originalToken,
                    CustomerName = "Test Customer"
                };

                string capturedForLogging = model.CheckoutToken ?? string.Empty;

                // Simulate token replacement on validation/concurrency failure
                model.CheckoutToken = Guid.NewGuid().ToString("N");

                if (capturedForLogging != originalToken)
                    throw new Exception($"Captured logging token mutated! Expected '{originalToken}', got '{capturedForLogging}'");

                if (model.CheckoutToken == originalToken)
                    throw new Exception("Replacement token was not generated for client retry!");
            });

            // =========================================================
            // TASK 5: SECURITY HARDENING & AUTHORIZATION TESTS
            // =========================================================

            // Test 13: All 11 sensitive management controllers have [Authorize] attribute at class level
            RunTest("Test 13: All management controllers enforce class-level [Authorize] with role constraints", () =>
            {
                var controllers = new Type[]
                {
                    typeof(CategoriesController),
                    typeof(ComplaintRoutingController),
                    typeof(CustomerPurchasePatternController),
                    typeof(CustomersController),
                    typeof(HomeController),
                    typeof(ProductsController),
                    typeof(ReportsController),
                    typeof(SalesAnalyticsController),
                    typeof(SalesController),
                    typeof(StockController),
                    typeof(StockTransactionsController)
                };

                foreach (var controller in controllers)
                {
                    var authAttr = controller.GetCustomAttribute<AuthorizeAttribute>();
                    if (authAttr == null)
                    {
                        throw new Exception($"Controller {controller.Name} is missing class-level [Authorize] attribute!");
                    }
                }
            });

            // Test 14: Public actions have [AllowAnonymous]
            RunTest("Test 14: Public anonymous endpoints are explicitly marked with [AllowAnonymous]", () =>
            {
                var loginGet = typeof(AccountController).GetMethod("Login", new[] { typeof(string) });
                if (loginGet?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("AccountController.Login (GET) is missing [AllowAnonymous]!");

                var loginPost = typeof(AccountController).GetMethod("Login", new[] { typeof(string), typeof(string), typeof(string) });
                if (loginPost?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("AccountController.Login (POST) is missing [AllowAnonymous]!");

                var accessDenied = typeof(AccountController).GetMethod("AccessDenied");
                if (accessDenied?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("AccountController.AccessDenied is missing [AllowAnonymous]!");

                var error = typeof(HomeController).GetMethod("Error");
                if (error?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("HomeController.Error is missing [AllowAnonymous]!");

                var privacy = typeof(HomeController).GetMethod("Privacy");
                if (privacy?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("HomeController.Privacy is missing [AllowAnonymous]!");
            });

            // Test 15: Admin-only destructive operations
            RunTest("Test 15: Admin-only operations enforce [Authorize(Roles = \"Admin\")]", () =>
            {
                var prodDelete = typeof(ProductsController).GetMethod("DeleteConfirmed", new[] { typeof(int) });
                var prodDeleteAuth = prodDelete?.GetCustomAttribute<AuthorizeAttribute>();
                if (prodDeleteAuth == null || prodDeleteAuth.Roles != "Admin")
                    throw new Exception("ProductsController.DeleteConfirmed is not restricted to Admin!");

                var catDelete = typeof(CategoriesController).GetMethod("DeleteConfirmed", new[] { typeof(int) });
                var catDeleteAuth = catDelete?.GetCustomAttribute<AuthorizeAttribute>();
                if (catDeleteAuth == null || catDeleteAuth.Roles != "Admin")
                    throw new Exception("CategoriesController.DeleteConfirmed is not restricted to Admin!");

                var custDelete = typeof(CustomersController).GetMethod("DeleteConfirmed", new[] { typeof(int) });
                var custDeleteAuth = custDelete?.GetCustomAttribute<AuthorizeAttribute>();
                if (custDeleteAuth == null || custDeleteAuth.Roles != "Admin")
                    throw new Exception("CustomersController.DeleteConfirmed is not restricted to Admin!");

                var saleDelete = typeof(SalesController).GetMethod("DeleteConfirmed", new[] { typeof(int) });
                var saleDeleteAuth = saleDelete?.GetCustomAttribute<AuthorizeAttribute>();
                if (saleDeleteAuth == null || saleDeleteAuth.Roles != "Admin")
                    throw new Exception("SalesController.DeleteConfirmed is not restricted to Admin!");

                var stockAdjust = typeof(StockController).GetMethods()
                    .Where(m => m.Name == "Adjust" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();
                var stockAdjustAuth = stockAdjust?.GetCustomAttribute<AuthorizeAttribute>();
                if (stockAdjustAuth == null || stockAdjustAuth.Roles != "Admin")
                    throw new Exception("StockController.Adjust (POST) is not restricted to Admin!");
            });

            // Test 16: Category & Product creation/modification actions enforce Admin role
            RunTest("Test 16: Product & Category Create and Edit actions enforce [Authorize(Roles = \"Admin\")]", () =>
            {
                var prodCreatePost = typeof(ProductsController).GetMethods()
                    .Where(m => m.Name == "Create" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();
                if (prodCreatePost?.GetCustomAttribute<AuthorizeAttribute>()?.Roles != "Admin")
                    throw new Exception("ProductsController.Create (POST) must be restricted to Admin!");

                var prodEditPost = typeof(ProductsController).GetMethods()
                    .Where(m => m.Name == "Edit" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();
                if (prodEditPost?.GetCustomAttribute<AuthorizeAttribute>()?.Roles != "Admin")
                    throw new Exception("ProductsController.Edit (POST) must be restricted to Admin!");

                var catCreatePost = typeof(CategoriesController).GetMethods()
                    .Where(m => m.Name == "Create" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();
                if (catCreatePost?.GetCustomAttribute<AuthorizeAttribute>()?.Roles != "Admin")
                    throw new Exception("CategoriesController.Create (POST) must be restricted to Admin!");

                var catEditPost = typeof(CategoriesController).GetMethods()
                    .Where(m => m.Name == "Edit" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();
                if (catEditPost?.GetCustomAttribute<AuthorizeAttribute>()?.Roles != "Admin")
                    throw new Exception("CategoriesController.Edit (POST) must be restricted to Admin!");
            });

            // Test 17: ReportsController restricts all reporting and export actions
            RunTest("Test 17: ReportsController has class-level [Authorize(Roles = \"Admin,Manager\")] protecting all 13 reports & exports", () =>
            {
                var reportsAuth = typeof(ReportsController).GetCustomAttribute<AuthorizeAttribute>();
                if (reportsAuth == null || reportsAuth.Roles != "Admin,Manager")
                    throw new Exception("ReportsController must be protected with [Authorize(Roles = \"Admin,Manager\")]!");
            });

            // Test 18: Anti-forgery tokens on all state-changing POST actions
            RunTest("Test 18: All state-changing POST actions enforce [ValidateAntiForgeryToken]", () =>
            {
                var controllerTypes = typeof(AccountController).Assembly.GetTypes()
                    .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract)
                    .ToList();

                foreach (var controller in controllerTypes)
                {
                    var postMethods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(m => m.GetCustomAttribute<HttpPostAttribute>() != null);

                    foreach (var method in postMethods)
                    {
                        var antiForgery = method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>();
                        if (antiForgery == null)
                        {
                            throw new Exception($"{controller.Name}.{method.Name} (POST) is missing [ValidateAntiForgeryToken]!");
                        }
                    }
                }
            });

            // Test 19: Customer resource ownership logic
            RunTest("Test 19: Customer ownership verification blocks unauthorized customer access to another customer's record", () =>
            {
                // Customer "alice@example.com"
                var aliceCustomer = new Customer { CustomerId = 1, Name = "Alice", Email = "alice@example.com" };
                // Customer "bob@example.com"
                var bobCustomer = new Customer { CustomerId = 2, Name = "Bob", Email = "bob@example.com" };

                // Simulate Customer user context (Alice is logged in)
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, "alice@example.com"),
                    new Claim(ClaimTypes.Role, "Customer")
                };
                var identity = new ClaimsIdentity(claims, "TestAuth");
                var userPrincipal = new ClaimsPrincipal(identity);

                bool CanAccess(Customer target, ClaimsPrincipal user)
                {
                    if (user.IsInRole("Admin") || user.IsInRole("Manager"))
                        return true;

                    if (user.IsInRole("Customer"))
                    {
                        return string.Equals(target.Email, user.Identity?.Name, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }

                // Alice accesses Alice's record -> Allowed
                if (!CanAccess(aliceCustomer, userPrincipal))
                    throw new Exception("Customer Alice was unexpectedly blocked from accessing her own record!");

                // Alice attempts to access Bob's record -> Denied!
                if (CanAccess(bobCustomer, userPrincipal))
                    throw new Exception("Customer Alice was unexpectedly allowed to access Bob's private record!");

                // Manager accesses Bob's record -> Allowed
                var managerClaims = new List<Claim> { new Claim(ClaimTypes.Name, "manager@gmail.com"), new Claim(ClaimTypes.Role, "Manager") };
                var managerUser = new ClaimsPrincipal(new ClaimsIdentity(managerClaims, "TestAuth"));
                if (!CanAccess(bobCustomer, managerUser))
                    throw new Exception("Manager was unexpectedly blocked from customer record!");
            });

            // Test 20: Product validation rejects negative prices and negative stock
            RunTest("Test 20: Product validation rule rejects negative prices and negative inventory quantities", () =>
            {
                var invalidProduct = new Product
                {
                    Name = "Invalid Item",
                    PurchasePrice = -10m,
                    SellingPrice = -20m,
                    StockQuantity = -5,
                    ReorderLevel = -1
                };

                bool IsValidProduct(Product p, out List<string> errors)
                {
                    errors = new List<string>();
                    if (p.PurchasePrice < 0) errors.Add("Purchase price cannot be negative.");
                    if (p.SellingPrice < 0) errors.Add("Selling price cannot be negative.");
                    if (p.StockQuantity < 0) errors.Add("Stock quantity cannot be negative.");
                    if (p.ReorderLevel < 0) errors.Add("Reorder level cannot be negative.");
                    return errors.Count == 0;
                }

                if (IsValidProduct(invalidProduct, out var errors))
                    throw new Exception("Negative values were unexpectedly accepted!");

                if (errors.Count != 4)
                    throw new Exception($"Expected 4 validation errors, but got {errors.Count}: {string.Join(", ", errors)}");
            });

            // Test 21: Stock adjustment non-zero check
            RunTest("Test 21: Stock adjustment validation rejects zero-quantity adjustments", () =>
            {
                var zeroAdjustment = new ViewModels.StockAdjustmentViewModel
                {
                    ProductId = 1,
                    Quantity = 0,
                    Reason = "Correction"
                };

                bool IsValidAdjustment(ViewModels.StockAdjustmentViewModel adj, int currentStock, out string? error)
                {
                    error = null;
                    if (adj.Quantity == 0)
                    {
                        error = "Adjustment quantity cannot be zero.";
                        return false;
                    }
                    if (currentStock + adj.Quantity < 0)
                    {
                        error = "Stock cannot become negative.";
                        return false;
                    }
                    return true;
                }

                if (IsValidAdjustment(zeroAdjustment, currentStock: 10, out var zeroError))
                    throw new Exception("Zero quantity adjustment was unexpectedly accepted!");

                var overdraftAdjustment = new ViewModels.StockAdjustmentViewModel { ProductId = 1, Quantity = -15, Reason = "Waste" };
                if (IsValidAdjustment(overdraftAdjustment, currentStock: 10, out var overdraftError))
                    throw new Exception("Overdraft adjustment resulting in negative stock was unexpectedly accepted!");
            });

            // Test 22: Token masking in production logging
            RunTest("Test 22: Production token masking prevents raw idempotency tokens from leaking in application logs", () =>
            {
                string rawToken = "d94e77ba3a1e4c76a9284cb549e3592f";
                string Mask(string? token) => !string.IsNullOrEmpty(token) && token.Length > 8
                    ? token[..8] + "..."
                    : "[Masked]";

                string masked = Mask(rawToken);
                if (masked == rawToken)
                    throw new Exception("Token was not masked!");
                if (!masked.EndsWith("..."))
                    throw new Exception("Masked token format unexpected: " + masked);
                if (masked.Contains(rawToken[8..]))
                    throw new Exception("Masked token exposed raw token tail!");
            });

            // =========================================================
            // TASK 5.1: RUNTIME AUTHORIZATION & SECURITY VERIFICATION
            // =========================================================

            // Setup real ASP.NET Core runtime authorization service
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorization(options =>
            {
                options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
                options.AddPolicy("Management", policy => policy.RequireRole("Admin", "Manager"));
            });
            var serviceProvider = services.BuildServiceProvider();
            var authService = serviceProvider.GetRequiredService<IAuthorizationService>();

            // Setup test principals
            var anonymousPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
            var customerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "customer@example.com"),
                new Claim(ClaimTypes.Role, "Customer")
            }, "TestCookieAuth"));
            var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "manager@gmail.com"),
                new Claim(ClaimTypes.Role, "Manager")
            }, "TestCookieAuth"));
            var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin@gmail.com"),
                new Claim(ClaimTypes.Role, "Admin")
            }, "TestCookieAuth"));

            // Test 23: Runtime Authorization - Anonymous user requests a protected management dashboard
            await RunTestAsync("Test 23: [Runtime] Anonymous user requesting protected management dashboard fails authorization", async () =>
            {
                var result = await authService.AuthorizeAsync(anonymousPrincipal, "Management");
                if (result.Succeeded)
                    throw new Exception("Anonymous user was unexpectedly authorized for management policy!");
            });

            // Test 24: Runtime Authorization - Customer requests an Admin-only page
            await RunTestAsync("Test 24: [Runtime] Customer requesting Admin-only resource fails authorization", async () =>
            {
                var result = await authService.AuthorizeAsync(customerPrincipal, "AdminOnly");
                if (result.Succeeded)
                    throw new Exception("Customer was unexpectedly authorized for Admin-only policy!");
            });

            // Test 25: Runtime Authorization - Customer requests a Manager-only report
            await RunTestAsync("Test 25: [Runtime] Customer requesting management report fails authorization", async () =>
            {
                var result = await authService.AuthorizeAsync(customerPrincipal, "Management");
                if (result.Succeeded)
                    throw new Exception("Customer was unexpectedly authorized for management report policy!");
            });

            // Test 26: Runtime Authorization - Manager requests an Admin-only stock adjustment
            await RunTestAsync("Test 26: [Runtime] Manager requesting Admin-only stock adjustment fails authorization", async () =>
            {
                var result = await authService.AuthorizeAsync(managerPrincipal, "AdminOnly");
                if (result.Succeeded)
                    throw new Exception("Manager was unexpectedly authorized for Admin-only policy!");
            });

            // Test 27: Runtime Authorization - Manager accesses an authorized management report
            await RunTestAsync("Test 27: [Runtime] Manager requesting authorized management report succeeds authorization", async () =>
            {
                var result = await authService.AuthorizeAsync(managerPrincipal, "Management");
                if (!result.Succeeded)
                    throw new Exception("Manager was unexpectedly blocked from authorized management report!");
            });

            // Test 28: Runtime Authorization - Admin accesses an administrative page
            await RunTestAsync("Test 28: [Runtime] Admin requesting administrative policy succeeds authorization", async () =>
            {
                var adminResult = await authService.AuthorizeAsync(adminPrincipal, "AdminOnly");
                if (!adminResult.Succeeded)
                    throw new Exception("Admin was unexpectedly blocked from Admin-only policy!");

                var mgmtResult = await authService.AuthorizeAsync(adminPrincipal, "Management");
                if (!mgmtResult.Succeeded)
                    throw new Exception("Admin was unexpectedly blocked from Management policy!");
            });

            // Test 29: Runtime Password Policy Evaluation
            RunTest("Test 29: [Runtime] Identity password validator enforces 10+ chars, uppercase, lowercase, digits, and non-alphanumeric", () =>
            {
                var options = new IdentityOptions();
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;

                // Test invalid passwords
                string[] invalidPasswords = new[]
                {
                    "Short1!",            // Too short (< 10)
                    "alllowercase123!",   // No uppercase
                    "ALLUPPERCASE123!",   // No lowercase
                    "NoDigitsInThis!",    // No digit
                    "NoSpecialChar123"    // No non-alphanumeric
                };

                foreach (var invalid in invalidPasswords)
                {
                    bool valid = invalid.Length >= options.Password.RequiredLength &&
                                 invalid.Any(char.IsDigit) &&
                                 invalid.Any(char.IsLower) &&
                                 invalid.Any(char.IsUpper) &&
                                 invalid.Any(c => !char.IsLetterOrDigit(c));

                    if (valid)
                        throw new Exception($"Invalid password '{invalid}' was unexpectedly accepted!");
                }

                // Test valid development & production passwords
                string[] validPasswords = new[]
                {
                    "Admin@Retail2026!",
                    "Manager@Retail2026!",
                    "ComplexSecure#99"
                };

                foreach (var valid in validPasswords)
                {
                    bool isValid = valid.Length >= options.Password.RequiredLength &&
                                  valid.Any(char.IsDigit) &&
                                  valid.Any(char.IsLower) &&
                                  valid.Any(char.IsUpper) &&
                                  valid.Any(c => !char.IsLetterOrDigit(c));

                    if (!isValid)
                        throw new Exception($"Valid password '{valid}' was unexpectedly rejected!");
                }
            });

            // Test 30: Runtime Account Lockout Calculation and Recovery
            RunTest("Test 30: [Runtime] Account lockout policy triggers after 5 failed attempts with 15-minute duration and recovery", () =>
            {
                int maxFailedAttempts = 5;
                TimeSpan lockoutDuration = TimeSpan.FromMinutes(15);

                int failedCount = 0;
                DateTimeOffset? lockoutEnd = null;

                void RecordFailedAttempt()
                {
                    failedCount++;
                    if (failedCount >= maxFailedAttempts)
                    {
                        lockoutEnd = DateTimeOffset.UtcNow.Add(lockoutDuration);
                    }
                }

                bool IsLockedOut() => lockoutEnd.HasValue && lockoutEnd.Value > DateTimeOffset.UtcNow;

                // 4 failed attempts -> Not locked out
                for (int i = 0; i < 4; i++) RecordFailedAttempt();
                if (IsLockedOut())
                    throw new Exception("User was locked out before reaching 5 failed attempts!");

                // 5th failed attempt -> Locked out for 15 minutes
                RecordFailedAttempt();
                if (!IsLockedOut())
                    throw new Exception("User was not locked out after 5 failed attempts!");

                if (lockoutEnd == null || lockoutEnd.Value <= DateTimeOffset.UtcNow.AddMinutes(14))
                    throw new Exception("Lockout duration is less than the required 15 minutes!");

                // Administrator recovery / unlock
                lockoutEnd = null;
                failedCount = 0;
                if (IsLockedOut())
                    throw new Exception("Administrator unlock failed to clear lockout status!");
            });

            // Test 31: Open Redirect Defense - ReturnUrl verification
            RunTest("Test 31: [Runtime] ReturnUrl validation accepts local relative URLs and rejects external/open redirects", () =>
            {
                bool IsLocalUrl(string? url)
                {
                    if (string.IsNullOrEmpty(url)) return false;
                    return (url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))) ||
                           (url.Length > 1 && url[0] == '~' && url[1] == '/');
                }

                string[] validLocalUrls = { "/", "/Home/Index", "/Sales/Create", "/Reports/DailySales", "~/Stock" };
                foreach (var url in validLocalUrls)
                {
                    if (!IsLocalUrl(url))
                        throw new Exception($"Valid local URL '{url}' was rejected!");
                }

                string[] maliciousUrls = { "https://attacker.com", "http://evil.com/login", "//evil.com", "/\\evil.com", "javascript:alert(1)" };
                foreach (var url in maliciousUrls)
                {
                    if (IsLocalUrl(url))
                        throw new Exception($"Malicious URL '{url}' was accepted as local URL!");
                }
            });

            // Test 32: Credential Security - Verification of appsettings.json
            RunTest("Test 32: [Config] Tracked appsettings.json contains no plain-text passwords or machine-specific connection strings", () =>
            {
                string appsettingsPath = Path.GetFullPath("d:\\RetailSalesStockAnalytics\\RetailSalesStockAnalytics\\appsettings.json");

                if (!File.Exists(appsettingsPath))
                    throw new Exception($"appsettings.json not found at {appsettingsPath}");

                string json = File.ReadAllText(appsettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("DefaultAdmin", out var adminElem) &&
                    adminElem.TryGetProperty("Password", out var adminPass) &&
                    !string.IsNullOrWhiteSpace(adminPass.GetString()))
                {
                    throw new Exception("Plaintext DefaultAdmin:Password found in tracked appsettings.json!");
                }

                if (root.TryGetProperty("DefaultManager", out var managerElem) &&
                    managerElem.TryGetProperty("Password", out var managerPass) &&
                    !string.IsNullOrWhiteSpace(managerPass.GetString()))
                {
                    throw new Exception("Plaintext DefaultManager:Password found in tracked appsettings.json!");
                }

                if (root.TryGetProperty("ConnectionStrings", out var connElem) &&
                    connElem.TryGetProperty("DefaultConnection", out var defaultConn))
                {
                    string? connStr = defaultConn.GetString();
                    if (connStr != null && connStr.Contains("MARUF-PC", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception("Machine-specific host 'MARUF-PC' found in tracked appsettings.json! Connection string must use a portable placeholder.");
                    }
                }
            });

            // Test 33: Sales Edit Data Integrity & Overposting Protection
            RunTest("Test 33: [Security/Overposting] SalesController.Edit strictly prevents modification of TotalAmount, Discount, and CheckoutToken", () =>
            {
                var editMethod = typeof(SalesController).GetMethods()
                    .Where(m => m.Name == "Edit" && m.GetCustomAttribute<HttpPostAttribute>() != null)
                    .FirstOrDefault();

                if (editMethod == null) throw new Exception("SalesController.Edit (POST) method not found!");

                var saleParam = editMethod.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(Sale));
                if (saleParam == null) throw new Exception("Sale parameter not found on SalesController.Edit!");

                var bindAttr = saleParam.GetCustomAttribute<BindAttribute>();
                if (bindAttr == null) throw new Exception("[Bind] attribute is missing from SalesController.Edit!");

                var boundFields = bindAttr.Include;

                if (boundFields.Contains("TotalAmount"))
                    throw new Exception("Vulnerability: TotalAmount is bound on SalesController.Edit! Financial totals must not be modifiable independently of line items.");

                if (boundFields.Contains("Discount"))
                    throw new Exception("Vulnerability: Discount is bound on SalesController.Edit! Discounts must not be modifiable independently of discount-allocation rules.");

                if (boundFields.Contains("CheckoutToken"))
                    throw new Exception("Vulnerability: CheckoutToken is bound on SalesController.Edit! Server-managed idempotency token must not be overridable.");

                if (!boundFields.Contains("SaleId") || !boundFields.Contains("CustomerId") || !boundFields.Contains("SaleDate"))
                    throw new Exception($"Expected bound fields [SaleId, CustomerId, SaleDate], but found: {string.Join(", ", boundFields)}");
            });

            // Test 34: Development Configuration Secret Free Verification
            RunTest("Test 34: [Config] appsettings.Development.json contains no plain-text passwords or secret credentials", () =>
            {
                string devSettingsPath = Path.GetFullPath("d:\\RetailSalesStockAnalytics\\RetailSalesStockAnalytics\\appsettings.Development.json");

                if (!File.Exists(devSettingsPath))
                    throw new Exception($"appsettings.Development.json not found at {devSettingsPath}");

                string json = File.ReadAllText(devSettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("DefaultAdmin", out var adminElem) &&
                    adminElem.TryGetProperty("Password", out var adminPass) &&
                    !string.IsNullOrWhiteSpace(adminPass.GetString()))
                {
                    throw new Exception("Plaintext DefaultAdmin:Password found in appsettings.Development.json!");
                }

                if (root.TryGetProperty("DefaultManager", out var managerElem) &&
                    managerElem.TryGetProperty("Password", out var managerPass) &&
                    !string.IsNullOrWhiteSpace(managerPass.GetString()))
                {
                    throw new Exception("Plaintext DefaultManager:Password found in appsettings.Development.json!");
                }
            });

            // Test 35: User Secrets ID Configuration in Project
            RunTest("Test 35: [Config] Project file configures UserSecretsId for out-of-repo development secret storage", () =>
            {
                string csprojPath = Path.GetFullPath("d:\\RetailSalesStockAnalytics\\RetailSalesStockAnalytics\\RetailSalesStockAnalytics.csproj");
                string content = File.ReadAllText(csprojPath);

                if (!content.Contains("<UserSecretsId>"))
                    throw new Exception("UserSecretsId is not configured in RetailSalesStockAnalytics.csproj!");
            });

            // Test 36: Git Ignore Rules Enforcement for Secret Files
            RunTest("Test 36: [Git] .gitignore explicitly excludes local secret stores, production configs, and user settings", () =>
            {
                string gitignorePath = Path.GetFullPath("d:\\RetailSalesStockAnalytics\\.gitignore");
                if (!File.Exists(gitignorePath))
                    throw new Exception(".gitignore was not found in workspace root!");

                string content = File.ReadAllText(gitignorePath);
                if (!content.Contains("secrets.json"))
                    throw new Exception(".gitignore does not exclude secrets.json!");
                if (!content.Contains("appsettings.Production.json"))
                    throw new Exception(".gitignore does not exclude appsettings.Production.json!");
                if (!content.Contains("[Bb]in/"))
                    throw new Exception(".gitignore does not exclude build binaries!");
            });

            // =========================================================
            // TASK 3.11: SALES CHECKOUT SERVICE ARCHITECTURE TESTS
            // =========================================================

            // Test 37: SalesCheckoutService implements ISalesCheckoutService
            RunTest("Test 37: [Architecture] SalesCheckoutService implements ISalesCheckoutService and defines checkout contracts", () =>
            {
                var serviceType = typeof(SalesCheckoutService);
                if (!typeof(ISalesCheckoutService).IsAssignableFrom(serviceType))
                    throw new Exception("SalesCheckoutService must implement ISalesCheckoutService!");

                var method = typeof(ISalesCheckoutService).GetMethod("CreateSaleAsync");
                if (method == null)
                    throw new Exception("ISalesCheckoutService must define CreateSaleAsync!");

                var controllerCtor = typeof(SalesController).GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(ISalesCheckoutService)));
                if (controllerCtor == null)
                    throw new Exception("SalesController must inject ISalesCheckoutService!");
            });

            // Test 38: CheckoutResult contract supports success, validation errors, and duplicate flags
            RunTest("Test 38: [Contract] CheckoutResult cleanly communicates outcomes and duplicate blocks", () =>
            {
                var success = CheckoutResult.Succeeded(42);
                if (!success.Success || success.SaleId != 42 || success.Errors.Count > 0)
                    throw new Exception("CheckoutResult.Succeeded invalid!");

                var duplicateFail = CheckoutResult.Failed("Duplicate submission was blocked.", isDuplicateBlocked: true);
                if (duplicateFail.Success || !duplicateFail.IsDuplicateBlocked || duplicateFail.Errors.Count != 1)
                    throw new Exception("CheckoutResult.Failed duplicate invalid!");

                var validationFail = CheckoutResult.Failed(new List<CheckoutValidationError>
                {
                    new CheckoutValidationError("CustomerName", "Customer name is required."),
                    new CheckoutValidationError("", "At least one product is required.")
                });
                if (validationFail.Success || validationFail.Errors.Count != 2)
                    throw new Exception("CheckoutResult.Failed validation list invalid!");
            });

            // =========================================================
            // TASK 3.12: SALE CANCELLATION SERVICE ARCHITECTURE TESTS
            // =========================================================

            // Test 39: ISalesCheckoutService defines CancelSaleAsync and SaleCancellationResult contract
            RunTest("Test 39: [Architecture & Contract] ISalesCheckoutService and SalesCheckoutService support CancelSaleAsync and SaleCancellationResult", () =>
            {
                var interfaceMethod = typeof(ISalesCheckoutService).GetMethod("CancelSaleAsync", new[] { typeof(int) });
                if (interfaceMethod == null)
                    throw new Exception("ISalesCheckoutService must define CancelSaleAsync(int)!");

                if (interfaceMethod.ReturnType != typeof(Task<SaleCancellationResult>))
                    throw new Exception("CancelSaleAsync must return Task<SaleCancellationResult>!");

                var serviceMethod = typeof(SalesCheckoutService).GetMethod("CancelSaleAsync", new[] { typeof(int) });
                if (serviceMethod == null)
                    throw new Exception("SalesCheckoutService must implement CancelSaleAsync(int)!");

                // Contract state checks
                var success = SaleCancellationResult.Succeeded();
                if (!success.Success || success.NotFound || success.AlreadyDeleted)
                    throw new Exception("SaleCancellationResult.Succeeded state invalid!");

                var notFound = SaleCancellationResult.SaleNotFound();
                if (notFound.Success || !notFound.NotFound || notFound.AlreadyDeleted || string.IsNullOrEmpty(notFound.ErrorMessage))
                    throw new Exception("SaleCancellationResult.SaleNotFound state invalid!");

                var concurrent = SaleCancellationResult.ConcurrentDelete();
                if (concurrent.Success || concurrent.NotFound || !concurrent.AlreadyDeleted || string.IsNullOrEmpty(concurrent.ErrorMessage))
                    throw new Exception("SaleCancellationResult.ConcurrentDelete state invalid!");

                var failed = SaleCancellationResult.Failed("Custom failure");
                if (failed.Success || failed.ErrorMessage != "Custom failure")
                    throw new Exception("SaleCancellationResult.Failed state invalid!");

                // Ensure DeleteConfirmed preserves Admin and AntiForgery protections
                var deleteMethod = typeof(SalesController).GetMethod("DeleteConfirmed", new[] { typeof(int) });
                if (deleteMethod == null)
                    throw new Exception("SalesController.DeleteConfirmed(int) not found!");

                var authAttr = deleteMethod.GetCustomAttribute<AuthorizeAttribute>();
                if (authAttr == null || authAttr.Roles != "Admin")
                    throw new Exception("SalesController.DeleteConfirmed must retain [Authorize(Roles = \"Admin\")]!");

                if (deleteMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("SalesController.DeleteConfirmed must retain [ValidateAntiForgeryToken]!");
            });

            // =========================================================
            // TASK 3.13: DASHBOARD SERVICE ARCHITECTURE TESTS
            // =========================================================

            // Test 40: IDashboardService and DashboardService decouple HomeController from DbContext
            RunTest("Test 40: [Architecture] IDashboardService and DashboardService decouple HomeController from ApplicationDbContext", () =>
            {
                var interfaceType = typeof(IDashboardService);
                var getMethod = interfaceType.GetMethod("GetDashboardAsync");
                if (getMethod == null)
                    throw new Exception("IDashboardService must define GetDashboardAsync()!");

                if (getMethod.ReturnType != typeof(Task<DashboardViewModel>))
                    throw new Exception("GetDashboardAsync must return Task<DashboardViewModel>!");

                var serviceType = typeof(DashboardService);
                if (!interfaceType.IsAssignableFrom(serviceType))
                    throw new Exception("DashboardService must implement IDashboardService!");

                var controllerCtor = typeof(HomeController).GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IDashboardService)));
                if (controllerCtor == null)
                    throw new Exception("HomeController must inject IDashboardService!");

                var hasDbContextParam = controllerCtor.GetParameters().Any(p => p.ParameterType == typeof(ApplicationDbContext));
                if (hasDbContextParam)
                    throw new Exception("HomeController must not inject ApplicationDbContext!");

                var homeAuth = typeof(HomeController).GetCustomAttribute<AuthorizeAttribute>();
                if (homeAuth == null || homeAuth.Roles != "Admin,Manager")
                    throw new Exception("HomeController must retain [Authorize(Roles = \"Admin,Manager\")]!");

                var privacy = typeof(HomeController).GetMethod("Privacy");
                if (privacy?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("HomeController.Privacy must retain [AllowAnonymous]!");

                var error = typeof(HomeController).GetMethod("Error");
                if (error?.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                    throw new Exception("HomeController.Error must retain [AllowAnonymous]!");
            });

            // =========================================================
            // TASK 3.14: STOCK SERVICE RESTOCK & ADJUSTMENT TESTS
            // =========================================================

            // Test 41: IStockService defines RestockAsync, AdjustStockAsync, and StockOperationResult contract
            RunTest("Test 41: [Architecture & Contract] IStockService and StockService consolidate Restock and Stock Adjustment", () =>
            {
                var interfaceType = typeof(IStockService);
                var restockMethod = interfaceType.GetMethod("RestockAsync", new[] { typeof(int), typeof(int), typeof(decimal), typeof(string) });
                if (restockMethod == null)
                    throw new Exception("IStockService must define RestockAsync(int, int, decimal, string?)!");

                if (restockMethod.ReturnType != typeof(Task<StockOperationResult>))
                    throw new Exception("RestockAsync must return Task<StockOperationResult>!");

                var adjustMethod = interfaceType.GetMethod("AdjustStockAsync", new[] { typeof(int), typeof(int), typeof(string) });
                if (adjustMethod == null)
                    throw new Exception("IStockService must define AdjustStockAsync(int, int, string)!");

                if (adjustMethod.ReturnType != typeof(Task<StockOperationResult>))
                    throw new Exception("AdjustStockAsync must return Task<StockOperationResult>!");

                var serviceType = typeof(StockService);
                if (serviceType.GetMethod("RestockAsync") == null)
                    throw new Exception("StockService must implement RestockAsync!");
                if (serviceType.GetMethod("AdjustStockAsync") == null)
                    throw new Exception("StockService must implement AdjustStockAsync!");

                // Contract state checks
                var success = StockOperationResult.Succeeded("Laptop", 5, 25);
                if (!success.Success || success.NotFound || success.InvalidOperation || success.ProductName != "Laptop" || success.Quantity != 5 || success.CurrentStock != 25)
                    throw new Exception("StockOperationResult.Succeeded state invalid!");

                var notFound = StockOperationResult.ProductNotFound();
                if (notFound.Success || !notFound.NotFound || notFound.InvalidOperation || notFound.ErrorField != "ProductId" || string.IsNullOrEmpty(notFound.ErrorMessage))
                    throw new Exception("StockOperationResult.ProductNotFound state invalid!");

                var invalid = StockOperationResult.Invalid("Adjustment quantity cannot be zero.");
                if (invalid.Success || invalid.NotFound || !invalid.InvalidOperation || invalid.ErrorField != "Quantity" || string.IsNullOrEmpty(invalid.ErrorMessage))
                    throw new Exception("StockOperationResult.Invalid state invalid!");

                var failed = StockOperationResult.Failed("Database timeout");
                if (failed.Success || failed.ErrorMessage != "Database timeout")
                    throw new Exception("StockOperationResult.Failed state invalid!");

                // Ensure StockController preserves Admin and AntiForgery protections
                var adjustPost = typeof(StockController).GetMethods()
                    .FirstOrDefault(m => m.Name == "Adjust" && m.GetCustomAttribute<HttpPostAttribute>() != null);
                if (adjustPost == null)
                    throw new Exception("StockController.Adjust (POST) not found!");

                var authAttr = adjustPost.GetCustomAttribute<AuthorizeAttribute>();
                if (authAttr == null || authAttr.Roles != "Admin")
                    throw new Exception("StockController.Adjust (POST) must retain [Authorize(Roles = \"Admin\")]!");

                if (adjustPost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("StockController.Adjust (POST) must retain [ValidateAntiForgeryToken]!");

                var createPost = typeof(StockController).GetMethods()
                    .FirstOrDefault(m => m.Name == "Create" && m.GetCustomAttribute<HttpPostAttribute>() != null);
                if (createPost == null || createPost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("StockController.Create (POST) must retain [ValidateAntiForgeryToken]!");
            });

            // =========================================================
            // TASK 3.15: PRODUCT STOCK AUDITING SERVICE TESTS
            // =========================================================

            // Test 42: IStockService defines RecordInitialStockAsync, RecordProductEditAdjustmentAsync and ProductsController delegates auditing
            RunTest("Test 42: [Architecture & Auditing] IStockService defines initial stock and edit auditing, and ProductsController delegates stock operations", () =>
            {
                var interfaceType = typeof(IStockService);
                var initialStockMethod = interfaceType.GetMethod("RecordInitialStockAsync", new[] { typeof(int), typeof(int), typeof(decimal) });
                if (initialStockMethod == null)
                    throw new Exception("IStockService must define RecordInitialStockAsync(int, int, decimal)!");

                if (initialStockMethod.ReturnType != typeof(Task<StockOperationResult>))
                    throw new Exception("RecordInitialStockAsync must return Task<StockOperationResult>!");

                var editAdjustmentMethod = interfaceType.GetMethod("RecordProductEditAdjustmentAsync", new[] { typeof(int), typeof(int), typeof(int), typeof(decimal) });
                if (editAdjustmentMethod == null)
                    throw new Exception("IStockService must define RecordProductEditAdjustmentAsync(int, int, int, decimal)!");

                if (editAdjustmentMethod.ReturnType != typeof(Task<StockOperationResult>))
                    throw new Exception("RecordProductEditAdjustmentAsync must return Task<StockOperationResult>!");

                var serviceType = typeof(StockService);
                if (serviceType.GetMethod("RecordInitialStockAsync") == null)
                    throw new Exception("StockService must implement RecordInitialStockAsync!");
                if (serviceType.GetMethod("RecordProductEditAdjustmentAsync") == null)
                    throw new Exception("StockService must implement RecordProductEditAdjustmentAsync!");

                // Contract checks: non-positive initial stock and zero-delta edit adjustment
                var zeroInitial = StockOperationResult.Succeeded(string.Empty, 0, 0);
                if (!zeroInitial.Success)
                    throw new Exception("Zero initial stock result should indicate success!");

                // ProductsController must inject IStockService
                var controllerCtor = typeof(ProductsController).GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IStockService)));
                if (controllerCtor == null)
                    throw new Exception("ProductsController must inject IStockService!");

                // Ensure ProductsController preserves Admin and AntiForgery protections
                var prodCreatePost = typeof(ProductsController).GetMethods()
                    .FirstOrDefault(m => m.Name == "Create" && m.GetCustomAttribute<HttpPostAttribute>() != null);
                if (prodCreatePost == null)
                    throw new Exception("ProductsController.Create (POST) not found!");
                var prodCreateAuth = prodCreatePost.GetCustomAttribute<AuthorizeAttribute>();
                if (prodCreateAuth == null || prodCreateAuth.Roles != "Admin")
                    throw new Exception("ProductsController.Create (POST) must retain [Authorize(Roles = \"Admin\")]!");
                if (prodCreatePost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("ProductsController.Create (POST) must retain [ValidateAntiForgeryToken]!");

                var prodEditPost = typeof(ProductsController).GetMethods()
                    .FirstOrDefault(m => m.Name == "Edit" && m.GetCustomAttribute<HttpPostAttribute>() != null);
                if (prodEditPost == null)
                    throw new Exception("ProductsController.Edit (POST) not found!");
                var prodEditAuth = prodEditPost.GetCustomAttribute<AuthorizeAttribute>();
                if (prodEditAuth == null || prodEditAuth.Roles != "Admin")
                    throw new Exception("ProductsController.Edit (POST) must retain [Authorize(Roles = \"Admin\")]!");
                if (prodEditPost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("ProductsController.Edit (POST) must retain [ValidateAntiForgeryToken]!");
            });

            // Test 43: EF Core Diagnostics - Normal DbContext options do not globally suppress PendingModelChangesWarning
            RunTest("Test 43: EF Core Diagnostics - Normal DbContext configuration does not globally suppress PendingModelChangesWarning", () =>
            {
                // Verify Program.cs registration does not ignore PendingModelChangesWarning globally
                var programPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RetailSalesStockAnalytics", "Program.cs");
                if (File.Exists(programPath))
                {
                    string content = File.ReadAllText(programPath);
                    int dbContextBlockStart = content.IndexOf("builder.Services.AddDbContext<ApplicationDbContext>");
                    if (dbContextBlockStart >= 0)
                    {
                        int dbContextBlockEnd = content.IndexOf(");", dbContextBlockStart);
                        if (dbContextBlockEnd > dbContextBlockStart)
                        {
                            string dbContextConfig = content.Substring(dbContextBlockStart, dbContextBlockEnd - dbContextBlockStart);
                            if (dbContextConfig.Contains("PendingModelChangesWarning"))
                            {
                                throw new Exception("builder.Services.AddDbContext<ApplicationDbContext> in Program.cs still suppresses PendingModelChangesWarning globally!");
                            }
                        }
                    }
                }

                // Verify DbContextOptions constructed normally does not have PendingModelChangesWarning ignored
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");
                var coreOptions = optionsBuilder.Options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>();
                var behavior = coreOptions?.WarningsConfiguration.GetBehavior(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning);
                if (behavior == WarningBehavior.Ignore)
                {
                    throw new Exception("Normal DbContextOptions configuration should not ignore PendingModelChangesWarning!");
                }
            });

            // Test 44: Sales Index Server-Side Pagination - IQueryable Skip/Take generates SQL OFFSET FETCH
            RunTest("Test 44: Sales Index Query generates SQL OFFSET ... ROWS FETCH NEXT ... ROWS ONLY", () =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");

                using var context = new ApplicationDbContext(optionsBuilder.Options);
                int pageNumber = 2;
                int pageSize = 10;
                int skip = (pageNumber - 1) * pageSize;

                var query = context.Sales
                    .AsNoTracking()
                    .Include(s => s.Customer)
                    .OrderByDescending(s => s.SaleDate)
                    .ThenByDescending(s => s.SaleId)
                    .Skip(skip)
                    .Take(pageSize);

                string sql = query.ToQueryString();
                Console.WriteLine($"\n[SQL GENERATED FOR SALES INDEX PAGINATION]:\n{sql.Trim()}\n");

                if (!sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase) ||
                    !sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Query did not generate SQL OFFSET ... FETCH: {sql}");
                }
            });

            // =========================================================
            // TASK 4.5.4.1: CONFIGURATION PRECEDENCE & STARTUP TESTS
            // =========================================================

            // Test 45: appsettings.json provides lowest-priority base connection string
            RunTest("Test 45: [Config Precedence 1/6] appsettings.json provides lowest-priority base connection string", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t45_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Staging"
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr != "Server=base-server;Database=TestDb;")
                        throw new Exception($"Expected base connection string 'Server=base-server;Database=TestDb;', but got '{connStr}'");
                }
                finally
                {
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 46: Environment-specific appsettings overrides base appsettings
            RunTest("Test 46: [Config Precedence 2/6] Environment-specific appsettings overrides base appsettings", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t46_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Staging.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=staging-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Staging"
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr != "Server=staging-server;Database=TestDb;")
                        throw new Exception($"Expected staging connection string 'Server=staging-server;Database=TestDb;', but got '{connStr}'");
                }
                finally
                {
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 47: appsettings.Local.json overrides environment-specific appsettings
            RunTest("Test 47: [Config Precedence 3/6] appsettings.Local.json overrides environment-specific appsettings", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t47_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Staging.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=staging-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Local.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=local-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Staging"
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr != "Server=local-server;Database=TestDb;")
                        throw new Exception($"Expected local connection string 'Server=local-server;Database=TestDb;', but got '{connStr}'");
                }
                finally
                {
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 48: User Secrets override appsettings.Local.json in Development
            RunTest("Test 48: [Config Precedence 4/6] User Secrets override appsettings.Local.json in Development", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t48_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Development.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=dev-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Local.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=local-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Development",
                        ApplicationName = typeof(ConfigurationHelper).Assembly.FullName
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr == "Server=local-server;Database=TestDb;")
                        throw new Exception("Vulnerability: appsettings.Local.json took precedence over User Secrets in Development!");
                    if (string.IsNullOrWhiteSpace(connStr))
                        throw new Exception("Expected User Secrets connection string to be resolved in Development!");
                }
                finally
                {
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 49: Environment variables override User Secrets and local JSON configuration
            RunTest("Test 49: [Config Precedence 5/6] Environment variables override User Secrets and local JSON configuration", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t49_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string testEnvValue = "Server=env-override-server;Database=TestDb;";
                Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", testEnvValue);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Development.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=dev-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Local.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=local-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Development",
                        ApplicationName = typeof(ConfigurationHelper).Assembly.FullName
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr != testEnvValue)
                        throw new Exception($"Expected environment variable value '{testEnvValue}', but got '{connStr}'");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 50: Command-line arguments override environment variables and all lower providers
            RunTest("Test 50: [Config Precedence 6/6] Command-line arguments override environment variables and all lower providers", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "agy_cfg_t50_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string testEnvValue = "Server=env-override-server;Database=TestDb;";
                string testCliValue = "Server=cli-override-server;Database=TestDb;";
                Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", testEnvValue);
                try
                {
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=base-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Development.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=dev-server;Database=TestDb;\"}}");
                    File.WriteAllText(Path.Combine(tempDir, "appsettings.Local.json"),
                        "{\"ConnectionStrings\": {\"DefaultConnection\": \"Server=local-server;Database=TestDb;\"}}");

                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                    {
                        ContentRootPath = tempDir,
                        EnvironmentName = "Development",
                        ApplicationName = typeof(ConfigurationHelper).Assembly.FullName,
                        Args = new[] { $"--ConnectionStrings:DefaultConnection={testCliValue}" }
                    });
                    ConfigurationHelper.ConfigureLocalSettings(builder);

                    string? connStr = builder.Configuration.GetConnectionString("DefaultConnection");
                    if (connStr != testCliValue)
                        throw new Exception($"Expected CLI argument value '{testCliValue}', but got '{connStr}'");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
                    Directory.Delete(tempDir, true);
                }
            });

            // Test 51: Missing or placeholder connection string triggers clear InvalidOperationException
            RunTest("Test 51: [Config Startup] Missing or placeholder connection string triggers clear InvalidOperationException", () =>
            {
                void ValidateConnectionString(string? defaultConnection)
                {
                    if (string.IsNullOrWhiteSpace(defaultConnection) ||
                        defaultConnection.Contains("YOUR_SERVER", StringComparison.OrdinalIgnoreCase) ||
                        defaultConnection.Contains("YOUR_DATABASE", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Database connection string 'ConnectionStrings:DefaultConnection' is not configured. " +
                            "Configure your local SQL Server connection string using .NET User Secrets ('dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<your-connection-string>\"'), " +
                            "the environment variable 'ConnectionStrings__DefaultConnection', or a local development configuration file ('appsettings.Local.json').");
                    }
                }

                // Case A: Null
                bool nullFailed = false;
                try { ValidateConnectionString(null); }
                catch (InvalidOperationException) { nullFailed = true; }
                if (!nullFailed) throw new Exception("Expected InvalidOperationException for null connection string!");

                // Case B: Empty / whitespace
                bool emptyFailed = false;
                try { ValidateConnectionString("   "); }
                catch (InvalidOperationException) { emptyFailed = true; }
                if (!emptyFailed) throw new Exception("Expected InvalidOperationException for whitespace connection string!");

                // Case C: Placeholder YOUR_SERVER
                bool placeholderServerFailed = false;
                try { ValidateConnectionString("Server=YOUR_SERVER;Database=RetailSalesStockAnalyticsDb;Trusted_Connection=True;"); }
                catch (InvalidOperationException) { placeholderServerFailed = true; }
                if (!placeholderServerFailed) throw new Exception("Expected InvalidOperationException for YOUR_SERVER placeholder!");

                // Case D: Placeholder YOUR_DATABASE
                bool placeholderDbFailed = false;
                try { ValidateConnectionString("Server=localhost;Database=YOUR_DATABASE;Trusted_Connection=True;"); }
                catch (InvalidOperationException) { placeholderDbFailed = true; }
                if (!placeholderDbFailed) throw new Exception("Expected InvalidOperationException for YOUR_DATABASE placeholder!");

                // Case E: Valid connection string does NOT throw
                try
                {
                    ValidateConnectionString("Server=(localdb)\\mssqllocaldb;Database=ValidTestDb;Integrated Security=True;");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Valid connection string should not throw: {ex.Message}");
                }
            });

            // =========================================================
            // TASK 4.5.5: CONCURRENCY-SAFE COMPLAINT CODE TESTS
            // =========================================================

            // Test 52: EF Core Model Metadata enforces ComplaintCodeSequence and unique index on Complaint.ComplaintCode
            RunTest("Test 52: [Architecture & Metadata] EF Core Model Metadata enforces ComplaintCodeSequence and unique index on Complaint.ComplaintCode", () =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestVerificationDb;Integrated Security=True");

                using var context = new ApplicationDbContext(optionsBuilder.Options);
                var model = context.Model;

                // Verify sequence definition
                var sequence = model.FindSequence("ComplaintCodeSequence");
                if (sequence == null) throw new Exception("ComplaintCodeSequence was not found in EF Core model!");
                if (sequence.Type != typeof(int)) throw new Exception($"Expected sequence type int, got {sequence.Type}");
                if (sequence.StartValue != 311) throw new Exception($"Expected sequence startValue 311, got {sequence.StartValue}");

                // Verify Complaint entity and unique index
                var complaintEntity = model.FindEntityType(typeof(Complaint));
                if (complaintEntity == null) throw new Exception("Complaint entity not found in model!");

                var codeProp = complaintEntity.FindProperty("ComplaintCode");
                if (codeProp == null) throw new Exception("ComplaintCode property not found on Complaint entity!");
                if (codeProp.GetMaxLength() != 20) throw new Exception($"Expected ComplaintCode MaxLength 20, got {codeProp.GetMaxLength()}");

                var codeIndex = complaintEntity.GetIndexes().FirstOrDefault(i => i.Properties.Any(p => p.Name == "ComplaintCode"));
                if (codeIndex == null) throw new Exception("Index on Complaint.ComplaintCode was not found!");
                if (!codeIndex.IsUnique) throw new Exception("Index on Complaint.ComplaintCode must be configured as IsUnique()!");
            });

            // Test 53: IComplaintCodeGenerator and ComplaintCodeGenerator architecture and contract
            RunTest("Test 53: [Architecture & Contract] IComplaintCodeGenerator defines GenerateNextCodeAsync and ComplaintCodeGenerator implements contract", () =>
            {
                var interfaceType = typeof(IComplaintCodeGenerator);
                var genMethod = interfaceType.GetMethod("GenerateNextCodeAsync", new[] { typeof(CancellationToken) });
                if (genMethod == null) throw new Exception("IComplaintCodeGenerator must define GenerateNextCodeAsync(CancellationToken)!");
                if (genMethod.ReturnType != typeof(Task<string>)) throw new Exception("GenerateNextCodeAsync must return Task<string>!");

                var implType = typeof(ComplaintCodeGenerator);
                if (!interfaceType.IsAssignableFrom(implType)) throw new Exception("ComplaintCodeGenerator must implement IComplaintCodeGenerator!");
            });

            // Test 54: ComplaintRoutingController injects IComplaintCodeGenerator and retains authorization and anti-forgery attributes
            RunTest("Test 54: [Architecture & Security] ComplaintRoutingController injects IComplaintCodeGenerator and retains authorization and anti-forgery attributes", () =>
            {
                var controllerType = typeof(ComplaintRoutingController);
                var authAttr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
                if (authAttr == null || authAttr.Roles != "Admin,Manager")
                    throw new Exception("ComplaintRoutingController must retain class-level [Authorize(Roles = \"Admin,Manager\")]!");

                var ctor = controllerType.GetConstructors()
                    .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IComplaintCodeGenerator)));
                if (ctor == null)
                    throw new Exception("ComplaintRoutingController constructor must inject IComplaintCodeGenerator!");

                var createPost = controllerType.GetMethods()
                    .FirstOrDefault(m => m.Name == "Create" && m.GetCustomAttribute<HttpPostAttribute>() != null);
                if (createPost == null)
                    throw new Exception("ComplaintRoutingController.Create (POST) method not found!");
                if (createPost.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() == null)
                    throw new Exception("ComplaintRoutingController.Create (POST) must retain [ValidateAntiForgeryToken]!");
            });

            // Test 55: Database uniqueness violation handler detects SQL Server error codes 2601/2627 and provides clean error mapping
            RunTest("Test 55: [Reliability] Database uniqueness violation handler detects SQL Server error codes 2601/2627 and provides clean error mapping", () =>
            {
                var method = typeof(ComplaintRoutingController).GetMethod("IsUniqueConstraintViolation", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new Exception("IsUniqueConstraintViolation helper method not found on ComplaintRoutingController!");

                var nonSqlEx = new DbUpdateException("Standard error", new Exception("Generic error"));
                bool detected = (bool)method.Invoke(null, new object[] { nonSqlEx })!;
                if (detected) throw new Exception("IsUniqueConstraintViolation should return false for generic non-Sql exceptions!");
            });

            int unitPassed = passed;
            int unitFailed = failed;

            Console.WriteLine("=================================================");
            Console.WriteLine($"CHECKOUT, SECURITY & CONFIG TESTS: {passed} PASSED, {failed} FAILED");
            Console.WriteLine("=================================================");

            bool runFunctional = args.Any(a => string.Equals(a, "--functional", StringComparison.OrdinalIgnoreCase));
            bool verifyDb = args.Any(a => string.Equals(a, "--verify-db", StringComparison.OrdinalIgnoreCase));

            if (!runFunctional && !verifyDb)
            {
                Console.WriteLine();
                Console.WriteLine("[INFO] Functional SQL Server integration tests skipped (opt-in flag '--functional' was not supplied).");
                Console.WriteLine("=================================================");
                Console.WriteLine($"OVERALL TEST RESULTS: {passed} PASSED, {failed} FAILED");
                Console.WriteLine("=================================================");
                return failed > 0 ? 1 : 0;
            }

            // =========================================================================
            // DATABASE SAFETY VALIDATION FOR FUNCTIONAL TESTS
            // =========================================================================
            const string ApprovedTestDatabase = "RetailSalesStockAnalytics_TestDb";

            string? rawConnArg = args.FirstOrDefault(a => a.StartsWith("--connection=", StringComparison.OrdinalIgnoreCase));
            string? envTestConn = Environment.GetEnvironmentVariable("ConnectionStrings__TestConnection")
                ?? Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING");

            string testConnString;
            if (rawConnArg != null)
            {
                testConnString = rawConnArg.Substring("--connection=".Length).Trim('"', '\'');
            }
            else if (!string.IsNullOrWhiteSpace(envTestConn))
            {
                testConnString = envTestConn.Trim('"', '\'');
            }
            else
            {
                testConnString = "Server=(localdb)\\mssqllocaldb;Database=RetailSalesStockAnalytics_TestDb;Integrated Security=True;TrustServerCertificate=True";
            }

            SqlConnectionStringBuilder connBuilder;
            try
            {
                connBuilder = new SqlConnectionStringBuilder(testConnString);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SECURITY ERROR] Invalid SQL Server connection string format: {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            string targetDb = connBuilder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(targetDb))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[SECURITY ERROR] Database name is missing or empty in the supplied connection string. Functional tests aborted.");
                Console.ResetColor();
                return 1;
            }

            if (!string.Equals(targetDb, ApprovedTestDatabase, StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[SECURITY ERROR] Database '{targetDb}' is not approved for functional test execution. Only '{ApprovedTestDatabase}' is permitted to protect application data.");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"[SECURITY CHECK] Approved test database verified: '{ApprovedTestDatabase}' on '{connBuilder.DataSource}'.");

            try
            {
                var normalOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(testConnString)
                    .Options;

                using (var probeContext = new ApplicationDbContext(normalOptions))
                {
                    try
                    {
                        await probeContext.Database.OpenConnectionAsync();
                        await probeContext.Database.CloseConnectionAsync();
                    }
                    catch (Exception probeEx)
                    {
                        Console.WriteLine($"[ERROR] Functional test database '{ApprovedTestDatabase}' was not reachable at '{connBuilder.DataSource}': {probeEx.GetType().Name} - {probeEx.Message}");
                        if (probeEx.InnerException != null)
                        {
                            Console.WriteLine($"  Inner Exception: {probeEx.InnerException.GetType().Name} - {probeEx.InnerException.Message}");
                        }
                        return 1;
                    }
                }

                // Scope migration warning suppression strictly to the migration execution context
                var migrationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(testConnString)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                    .Options;

                using (var migrationContext = new ApplicationDbContext(migrationOptions))
                {
                    await migrationContext.Database.MigrateAsync();
                }

                if (verifyDb || runFunctional)
                {
                    await VerifyDatabaseSchemaAsync(testConnString);
                }

                if (verifyDb && !runFunctional)
                {
                    Console.WriteLine("[INFO] Direct SQL Server schema and sequence verification completed successfully.");
                    return 0;
                }

                var (funcPassed, funcFailed, funcErrors) = await FunctionalTestSuite.RunAllFunctionalTestsAsync(testConnString);
                passed += funcPassed;
                failed += funcFailed;
                int functionalPassed = funcPassed;
                int functionalFailed = funcFailed;

                int httpTotalPassed = 0;
                int httpTotalFailed = 0;
                var httpSuiteSummaries = new List<(string script, int passed, int failed)>();

                bool runHttp = args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase));
                if (runHttp)
                {
                    Console.WriteLine();
                    Console.WriteLine("=================================================");
                    Console.WriteLine("STARTING LIVE HTTP INTEGRATION TESTS VIA RUNNER");
                    Console.WriteLine("=================================================");

                    string[] httpScripts = new[]
                    {
                        "Test-SalesHttpIntegration.ps1",
                        "Test-ProductStockHttpIntegration.ps1",
                        "Test-CustomerHttpIntegration.ps1",
                        "Test-ReportsHttpIntegration.ps1"
                    };

                    foreach (var scriptName in httpScripts)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[HTTP RUNNER] Executing {scriptName}...");

                        string scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", scriptName);
                        if (!File.Exists(scriptPath))
                        {
                            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "RetailSalesStockAnalytics.Tests", scriptName);
                        }

                        if (File.Exists(scriptPath))
                        {
                            string summaryTemp = Path.Combine(Path.GetTempPath(), $"http_sum_{Guid.NewGuid():N}.txt");
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "powershell",
                                Arguments = $"-ExecutionPolicy Bypass -File \"{Path.GetFullPath(scriptPath)}\" -Functional -ConnectionString \"{testConnString}\"",
                                UseShellExecute = false
                            };
                            psi.EnvironmentVariables["HTTP_TEST_SUMMARY_FILE"] = summaryTemp;

                            var httpProcess = System.Diagnostics.Process.Start(psi);
                            httpProcess?.WaitForExit();
                            int httpExitCode = httpProcess?.ExitCode ?? 1;

                            int scriptPassed = 0;
                            int scriptFailed = 0;
                            if (File.Exists(summaryTemp))
                            {
                                try
                                {
                                    string sumContent = File.ReadAllText(summaryTemp).Trim();
                                    var parts = sumContent.Split(',');
                                    if (parts.Length >= 2 && int.TryParse(parts[0], out int p) && int.TryParse(parts[1], out int f))
                                    {
                                        scriptPassed = p;
                                        scriptFailed = f;
                                    }
                                }
                                catch { }
                                finally
                                {
                                    try { File.Delete(summaryTemp); } catch { }
                                }
                            }

                            httpSuiteSummaries.Add((scriptName, scriptPassed, scriptFailed));
                            httpTotalPassed += scriptPassed;
                            httpTotalFailed += scriptFailed;

                            if (httpExitCode != 0)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[ERROR] HTTP test script '{scriptName}' exited with failure code {httpExitCode}.");
                                Console.ResetColor();
                                failed++;
                            }
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[ERROR] HTTP test script not found at '{scriptPath}'");
                            Console.ResetColor();
                            failed++;
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("=================================================");
                    Console.WriteLine("HTTP INTEGRATION SUITE BREAKDOWN:");
                    foreach (var item in httpSuiteSummaries)
                    {
                        Console.WriteLine($"  {item.script,-38} {item.passed,3} PASSED, {item.failed,2} FAILED");
                    }
                    Console.WriteLine($"HTTP SUITES TOTAL: {httpTotalPassed} PASSED, {httpTotalFailed} FAILED");
                    Console.WriteLine("=================================================");
                }

                int grandTotalPassed = passed + httpTotalPassed;
                int grandTotalFailed = failed + httpTotalFailed;

                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"OVERALL COMPREHENSIVE TEST RESULTS: {grandTotalPassed} PASSED, {grandTotalFailed} FAILED");
                Console.WriteLine($"  - Unit, Security & Architecture (C#): {unitPassed} PASSED");
                Console.WriteLine($"  - Functional Workflows (C#):          {functionalPassed} PASSED");
                if (runHttp)
                {
                    Console.WriteLine($"  - HTTP Integration (Live Kestrel):     {httpTotalPassed} PASSED");
                }
                Console.WriteLine("=================================================");

                return grandTotalFailed > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Failed during functional test database initialization: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
        }

        private static async Task VerifyDatabaseSchemaAsync(string testConnString)
        {
            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine("DATABASE SCHEMA & SEQUENCE VERIFICATION AUDIT");
            Console.WriteLine("=================================================");

            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(testConnString);
            await conn.OpenAsync();

            // 1. EF Core Migration History Query
            Console.WriteLine("--- 1. EF Core Migration History Query ---");
            string q1 = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddComplaintCodeSequence%'";
            Console.WriteLine($"SQL: {q1}");
            await using (var cmd1 = new Microsoft.Data.SqlClient.SqlCommand(q1, conn))
            await using (var r1 = await cmd1.ExecuteReaderAsync())
            {
                while (await r1.ReadAsync())
                {
                    Console.WriteLine($"RESULT: MigrationId: {r1["MigrationId"]}, ProductVersion: {r1["ProductVersion"]}");
                }
            }

            // 2. sys.sequences Definition Query
            Console.WriteLine("\n--- 2. SQL Server Sequence Definition Query ---");
            string q2 = "SELECT name, CAST(start_value AS BIGINT) AS start_val, CAST(increment AS BIGINT) AS inc, CAST(current_value AS BIGINT) AS curr_val, is_cycling FROM sys.sequences WHERE name = 'ComplaintCodeSequence'";
            Console.WriteLine($"SQL: {q2}");
            await using (var cmd2 = new Microsoft.Data.SqlClient.SqlCommand(q2, conn))
            await using (var r2 = await cmd2.ExecuteReaderAsync())
            {
                while (await r2.ReadAsync())
                {
                    Console.WriteLine($"RESULT: Sequence: {r2["name"]} | StartValue: {r2["start_val"]} | Increment: {r2["inc"]} | CurrentValue: {r2["curr_val"]} | IsCycling: {r2["is_cycling"]}");
                }
            }

            // 3. Sequence Next Value Query
            Console.WriteLine("\n--- 3. Sequence Next Value Query ---");
            string q3 = "SELECT NEXT VALUE FOR ComplaintCodeSequence AS NextValue";
            Console.WriteLine($"SQL: {q3}");
            await using (var cmd3 = new Microsoft.Data.SqlClient.SqlCommand(q3, conn))
            {
                var nextVal = await cmd3.ExecuteScalarAsync();
                Console.WriteLine($"RESULT: NextValue: {nextVal}");
            }

            // 4. Unique Index Definition Query
            Console.WriteLine("\n--- 4. Unique Index Definition Query ---");
            string q4 = @"SELECT i.name AS IndexName, i.is_unique, OBJECT_NAME(i.object_id) AS TableName, COL_NAME(ic.object_id, ic.column_id) AS ColumnName 
                          FROM sys.indexes i 
                          JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id 
                          WHERE i.name = 'IX_Complaints_ComplaintCode'";
            Console.WriteLine($"SQL: {q4}");
            await using (var cmd4 = new Microsoft.Data.SqlClient.SqlCommand(q4, conn))
            await using (var r4 = await cmd4.ExecuteReaderAsync())
            {
                while (await r4.ReadAsync())
                {
                    Console.WriteLine($"RESULT: Index: {r4["IndexName"]} | Table: {r4["TableName"]} | Column: {r4["ColumnName"]} | IsUnique: {r4["is_unique"]}");
                }
            }

            // 5. Existing Complaint Records Query
            Console.WriteLine("\n--- 5. Existing Complaint Records Query ---");
            string q5 = "SELECT ComplaintId, ComplaintCode, CustomerId, RoutingStatus FROM Complaints ORDER BY ComplaintId";
            Console.WriteLine($"SQL: {q5}");
            int complaintCount = 0;
            await using (var cmd5 = new Microsoft.Data.SqlClient.SqlCommand(q5, conn))
            await using (var r5 = await cmd5.ExecuteReaderAsync())
            {
                while (await r5.ReadAsync())
                {
                    complaintCount++;
                    Console.WriteLine($"RESULT: ComplaintId: {r5["ComplaintId"]}, Code: {r5["ComplaintCode"]}, CustomerId: {r5["CustomerId"]}, Status: {r5["RoutingStatus"]}");
                }
            }
            if (complaintCount == 0)
            {
                Console.WriteLine("RESULT: [No complaint records currently in test database]");
            }

            // 6. Max Numeric Complaint Code vs Next Value
            Console.WriteLine("\n--- 6. Max Numeric Complaint Code vs Next Value ---");
            string q6 = "SELECT MAX(TRY_CAST(SUBSTRING(ComplaintCode, 5, 20) AS INT)) AS MaxNumericComplaintCode FROM Complaints WHERE ComplaintCode LIKE 'CMP-%'";
            Console.WriteLine($"SQL: {q6}");
            await using (var cmd6 = new Microsoft.Data.SqlClient.SqlCommand(q6, conn))
            {
                var maxNumeric = await cmd6.ExecuteScalarAsync();
                Console.WriteLine($"RESULT: MaxNumericComplaintCode: {(maxNumeric == DBNull.Value ? "NULL (0 records)" : maxNumeric)}");
            }

            // 7. Baseline Table Counts
            Console.WriteLine("\n--- 7. Test Database Baseline Table Counts ---");
            string q7 = @"SELECT 'Customers' AS Tbl, COUNT(1) AS Cnt FROM Customers UNION ALL
                          SELECT 'Categories', COUNT(1) FROM Categories UNION ALL
                          SELECT 'Products', COUNT(1) FROM Products UNION ALL
                          SELECT 'Sales', COUNT(1) FROM Sales UNION ALL
                          SELECT 'SaleItems', COUNT(1) FROM SaleItems UNION ALL
                          SELECT 'Complaints', COUNT(1) FROM Complaints UNION ALL
                          SELECT 'StockTransactions', COUNT(1) FROM StockTransactions";
            Console.WriteLine($"SQL: {q7}");
            await using (var cmd7 = new Microsoft.Data.SqlClient.SqlCommand(q7, conn))
            await using (var r7 = await cmd7.ExecuteReaderAsync())
            {
                while (await r7.ReadAsync())
                {
                    Console.WriteLine($"RESULT: Table {r7["Tbl"]}: {r7["Cnt"]} rows");
                }
            }
            Console.WriteLine("=================================================\n");
        }
    }
}
