using System.Collections.Generic;

namespace RetailSalesStockAnalytics.Services
{
    public class CheckoutResult
    {
        public bool Success { get; set; }
        public int? SaleId { get; set; }
        public bool IsDuplicateBlocked { get; set; }
        public List<CheckoutValidationError> Errors { get; set; } = new();

        public static CheckoutResult Succeeded(int saleId) =>
            new() { Success = true, SaleId = saleId };

        public static CheckoutResult Failed(string errorMessage, string fieldName = "", bool isDuplicateBlocked = false)
        {
            var result = new CheckoutResult
            {
                Success = false,
                IsDuplicateBlocked = isDuplicateBlocked
            };
            result.Errors.Add(new CheckoutValidationError(fieldName, errorMessage));
            return result;
        }

        public static CheckoutResult Failed(List<CheckoutValidationError> errors) =>
            new() { Success = false, Errors = errors };
    }

    public record CheckoutValidationError(string FieldName, string ErrorMessage);

    public class SaleCancellationResult
    {
        public bool Success { get; set; }
        public bool NotFound { get; set; }
        public bool AlreadyDeleted { get; set; }
        public string? ErrorMessage { get; set; }

        public static SaleCancellationResult Succeeded() =>
            new() { Success = true };

        public static SaleCancellationResult SaleNotFound(string message = "Sale not found or has already been deleted.") =>
            new() { Success = false, NotFound = true, ErrorMessage = message };

        public static SaleCancellationResult ConcurrentDelete(string message = "This sale has already been deleted by another user.") =>
            new() { Success = false, AlreadyDeleted = true, ErrorMessage = message };

        public static SaleCancellationResult Failed(string message = "An error occurred while deleting the sale.") =>
            new() { Success = false, ErrorMessage = message };
    }
}

