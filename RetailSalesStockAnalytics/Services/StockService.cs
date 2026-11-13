using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class StockService : IStockService
    {
        private readonly ApplicationDbContext _context;

        public StockService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<StockDashboardViewModel> GetStockDashboardAsync()
        {
            var products = await _context.Products
                .Include(p => p.Category)
                .ToListAsync();

            var productViewModels = products
                .Select(product =>
                {
                    string status;

                    if (product.StockQuantity == 0)
                    {
                        status = "Out of Stock";
                    }
                    else if (product.StockQuantity <= product.ReorderLevel)
                    {
                        status = "Low Stock";
                    }
                    else
                    {
                        status = "Normal";
                    }

                    decimal inventoryValue =
                        product.StockQuantity * product.PurchasePrice;

                    return new StockProductViewModel
                    {
                        ProductId = product.ProductId,

                        ProductName = product.Name,

                        CategoryName =
                            product.Category?.Name ?? "Uncategorized",

                        StockQuantity =
                            product.StockQuantity,

                        ReorderLevel =
                            product.ReorderLevel,

                        PurchasePrice =
                            product.PurchasePrice,

                        InventoryValue =
                            inventoryValue,

                        Status =
                            status
                    };
                })
                .ToList();

            var dashboard = new StockDashboardViewModel
            {
                TotalProducts = products.Count,

                TotalStockUnits = products.Sum(
                    p => p.StockQuantity),

                TotalInventoryValue = products.Sum(
                    p => p.StockQuantity * p.PurchasePrice),

                LowStockProducts = products.Count(
                    p => p.StockQuantity > 0 &&
                         p.StockQuantity <= p.ReorderLevel),

                OutOfStockProducts = products.Count(
                    p => p.StockQuantity == 0),

                Products = productViewModels
            };

            return dashboard;
        }

        public async Task<StockOperationResult> RestockAsync(
            int productId,
            int quantity,
            decimal unitCost,
            string? note)
        {
            if (quantity <= 0)
            {
                return StockOperationResult.Invalid("Quantity must be at least 1.");
            }

            var product = await _context.Products.FirstOrDefaultAsync(p => p.ProductId == productId);
            if (product == null)
            {
                return StockOperationResult.ProductNotFound();
            }

            // Update current stock
            product.StockQuantity += quantity;

            // Create stock transaction
            var transaction = new StockTransaction
            {
                ProductId = product.ProductId,
                Quantity = quantity,
                TransactionType = "Restock",
                Note = note,
                TransactionDate = DateTime.Now,
                UnitCost = unitCost
            };

            _context.StockTransactions.Add(transaction);
            await _context.SaveChangesAsync();

            return StockOperationResult.Succeeded(product.Name, quantity, product.StockQuantity);
        }

        public async Task<StockOperationResult> AdjustStockAsync(
            int productId,
            int quantity,
            string reason)
        {
            if (quantity == 0)
            {
                return StockOperationResult.Invalid("Adjustment quantity cannot be zero.");
            }

            var product = await _context.Products.FirstOrDefaultAsync(p => p.ProductId == productId);
            if (product == null)
            {
                return StockOperationResult.ProductNotFound();
            }

            // Prevent stock from becoming negative
            if (product.StockQuantity + quantity < 0)
            {
                return StockOperationResult.Invalid(
                    $"Stock cannot become negative. Current stock: {product.StockQuantity}.");
            }

            // Update stock
            product.StockQuantity += quantity;

            // Create transaction record
            var transaction = new StockTransaction
            {
                ProductId = product.ProductId,
                Quantity = quantity,
                TransactionType = "Adjustment",
                Note = reason,
                TransactionDate = DateTime.Now,
                UnitCost = product.PurchasePrice
            };

            _context.StockTransactions.Add(transaction);
            await _context.SaveChangesAsync();

            return StockOperationResult.Succeeded(product.Name, quantity, product.StockQuantity);
        }

        public async Task<StockOperationResult> RecordInitialStockAsync(
            int productId,
            int quantity,
            decimal unitCost)
        {
            if (quantity <= 0)
            {
                return StockOperationResult.Succeeded(string.Empty, 0, 0);
            }

            var transaction = new StockTransaction
            {
                ProductId = productId,
                Quantity = quantity,
                TransactionType = "Initial Stock",
                Note = "Initial stock recorded upon product creation",
                TransactionDate = DateTime.Now,
                UnitCost = unitCost
            };

            _context.StockTransactions.Add(transaction);
            await _context.SaveChangesAsync();

            return StockOperationResult.Succeeded(string.Empty, quantity, quantity);
        }

        public async Task<StockOperationResult> RecordProductEditAdjustmentAsync(
            int productId,
            int oldQuantity,
            int newQuantity,
            decimal unitCost)
        {
            if (oldQuantity == newQuantity)
            {
                return StockOperationResult.Succeeded(string.Empty, 0, newQuantity);
            }

            int diff = newQuantity - oldQuantity;
            var transaction = new StockTransaction
            {
                ProductId = productId,
                Quantity = diff,
                TransactionType = "Product Edit Adjustment",
                Note = $"Stock adjusted via Product Edit from {oldQuantity} to {newQuantity}",
                TransactionDate = DateTime.Now,
                UnitCost = unitCost
            };

            _context.StockTransactions.Add(transaction);
            await _context.SaveChangesAsync();

            return StockOperationResult.Succeeded(string.Empty, diff, newQuantity);
        }
    }
}