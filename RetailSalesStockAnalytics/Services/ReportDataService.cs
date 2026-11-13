using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.ViewModels.Reports;

namespace RetailSalesStockAnalytics.Services
{
    public class ReportDataService : IReportDataService
    {
        private readonly ApplicationDbContext _context;

        public ReportDataService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<DailySalesReportViewModel> GetDailySalesReportAsync(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = new DailySalesReportViewModel
            {
                StartDate = startDate,
                EndDate = endDate
            };

            var salesQuery = _context.Sales
                .AsNoTracking()
                .Include(s => s.SaleItems)
                .AsQueryable();

            if (startDate.HasValue)
            {
                salesQuery = salesQuery.Where(s =>
                    s.SaleDate >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                var endDateExclusive = endDate.Value.Date.AddDays(1);
                salesQuery = salesQuery.Where(s =>
                    s.SaleDate < endDateExclusive);
            }

            var sales = await salesQuery.ToListAsync();

            model.TotalSales = sales.Count;

            model.TotalUnitsSold = sales
                .SelectMany(s => s.SaleItems)
                .Sum(i => i.Quantity);

            model.TotalRevenue = sales.Sum(s => s.TotalAmount);

            model.TotalCost = sales
                .SelectMany(s => s.SaleItems)
                .Sum(i => i.Quantity * i.UnitCost);

            model.TotalProfit = model.TotalRevenue - model.TotalCost;

            model.DailySales = sales
                .GroupBy(s => s.SaleDate.Date)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var items = g.SelectMany(s => s.SaleItems);
                    var revenue = g.Sum(s => s.TotalAmount);
                    var cost = items.Sum(i => i.Quantity * i.UnitCost);

                    return new DailySalesRow
                    {
                        Date = g.Key,
                        SalesCount = g.Count(),
                        UnitsSold = items.Sum(i => i.Quantity),
                        Revenue = revenue,
                        Cost = cost,
                        Profit = revenue - cost
                    };
                })
                .ToList();

            return model;
        }

        public async Task<DailyDemandingProductReportViewModel> GetDailyDemandingProductsReportAsync(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = new DailyDemandingProductReportViewModel
            {
                StartDate = startDate,
                EndDate = endDate
            };

            var salesQuery = _context.Sales
                .AsNoTracking()
                .Include(s => s.SaleItems)
                    .ThenInclude(i => i.Product)
                        .ThenInclude(p => p.Category)
                .AsQueryable();

            if (startDate.HasValue)
            {
                salesQuery = salesQuery.Where(s =>
                    s.SaleDate >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                var endDateExclusive = endDate.Value.Date.AddDays(1);
                salesQuery = salesQuery.Where(s =>
                    s.SaleDate < endDateExclusive);
            }

            var sales = await salesQuery.ToListAsync();

            model.TotalUnitsSold = sales
                .SelectMany(s => s.SaleItems)
                .Sum(i => i.Quantity);

            model.TotalRevenue = sales.Sum(s => s.TotalAmount);

            var productGroups = sales
                .SelectMany(s =>
                    s.SaleItems.Select(i => new
                    {
                        Date = s.SaleDate.Date,
                        ProductId = i.ProductId,
                        ProductName = i.Product?.Name ?? "Unknown",
                        CategoryName = i.Product?.Category?.Name ?? "Unknown",
                        Quantity = i.Quantity,
                        Revenue = i.Quantity * i.UnitPrice
                    }))
                .GroupBy(x => new
                {
                    x.Date,
                    x.ProductId,
                    x.ProductName,
                    x.CategoryName
                })
                .Select(g => new
                {
                    g.Key.Date,
                    g.Key.ProductId,
                    g.Key.ProductName,
                    g.Key.CategoryName,
                    UnitsSold = g.Sum(x => x.Quantity),
                    Revenue = g.Sum(x => x.Revenue)
                })
                .OrderByDescending(x => x.Date)
                .ThenByDescending(x => x.UnitsSold)
                .ToList();

            var dailyTotals = productGroups
                .GroupBy(x => x.Date)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.UnitsSold));

