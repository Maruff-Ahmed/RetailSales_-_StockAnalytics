using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailSalesStockAnalytics.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckoutTokenToSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckoutToken",
                table: "Sales",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_CheckoutToken",
                table: "Sales",
                column: "CheckoutToken",
                unique: true,
                filter: "[CheckoutToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_CheckoutToken",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CheckoutToken",
                table: "Sales");
        }
    }
}

