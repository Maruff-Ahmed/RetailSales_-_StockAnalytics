using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailSalesStockAnalytics.Migrations
{
    /// <inheritdoc />
    public partial class LinkComplaintCategoryToProductCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "ComplaintCategories",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ComplaintCategories_CategoryId",
                table: "ComplaintCategories",
                column: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_ComplaintCategories_Categories_CategoryId",
                table: "ComplaintCategories",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "CategoryId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ComplaintCategories_Categories_CategoryId",
                table: "ComplaintCategories");

            migrationBuilder.DropIndex(
                name: "IX_ComplaintCategories_CategoryId",
                table: "ComplaintCategories");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "ComplaintCategories");
        }
    }
}
