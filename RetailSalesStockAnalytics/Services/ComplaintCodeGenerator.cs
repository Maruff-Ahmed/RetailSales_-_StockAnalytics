using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetailSalesStockAnalytics.Data;

namespace RetailSalesStockAnalytics.Services
{
    public class ComplaintCodeGenerator : IComplaintCodeGenerator
    {
        private readonly ApplicationDbContext _context;

        public ComplaintCodeGenerator(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<string> GenerateNextCodeAsync(CancellationToken cancellationToken = default)
        {
            if (_context.Database.IsSqlServer())
            {
                var connection = _context.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                using var command = connection.CreateCommand();
                var currentTx = _context.Database.CurrentTransaction;
                if (currentTx != null)
                {
                    command.Transaction = currentTx.GetDbTransaction();
                }

                command.CommandText = "SELECT NEXT VALUE FOR ComplaintCodeSequence";
                var result = await command.ExecuteScalarAsync(cancellationToken);

                if (result != null && result != DBNull.Value)
                {
                    long nextVal = Convert.ToInt64(result);
                    return $"CMP-{nextVal}";
                }
            }

            // Fallback for non-relational or mock providers
            int count = await _context.Complaints.CountAsync(cancellationToken);
            return $"CMP-{count + 311}";
        }
    }
}

