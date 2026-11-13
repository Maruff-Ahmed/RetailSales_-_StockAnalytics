using System;
using System.Collections.Generic;
using RetailSalesStockAnalytics.Models;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class SalesIndexViewModel
    {
        public static readonly int[] AllowedPageSizes = new[] { 10, 25, 50 };
        public const int DefaultPageSize = 25;
        public const int DefaultPageNumber = 1;

        public IReadOnlyList<Sale> Sales { get; set; } = new List<Sale>();

        // Pagination metadata
        public int PageNumber { get; set; } = DefaultPageNumber;
        public int PageSize { get; set; } = DefaultPageSize;
        public int TotalItems { get; set; }

        public int TotalPages => PageSize > 0
            ? (int)Math.Ceiling((double)TotalItems / PageSize)
            : 0;

        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;

        public int FirstItemIndex => TotalItems == 0
            ? 0
            : (int)(((long)(PageNumber - 1) * PageSize) + 1);

        public int LastItemIndex => TotalItems == 0
            ? 0
            : (int)Math.Min((long)PageNumber * PageSize, TotalItems);

        // Filter and sort parameters
        public string? SearchTerm { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? SortOrder { get; set; }
    }
}

