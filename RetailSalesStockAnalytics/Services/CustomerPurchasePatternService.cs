using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class CustomerPurchasePatternService
    {
        private readonly ApplicationDbContext _context;

        public CustomerPurchasePatternService(
            ApplicationDbContext context)
        {
            _context = context;
        }


        public async Task<CustomerPurchasePatternViewModel>
            GetPurchasePatternAsync(
                DateTime startDate,
                DateTime endDate)
        {
            // ==========================================
            // DATE RANGE
            // ==========================================

            DateTime endDateExclusive =
                endDate.Date.AddDays(1);


            // ==========================================
            // QUERY A — SALES PROJECTION (CUSTOMER & HOURLY)
            // ==========================================

            var salesData = await _context.Sales
                .AsNoTracking()
                .Where(s =>
                    s.SaleDate >= startDate.Date &&
                    s.SaleDate < endDateExclusive)
                .Select(s => new
                {
                    s.CustomerId,
                    CustomerName = s.Customer != null
                        ? s.Customer.Name
                        : "Unknown Customer",
                    s.TotalAmount,
                    Hour = s.SaleDate.Hour
                })
                .ToListAsync();


            // ==========================================
            // QUERY B — PRODUCT SALES PROJECTION
            // ==========================================

            var productData = await _context.SaleItems
                .AsNoTracking()
                .Where(si =>
                    si.Sale != null &&
                    si.Sale.SaleDate >= startDate.Date &&
                    si.Sale.SaleDate < endDateExclusive)
                .Select(si => new
                {
                    si.ProductId,
                    ProductName = si.Product != null
                        ? si.Product.Name
                        : "Unknown Product",
                    si.Quantity,
                    Revenue = si.Quantity * si.UnitPrice
                })
                .ToListAsync();


            // ==========================================
            // BASIC TOTALS
            // ==========================================

            int totalSales = salesData.Count;

            decimal totalRevenue =
                salesData.Sum(s => s.TotalAmount);


            // ==========================================
            // CUSTOMER ANALYSIS
            // ==========================================

            var customerGroups = salesData
                .GroupBy(s => new
                {
                    CustomerId = s.CustomerId,
                    CustomerName = s.CustomerName
                })
                .Select(g => new
                {
                    g.Key.CustomerId,
                    g.Key.CustomerName,
                    PurchaseCount = g.Count(),
                    TotalSpent = g.Sum(s => s.TotalAmount)
                })
                .OrderByDescending(x => x.PurchaseCount)
                .ToList();


            int totalCustomers =
                customerGroups.Count;


            int repeatCustomers =
                customerGroups.Count(x =>
                    x.PurchaseCount >= 2);


            decimal repeatCustomerPercentage =
                totalCustomers > 0
                    ? (decimal)repeatCustomers
                      / totalCustomers
                      * 100
                    : 0;


            var customerAnalysis =
                customerGroups
                    .Select(x =>
                    {
                        string customerType;

                        if (x.PurchaseCount >= 5)
                        {
                            customerType = "Frequent";
                        }
                        else if (x.PurchaseCount >= 2)
                        {
                            customerType = "Returning";
                        }
                        else
                        {
                            customerType = "Single Visit";
                        }


                        // Percentage of all sales
                        decimal purchaseFrequency =
                            totalSales > 0
                                ? (decimal)x.PurchaseCount
                                  / totalSales
                                  * 100
                                : 0;


                        // Percentage of total revenue
                        decimal revenueContribution =
                            totalRevenue > 0
                                ? x.TotalSpent
                                  / totalRevenue
                                  * 100
                                : 0;


                        return new CustomerPurchaseAnalysisViewModel
                        {
                            CustomerId =
                                x.CustomerId,

                            CustomerName =
                                x.CustomerName,

                            PurchaseCount =
                                x.PurchaseCount,

                            PurchaseFrequencyPercentage =
                                purchaseFrequency,

                            TotalSpent =
                                x.TotalSpent,

                            RevenueContributionPercentage =
                                revenueContribution,

                            CustomerType =
                                customerType
                        };
                    })
                    .ToList();


            // ==========================================
            // HOURLY PURCHASE ANALYSIS
            // ==========================================

            var hourlyGroups =
                salesData
                    .GroupBy(s => s.Hour)
                    .Select(g => new
                    {
                        Hour = g.Key,

                        SalesCount =
                            g.Count(),

                        Revenue =
                            g.Sum(s =>
                                s.TotalAmount)
                    })
                    .OrderBy(x => x.Hour)
                    .ToList();


            var hourlyAnalysis =
                hourlyGroups
                    .Select(x =>
                    {
                        // Percentage of all sales
                        decimal salesPercentage =
                            totalSales > 0
                                ? (decimal)x.SalesCount
                                  / totalSales
                                  * 100
                                : 0;


                        // Percentage of total revenue
                        decimal revenuePercentage =
                            totalRevenue > 0
                                ? x.Revenue
                                  / totalRevenue
                                  * 100
                                : 0;


                        return new PurchaseHourAnalysisViewModel
                        {
                            Hour =
                                x.Hour,

                            SalesCount =
                                x.SalesCount,

                            Revenue =
                                x.Revenue,

                            SalesPercentage =
                                salesPercentage,

                            RevenuePercentage =
                                revenuePercentage
                        };
                    })
                    .ToList();


            // ==========================================
            // PRODUCT PURCHASE ANALYSIS
            // ==========================================

            var productGroups =
                productData
                    .GroupBy(x => new
                    {
                        x.ProductId,
                        x.ProductName
                    })
                    .Select(g => new
                    {
                        g.Key.ProductId,
                        g.Key.ProductName,
                        QuantityPurchased =
                            g.Sum(x => x.Quantity),
                        Revenue =
                            g.Sum(x => x.Revenue)
                    })
                    .OrderByDescending(
                        x => x.QuantityPurchased)
                    .ToList();


            int totalQuantity =
                productGroups.Sum(
                    x => x.QuantityPurchased);


            var productAnalysis =
                productGroups
                    .Select(x =>
                    {
                        // Percentage of total quantity sold
                        decimal purchasePercentage =
                            totalQuantity > 0
                                ? (decimal)x.QuantityPurchased
                                  / totalQuantity
                                  * 100
                                : 0;


                        // Percentage of total revenue
                        decimal revenuePercentage =
                            totalRevenue > 0
                                ? x.Revenue
                                  / totalRevenue
                                  * 100
                                : 0;


                        return new ProductPurchaseAnalysisViewModel
                        {
                            ProductId =
                                x.ProductId,

                            ProductName =
                                x.ProductName,

                            QuantityPurchased =
                                x.QuantityPurchased,

                            PurchasePercentage =
                                purchasePercentage,

                            Revenue =
                                x.Revenue,

                            RevenuePercentage =
                                revenuePercentage
                        };
                    })
                    .ToList();


            // ==========================================
            // RETURN VIEW MODEL
            // ==========================================

            return new CustomerPurchasePatternViewModel
            {
                StartDate =
                    startDate,

                EndDate =
                    endDate,

                TotalCustomers =
                    totalCustomers,

                RepeatCustomers =
                    repeatCustomers,

                RepeatCustomerPercentage =
                    repeatCustomerPercentage,

                Customers =
                    customerAnalysis,

                HourlyAnalysis =
                    hourlyAnalysis,

                Products =
                    productAnalysis
            };
        }
    }
}
