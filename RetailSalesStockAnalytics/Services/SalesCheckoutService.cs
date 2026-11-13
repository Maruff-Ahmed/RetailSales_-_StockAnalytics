using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class SalesCheckoutService : ISalesCheckoutService
    {
        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SalesCheckoutService> _logger;
        private static readonly object _tokenLock = new();

        public SalesCheckoutService(
            ApplicationDbContext context,
            IMemoryCache cache,
            ILogger<SalesCheckoutService> logger)
        {
            _context = context;
            _cache = cache;
            _logger = logger;
        }

        private void ReleaseInFlightToken(string? tokenKey, bool tokenAcquired)
        {
            if (tokenAcquired && tokenKey != null)
            {
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

        private void CompleteToken(string? tokenKey)
        {
            if (tokenKey != null)
            {
                lock (_tokenLock)
                {
                    _cache.Set(tokenKey, CheckoutTokenStatus.Completed, TimeSpan.FromMinutes(30));
                }
            }
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            if (ex.InnerException is SqlException sqlEx)
            {
                // 2601: Cannot insert duplicate key row in object with unique index
                // 2627: Violation of PRIMARY KEY or UNIQUE KEY constraint
                return sqlEx.Number == 2601 || sqlEx.Number == 2627;
            }
            return false;
        }

        public async Task<CheckoutResult> CreateSaleAsync(
            SaleCreateViewModel model,
            bool isModelStateValid = true)
        {
            // Preserve the original submitted token for logging and database verification
            string originalAttemptedToken = model.CheckoutToken ?? string.Empty;

            // -----------------------------------------------------
            // Duplicate Checkout Protection (Idempotency Token)
            // -----------------------------------------------------

            string? tokenKey = null;
            bool tokenAcquired = false;

            if (string.IsNullOrWhiteSpace(model.CheckoutToken))
            {
                return CheckoutResult.Failed(
                    "Checkout session token is missing. Please reload the page to start a new sale.");
            }

            tokenKey = $"checkout_token_{model.CheckoutToken}";

            lock (_tokenLock)
            {
                if (_cache.TryGetValue(tokenKey, out var existingStatus))
                {
                    if (existingStatus is CheckoutTokenStatus status && status == CheckoutTokenStatus.Completed)
                    {
                        return CheckoutResult.Failed(
                            "This sale has already been completed. Duplicate submission was blocked.",
                            isDuplicateBlocked: true);
                    }
                    else
                    {
                        return CheckoutResult.Failed(
                            "This checkout request is currently being processed. Duplicate submission was blocked.",
                            isDuplicateBlocked: true);
                    }
                }
                else
                {
                    // Reserve token in memory cache as InFlight
                    tokenAcquired = true;
                    _cache.Set(tokenKey, CheckoutTokenStatus.InFlight, TimeSpan.FromMinutes(15));
                }
            }

            // -----------------------------------------------------
            // Validate sale items & customer name
            // -----------------------------------------------------

            var validationErrors = new List<CheckoutValidationError>();

            if (model.Items == null || model.Items.Count == 0)
            {
                validationErrors.Add(new CheckoutValidationError("", "At least one product is required."));
            }

            if (string.IsNullOrWhiteSpace(model.CustomerName))
            {
                validationErrors.Add(new CheckoutValidationError("CustomerName", "Customer name is required."));
            }

            if (!isModelStateValid || validationErrors.Count > 0)
            {
                ReleaseInFlightToken(tokenKey, tokenAcquired);
                return CheckoutResult.Failed(validationErrors);
            }

            // -----------------------------------------------------
            // Durable Database Idempotency Check
            // (Guards against app restarts/crashes where cache was cleared)
            // -----------------------------------------------------

            var existingSale = await _context.Sales
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CheckoutToken == model.CheckoutToken);

            if (existingSale != null)
            {
                _logger.LogInformation(
                    "Duplicate sale creation blocked by database check for token {Token} (Existing Sale #{SaleId})",
                    originalAttemptedToken,
                    existingSale.SaleId);

                CompleteToken(tokenKey);

                return CheckoutResult.Failed(
                    "This sale has already been completed. Duplicate submission was blocked.",
                    isDuplicateBlocked: true);
            }

            // -----------------------------------------------------
            // Start database transaction
            // -----------------------------------------------------

            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                decimal subtotal = 0;
                decimal totalCost = 0;
                var saleItems = new List<SaleItem>();

                // -------------------------------------------------
                // Process and validate sale items
                // -------------------------------------------------

                var requestedTotals = model.Items!
                    .GroupBy(i => i.ProductId)
                    .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

                var productMap = new Dictionary<int, Product>();

                foreach (var item in model.Items!)
                {
                    if (!productMap.TryGetValue(item.ProductId, out var product))
                    {
                        product = await _context.Products
                            .AsNoTracking()
                            .FirstOrDefaultAsync(p => p.ProductId == item.ProductId);

                        if (product == null)
                        {
                            validationErrors.Add(new CheckoutValidationError(
                                "",
                                $"Product with ID {item.ProductId} was not found."));
                            continue;
                        }

                        productMap[item.ProductId] = product;
                    }

                    // Quantity validation
                    if (item.Quantity <= 0)
                    {
                        validationErrors.Add(new CheckoutValidationError(
                            "",
                            $"Quantity for {product.Name} must be greater than zero."));
                        continue;
                    }

                    // -------------------------------------------------
                    // Pre-check stock across all items of this product in cart
                    // -------------------------------------------------

                    int totalRequestedForProduct = requestedTotals[item.ProductId];

                    if (totalRequestedForProduct > product.StockQuantity)
                    {
                        validationErrors.Add(new CheckoutValidationError(
                            "",
                            $"Not enough stock for {product.Name}. " +
                            $"Requested: {totalRequestedForProduct}, Available stock: {product.StockQuantity}."));
                        continue;
                    }

                    // Calculate revenue
                    decimal itemRevenue = product.SellingPrice * item.Quantity;

                    // Calculate cost
                    decimal itemCost = product.PurchasePrice * item.Quantity;

                    subtotal += itemRevenue;
                    totalCost += itemCost;

                    // Create SaleItem
                    saleItems.Add(new SaleItem
                    {
                        ProductId = product.ProductId,
                        Quantity = item.Quantity,
                        UnitPrice = product.SellingPrice,
                        UnitCost = product.PurchasePrice
                    });
                }

                // Check validation after processing items
                if (validationErrors.Count > 0)
                {
                    await transaction.RollbackAsync();
                    ReleaseInFlightToken(tokenKey, tokenAcquired);
                    return CheckoutResult.Failed(validationErrors);
                }

                // -------------------------------------------------
                // Validate discount
                // -------------------------------------------------

                if (model.Discount < 0)
                {
                    await transaction.RollbackAsync();
                    ReleaseInFlightToken(tokenKey, tokenAcquired);
                    return CheckoutResult.Failed(
                        "Discount cannot be negative.",
                        fieldName: "Discount");
                }

                if (model.Discount > subtotal)
                {
                    await transaction.RollbackAsync();
                    ReleaseInFlightToken(tokenKey, tokenAcquired);
                    return CheckoutResult.Failed(
                        "Discount cannot be greater than the subtotal.",
                        fieldName: "Discount");
                }

                // -------------------------------------------------
                // FIND OR CREATE CUSTOMER
                // -------------------------------------------------

                Customer? customer = null;

                // First priority: Phone
                if (!string.IsNullOrWhiteSpace(model.CustomerPhone))
                {
                    var phone = model.CustomerPhone.Trim();
                    customer = await _context.Customers
                        .FirstOrDefaultAsync(c => c.Phone == phone);
                }

                // Second priority: Email
                if (customer == null && !string.IsNullOrWhiteSpace(model.CustomerEmail))
                {
                    var email = model.CustomerEmail.Trim();
                    customer = await _context.Customers
                        .FirstOrDefaultAsync(c => c.Email == email);
                }

                // Create new customer if not found
                if (customer == null)
                {
                    customer = new Customer
                    {
                        Name = model.CustomerName.Trim(),
                        Phone = string.IsNullOrWhiteSpace(model.CustomerPhone)
                            ? null
                            : model.CustomerPhone.Trim(),
                        Email = string.IsNullOrWhiteSpace(model.CustomerEmail)
                            ? null
                            : model.CustomerEmail.Trim()
                    };

                    _context.Customers.Add(customer);
                    await _context.SaveChangesAsync();
                }
                else
                {
                    // Update missing customer information
                    bool customerChanged = false;

                    if (!string.IsNullOrWhiteSpace(model.CustomerName) &&
                        customer.Name != model.CustomerName.Trim())
                    {
                        customer.Name = model.CustomerName.Trim();
                        customerChanged = true;
                    }

                    if (!string.IsNullOrWhiteSpace(model.CustomerPhone) &&
                        string.IsNullOrWhiteSpace(customer.Phone))
                    {
                        customer.Phone = model.CustomerPhone.Trim();
                        customerChanged = true;
                    }

                    if (!string.IsNullOrWhiteSpace(model.CustomerEmail) &&
                        string.IsNullOrWhiteSpace(customer.Email))
                    {
                        customer.Email = model.CustomerEmail.Trim();
                        customerChanged = true;
                    }

                    if (customerChanged)
                    {
                        await _context.SaveChangesAsync();
                    }
                }

                // -------------------------------------------------
                // Calculate Net Revenue
                // -------------------------------------------------

                decimal totalAmount = subtotal - model.Discount;

                // -------------------------------------------------
                // Create Sale
                // -------------------------------------------------

                var sale = new Sale
                {
                    CustomerId = customer.CustomerId,
                    SaleDate = model.SaleDate,
                    TotalAmount = totalAmount,
                    Discount = model.Discount,
                    CheckoutToken = model.CheckoutToken,
                    SaleItems = saleItems
                };

                _context.Sales.Add(sale);
                await _context.SaveChangesAsync();

                // -------------------------------------------------
                // ATOMIC CONCURRENT STOCK DEDUCTION
                // -------------------------------------------------
                // Update products in deterministic sorted order (by ProductId)
                // to prevent deadlocks between concurrent checkouts.
                // The atomic WHERE condition (StockQuantity >= totalQuantity)
                // guarantees at the database level that stock cannot be oversold
                // or overwritten by concurrent transactions.
                foreach (var (productId, totalQuantity) in requestedTotals.OrderBy(kvp => kvp.Key))
                {
                    int rowsUpdated = await _context.Products
                        .Where(p => p.ProductId == productId && p.StockQuantity >= totalQuantity)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity - totalQuantity));

                    if (rowsUpdated == 0)
                    {
                        var product = productMap.GetValueOrDefault(productId);
                        var productName = product?.Name ?? $"Product ID {productId}";
                        var currentStock = await _context.Products
                            .AsNoTracking()
                            .Where(p => p.ProductId == productId)
                            .Select(p => p.StockQuantity)
                            .FirstOrDefaultAsync();

                        throw new InvalidOperationException(
                            $"Insufficient stock for {productName}. Another checkout claimed the remaining inventory. Requested: {totalQuantity}, Available: {currentStock}.");
                    }
                }

                // -------------------------------------------------
                // Record outbound stock transactions
                // -------------------------------------------------

                foreach (var item in model.Items!)
                {
                    var product = productMap[item.ProductId];

                    _context.StockTransactions.Add(new StockTransaction
                    {
                        ProductId = product.ProductId,
                        Quantity = -item.Quantity,
                        TransactionType = "Sale",
                        Note = $"Sale #{sale.SaleId} - Customer: {customer.Name}",
                        TransactionDate = model.SaleDate,
                        UnitCost = product.PurchasePrice
                    });
                }

                await _context.SaveChangesAsync();

                // -------------------------------------------------
                // Commit transaction
                // -------------------------------------------------

                await transaction.CommitAsync();

                // Mark token as permanently completed in cache
                CompleteToken(tokenKey);

                string maskedToken = !string.IsNullOrEmpty(originalAttemptedToken) && originalAttemptedToken.Length > 8
                    ? originalAttemptedToken[..8] + "..."
                    : "[Masked]";

                _logger.LogInformation(
                    "Sale #{SaleId} created successfully for customer {CustomerName} with token {Token}",
                    sale.SaleId,
                    customer.Name,
                    maskedToken);

                return CheckoutResult.Succeeded(sale.SaleId);
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                ReleaseInFlightToken(tokenKey, tokenAcquired);

                string maskedToken = !string.IsNullOrEmpty(originalAttemptedToken) && originalAttemptedToken.Length > 8
                    ? originalAttemptedToken[..8] + "..."
                    : "[Masked]";

                _logger.LogWarning(
                    ex,
                    "Checkout failed for token {Token} due to stock or validation constraint: {Message}",
                    maskedToken,
                    ex.Message);

                return CheckoutResult.Failed(ex.Message);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await transaction.RollbackAsync();
                CompleteToken(tokenKey);

                string maskedToken = !string.IsNullOrEmpty(originalAttemptedToken) && originalAttemptedToken.Length > 8
                    ? originalAttemptedToken[..8] + "..."
                    : "[Masked]";

                _logger.LogWarning(
                    ex,
                    "Duplicate sale creation blocked by database unique index for token {Token}",
                    maskedToken);

                return CheckoutResult.Failed(
                    "This sale has already been completed. Duplicate submission was blocked.",
                    isDuplicateBlocked: true);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                ReleaseInFlightToken(tokenKey, tokenAcquired);

                string maskedToken = !string.IsNullOrEmpty(originalAttemptedToken) && originalAttemptedToken.Length > 8
                    ? originalAttemptedToken[..8] + "..."
                    : "[Masked]";

                _logger.LogError(
                    ex,
                    "Unexpected error occurred during checkout for token {Token}",
                    maskedToken);

                return CheckoutResult.Failed("An error occurred while saving the sale.");
            }
        }

        public async Task<SaleCancellationResult> CancelSaleAsync(int saleId)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // 1. Read sale and its items (with AsNoTracking) to know what products/quantities to restore
                var sale = await _context.Sales
                    .AsNoTracking()
                    .Include(s => s.SaleItems)
                    .FirstOrDefaultAsync(s => s.SaleId == saleId);

                if (sale == null)
                {
                    await transaction.RollbackAsync();
                    return SaleCancellationResult.SaleNotFound();
                }

                // 2. ATOMIC CLAIM & DELETION:
                // Delete the sale directly in SQL Server first under an exclusive row lock.
                // Foreign key cascade (FK_SaleItems_Sales_SaleId ON DELETE CASCADE) removes related SaleItems.
                int rowsDeleted = await _context.Sales
                    .Where(s => s.SaleId == saleId)
                    .ExecuteDeleteAsync();

                if (rowsDeleted == 0)
                {
                    // Another concurrent request has already deleted this sale!
                    await transaction.RollbackAsync();
                    _logger.LogWarning("Concurrent deletion attempt on already-deleted Sale #{SaleId} was blocked", saleId);
                    return SaleCancellationResult.ConcurrentDelete();
                }

                // 3. Since this request is the sole winner that claimed and deleted the sale,
                // restore product inventory atomically ordered by ProductId to prevent deadlocks.
                var groupedItems = sale.SaleItems
                    .GroupBy(i => i.ProductId)
                    .Select(g => new { ProductId = g.Key, Quantity = g.Sum(i => i.Quantity) })
                    .OrderBy(g => g.ProductId);

                foreach (var group in groupedItems)
                {
                    await _context.Products
                        .Where(p => p.ProductId == group.ProductId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity + group.Quantity));
                }

                // 4. Record stock cancellation transactions
                foreach (var item in sale.SaleItems)
                {
                    _context.StockTransactions.Add(new StockTransaction
                    {
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        TransactionType = "Sale Cancellation",
                        Note = $"Restored stock from deleted Sale #{sale.SaleId}",
                        TransactionDate = DateTime.Now,
                        UnitCost = item.UnitCost
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Sale #{SaleId} deleted and stock restored successfully", saleId);
                return SaleCancellationResult.Succeeded();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error deleting Sale #{SaleId}", saleId);
                return SaleCancellationResult.Failed("An error occurred while deleting the sale.");
            }
        }
    }
}

