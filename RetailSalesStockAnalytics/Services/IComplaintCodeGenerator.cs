using System.Threading;
using System.Threading.Tasks;

namespace RetailSalesStockAnalytics.Services
{
    public interface IComplaintCodeGenerator
    {
        /// <summary>
        /// Generates the next sequential, concurrency-safe complaint code in the format 'CMP-XXX'
        /// backed by an atomic database sequence.
        /// </summary>
        Task<string> GenerateNextCodeAsync(CancellationToken cancellationToken = default);
    }
}

