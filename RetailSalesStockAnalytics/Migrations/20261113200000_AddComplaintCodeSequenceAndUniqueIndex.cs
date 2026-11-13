using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailSalesStockAnalytics.Migrations
{
    /// <inheritdoc />
    public partial class AddComplaintCodeSequenceAndUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence<int>(
                name: "ComplaintCodeSequence",
                startValue: 311L);

            // Synchronize sequence with existing complaint codes if any exist in the database
            migrationBuilder.Sql(@"
DECLARE @maxVal INT;
SELECT @maxVal = MAX(TRY_CAST(SUBSTRING(ComplaintCode, 5, 20) AS INT))
FROM Complaints
WHERE ComplaintCode LIKE 'CMP-%';

IF @maxVal IS NOT NULL AND @maxVal >= 311
BEGIN
    DECLARE @nextVal INT = @maxVal + 1;
    DECLARE @sql NVARCHAR(MAX) = N'ALTER SEQUENCE ComplaintCodeSequence RESTART WITH ' + CAST(@nextVal AS NVARCHAR(20)) + N';';
    EXEC sp_executesql @sql;
END");

            migrationBuilder.CreateIndex(
                name: "IX_Complaints_ComplaintCode",
                table: "Complaints",
                column: "ComplaintCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Complaints_ComplaintCode",
                table: "Complaints");

            migrationBuilder.DropSequence(
                name: "ComplaintCodeSequence");
        }
    }
}

