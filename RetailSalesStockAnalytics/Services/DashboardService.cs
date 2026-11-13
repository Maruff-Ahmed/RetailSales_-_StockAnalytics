using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class DashboardService : IDashboardService
    {
        private readonly ApplicationDbContext _context;

        public DashboardService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<DashboardViewModel> GetDashboardAsync()
        {
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            // Last 7 days including today
            var sevenDaysAgo = today.AddDays(-6);


            // =================================================
            // TOP SUMMARY CARDS
            // =================================================

            var todaySales = await _context.Sales
                .AsNoTracking()
                .Where(s =>
                    s.SaleDate >= today &&
                    s.SaleDate < tomorrow)
                .ToListAsync();

            var todayNetRevenue = todaySales
                .Sum(s => s.TotalAmount);

            var todaySalesCount = todaySales.Count;

            var totalStockQuantity = await _context.Products
                .AsNoTracking()
                .SumAsync(p => p.StockQuantity);

            var lowStockCount = await _context.Products
                .AsNoTracking()
                .CountAsync(p =>
                    p.StockQuantity <= p.ReorderLevel);


            // =================================================
            // SALES TREND — LAST 7 DAYS
            // =================================================

            var salesForTrend = await _context.Sales
                .AsNoTracking()
                .Where(s =>
                    s.SaleDate >= sevenDaysAgo &&
                    s.SaleDate < tomorrow)
                .Select(s => new
                {
                    s.SaleDate,
                    s.TotalAmount
                })
                .ToListAsync();

            var salesTrend = Enumerable
                .Range(0, 7)
                .Select(i =>
                {
                    var date = sevenDaysAgo.AddDays(i);

                    var daySales = salesForTrend
                        .Where(s =>
                            s.SaleDate.Date == date)
                        .ToList();

                    return new DailySalesViewModel
                    {
                        Date = date,

                        Revenue = daySales
                            .Sum(s => s.TotalAmount),

                        SalesCount = daySales.Count
                    };
                })
                .ToList();


            // =================================================
            // SALES BY CATEGORY
            // =================================================

            var categorySales = await _context.SaleItems
                .AsNoTracking()
                .Where(si =>
                    si.Sale != null &&
                    si.Sale.SaleDate >= sevenDaysAgo &&
                    si.Sale.SaleDate < tomorrow)
                .GroupBy(si => new
                {
                    si.Product!.CategoryId,
                    CategoryName = si.Product.Category!.Name
                })
                .Select(g => new CategorySalesViewModel
                {
                    CategoryName = g.Key.CategoryName,

                    QuantitySold = g.Sum(x => x.Quantity),

                    Revenue = g.Sum(x =>
                        x.Quantity * x.UnitPrice)
                })
                .OrderByDescending(x => x.Revenue)
                .ToListAsync();


            // =================================================
            // TOP PRODUCTS
            // =================================================

            var topProducts = await _context.SaleItems
                .AsNoTracking()
                .Where(si =>
                    si.Sale != null &&
                    si.Sale.SaleDate >= sevenDaysAgo &&
                    si.Sale.SaleDate < tomorrow)
                .GroupBy(si => new
                {
                    si.ProductId,
                    ProductName = si.Product!.Name
                })
                .Select(g => new TopProductViewModel
                {
                    ProductName = g.Key.ProductName,

                    QuantitySold = g.Sum(x => x.Quantity),

                    Revenue = g.Sum(x =>
                        x.Quantity * x.UnitPrice)
                })
                .OrderByDescending(x => x.QuantitySold)
                .Take(5)
                .ToListAsync();


            // =================================================
            // STOCK MOVEMENT
            //
            // Positive Quantity = Stock In
            // Negative Quantity = Stock Out
            // =================================================

            var stockInQuantity = await _context.StockTransactions
                .AsNoTracking()
                .Where(st =>
                    st.Quantity > 0 &&
                    st.TransactionDate >= sevenDaysAgo &&
                    st.TransactionDate < tomorrow)
                .SumAsync(st =>
                    (int?)st.Quantity) ?? 0;

            var stockOutQuantity = await _context.StockTransactions
                .AsNoTracking()
                .Where(st =>
                    st.Quantity < 0 &&
                    st.TransactionDate >= sevenDaysAgo &&
                    st.TransactionDate < tomorrow)
                .SumAsync(st =>
                    (int?)Math.Abs(st.Quantity)) ?? 0;


            // =================================================
            // RECENT SALES
            // =================================================

            var recentSales = await _context.Sales
                .AsNoTracking()
                .Include(s => s.Customer)
                .OrderByDescending(s => s.SaleDate)
                .Take(5)
                .Select(s => new RecentSaleViewModel
                {
                    SaleId = s.SaleId,

                    CustomerName =
                        s.Customer != null
                            ? s.Customer.Name
                            : "Unknown Customer",

                    SaleDate = s.SaleDate,

                    TotalAmount = s.TotalAmount
                })
                .ToListAsync();


            // =================================================
            // LOW STOCK PRODUCTS
            // =================================================

            var lowStockProducts = await _context.Products
                .AsNoTracking()
                .Where(p =>
                    p.StockQuantity <= p.ReorderLevel)
                .OrderBy(p => p.StockQuantity)
                .Take(5)
                .Select(p => new LowStockProductViewModel
                {
                    ProductName = p.Name,

                    StockQuantity = p.StockQuantity,

                    ReorderLevel = p.ReorderLevel
                })
                .ToListAsync();


            // =================================================
            // AI COMPLAINT SUMMARY
            // =================================================

            var totalComplaints = await _context.Complaints
                .AsNoTracking()
                .CountAsync();

            var routedComplaints = await _context.Complaints
                .AsNoTracking()
                .CountAsync(c =>
                    c.RoutingStatus == "Routed");

            var pendingComplaints = await _context.Complaints
                .AsNoTracking()
                .CountAsync(c =>
                    c.RoutingStatus == "Pending");


            // =================================================
            // CREATE DASHBOARD VIEW MODEL
            // =================================================

            return new DashboardViewModel
            {
                // Summary
                TodayNetRevenue = todayNetRevenue,

                TodaySalesCount = todaySalesCount,

                TotalStockQuantity = totalStockQuantity,

                LowStockCount = lowStockCount,

                // Sales trend
                SalesTrend = salesTrend,

                // Categories
                SalesByCategory = categorySales,

                // Top products
                TopProducts = topProducts,

                // Stock movement
                StockInQuantity = stockInQuantity,

                StockOutQuantity = stockOutQuantity,

                // Recent sales
                RecentSales = recentSales,

                // Low stock
                LowStockProducts = lowStockProducts,

                // Complaints
                TotalComplaints = totalComplaints,

                RoutedComplaints = routedComplaints,

                PendingComplaints = pendingComplaints
            };
        }
    }
}