            model.Products = productGroups
                .Select(x =>
                {
                    var dailyTotal = dailyTotals[x.Date];
                    var demandPercentage = dailyTotal > 0
                        ? (decimal)x.UnitsSold / dailyTotal * 100
                        : 0;

                    return new DemandingProductRow
                    {
                        Date = x.Date,
                        ProductName = x.ProductName,
                        CategoryName = x.CategoryName,
                        UnitsSold = x.UnitsSold,
                        Revenue = x.Revenue,
                        DemandPercentage = demandPercentage
                    };
                })
                .ToList();

            model.TotalProductsSold = productGroups
                .Select(x => x.ProductId)
                .Distinct()
                .Count();

            return model;
        }

        public async Task<StockMovementReportViewModel> GetStockMovementReportAsync(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = new StockMovementReportViewModel
            {
                StartDate = startDate,
                EndDate = endDate
            };

            var query = _context.StockTransactions
                .AsNoTracking()
                .Include(t => t.Product)
                .AsQueryable();

            if (startDate.HasValue)
            {
                query = query.Where(t =>
                    t.TransactionDate >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                var endDateExclusive = endDate.Value.Date.AddDays(1);
                query = query.Where(t =>
                    t.TransactionDate < endDateExclusive);
            }

            var transactions = await query
                .OrderByDescending(t => t.TransactionDate)
                .ToListAsync();

            model.TotalTransactions = transactions.Count;

            model.TotalStockIn = transactions
                .Where(t =>
                    t.TransactionType.ToLower() == "restock" ||
                    t.TransactionType.ToLower() == "stock in" ||
                    t.TransactionType.ToLower() == "in" ||
                    t.TransactionType.ToLower() == "sale cancellation")
                .Sum(t => Math.Abs(t.Quantity));

            model.TotalStockOut = transactions
                .Where(t =>
                    t.TransactionType.ToLower() == "sale" ||
                    t.TransactionType.ToLower() == "stock out" ||
                    t.TransactionType.ToLower() == "out")
                .Sum(t => Math.Abs(t.Quantity));

            model.TotalAdjustments = transactions
                .Where(t =>
                    t.TransactionType.ToLower() == "adjustment")
                .Sum(t => Math.Abs(t.Quantity));

            model.Movements = transactions
                .Select(t => new StockMovementRow
                {
                    Date = t.TransactionDate,
                    ProductName = t.Product?.Name ?? "Unknown Product",
                    TransactionType = t.TransactionType,
                    Quantity = t.Quantity,
                    UnitCost = t.UnitCost,
                    TotalCost = Math.Abs(t.Quantity) * t.UnitCost,
                    Note = t.Note
                })
                .ToList();

            return model;
        }

        public async Task<ComplaintRoutingReportViewModel> GetComplaintRoutingReportAsync(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = new ComplaintRoutingReportViewModel
            {
                StartDate = startDate,
                EndDate = endDate
            };

            var query = _context.Complaints
                .AsNoTracking()
                .Include(c => c.Customer)
                .Include(c => c.ComplaintCategory)
                .AsQueryable();

            if (startDate.HasValue)
            {
                query = query.Where(c =>
                    c.CreatedAt >= startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                var endDateExclusive = endDate.Value.Date.AddDays(1);
                query = query.Where(c =>
                    c.CreatedAt < endDateExclusive);
            }

            var complaints = await query
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            model.TotalComplaints = complaints.Count;

            model.RoutedComplaints = complaints.Count(c =>
                c.RoutingStatus.Equals(
                    "Routed",
                    StringComparison.OrdinalIgnoreCase));

            model.PendingComplaints = complaints.Count(c =>
                c.RoutingStatus.Equals(
                    "Pending",
                    StringComparison.OrdinalIgnoreCase));

            model.ManualReviewComplaints = complaints.Count(c =>
                c.RoutingStatus.Equals(
                    "Manual Review",
                    StringComparison.OrdinalIgnoreCase));

            model.AverageSimilarityScore = complaints.Any()
                ? complaints.Average(c => c.SimilarityScore)
                : 0;

            model.Complaints = complaints
                .Select(c => new ComplaintRoutingRow
                {
                    ComplaintCode = c.ComplaintCode,
                    CreatedAt = c.CreatedAt,
                    CustomerName = c.Customer?.Name ?? "Unknown Customer",
                    ComplaintText = c.ComplaintText,
                    CategoryName = c.ComplaintCategory?.CategoryName ?? "Not Assigned",
                    SimilarityScore = c.SimilarityScore,
                    RoutingStatus = c.RoutingStatus
                })
                .ToList();

            return model;
        }
    }
}

