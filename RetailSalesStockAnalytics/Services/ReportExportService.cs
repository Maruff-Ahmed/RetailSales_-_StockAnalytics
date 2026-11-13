
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RetailSalesStockAnalytics.ViewModels.Reports;

namespace RetailSalesStockAnalytics.Services
{
    public class ReportExportService
    {
        // =========================================================
        // DAILY SALES - EXCEL
        // =========================================================

        public byte[] ExportDailySalesExcel(
            DailySalesReportViewModel model)
        {
            using var workbook = new XLWorkbook();

            var worksheet =
                workbook.Worksheets.Add("Daily Sales");

            worksheet.Cell(1, 1).Value =
                "Daily Sales Report";

            worksheet.Cell(2, 1).Value =
                "Start Date";

            worksheet.Cell(2, 2).Value =
                model.StartDate?.ToString("dd MMM yyyy")
                ?? "All";

            worksheet.Cell(3, 1).Value =
                "End Date";

            worksheet.Cell(3, 2).Value =
                model.EndDate?.ToString("dd MMM yyyy")
                ?? "All";


            worksheet.Cell(5, 1).Value =
                "Date";

            worksheet.Cell(5, 2).Value =
                "Sales";

            worksheet.Cell(5, 3).Value =
                "Revenue";


            int row = 6;

            foreach (var item in model.DailySales)
            {
                worksheet.Cell(row, 1).Value =
                    item.Date.ToString("dd MMM yyyy");

                worksheet.Cell(row, 2).Value =
                    item.SalesCount;

                worksheet.Cell(row, 3).Value =
                    item.Revenue;

                row++;
            }


            worksheet.Columns().AdjustToContents();


            using var stream = new MemoryStream();

            workbook.SaveAs(stream);

            return stream.ToArray();
        }


        // =========================================================
        // DAILY SALES - PDF
        // =========================================================

        public byte[] ExportDailySalesPdf(
            DailySalesReportViewModel model)
        {
            var document =
                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(40);

                        page.Header()
                            .Text("Daily Sales Report")
                            .FontSize(20)
                            .Bold();

                        page.Content()
                            .Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn();

                                    columns.RelativeColumn();

                                    columns.RelativeColumn();
                                });


                                table.Header(header =>
                                {
                                    header.Cell()
                                        .Text("Date")
                                        .Bold();

                                    header.Cell()
                                        .Text("Sales")
                                        .Bold();

                                    header.Cell()
                                        .Text("Revenue")
                                        .Bold();
                                });


                                foreach (var item in model.DailySales)
                                {
                                    table.Cell()
                                        .Text(
                                            item.Date
                                                .ToString("dd MMM yyyy"));

                                    table.Cell()
                                        .Text(
                                            item.SalesCount
                                                .ToString());

                                    table.Cell()
                                        .Text(
                                            $"৳ {item.Revenue:N2}");
                                }

                            });

                    });
                });


            return document.GeneratePdf();
        }


        // =========================================================
        // STOCK MOVEMENT - EXCEL
        // =========================================================

        public byte[] ExportStockMovementExcel(
            StockMovementReportViewModel model)
        {
            using var workbook = new XLWorkbook();

            var worksheet =
                workbook.Worksheets.Add("Stock Movement");


            worksheet.Cell(1, 1).Value =
                "Stock Movement Report";


            worksheet.Cell(3, 1).Value =
                "Date";

            worksheet.Cell(3, 2).Value =
                "Product";

            worksheet.Cell(3, 3).Value =
                "Transaction Type";

            worksheet.Cell(3, 4).Value =
                "Quantity";

            worksheet.Cell(3, 5).Value =
                "Unit Cost";

            worksheet.Cell(3, 6).Value =
                "Total Cost";

            worksheet.Cell(3, 7).Value =
                "Note";


            int row = 4;

            foreach (var item in model.Movements)
            {
                worksheet.Cell(row, 1).Value =
                    item.Date;

                worksheet.Cell(row, 2).Value =
                    item.ProductName;

                worksheet.Cell(row, 3).Value =
                    item.TransactionType;

                worksheet.Cell(row, 4).Value =
                    item.Quantity;

                worksheet.Cell(row, 5).Value =
                    item.UnitCost;

                worksheet.Cell(row, 6).Value =
                    item.TotalCost;

                worksheet.Cell(row, 7).Value =
                    item.Note ?? "";

                row++;
            }


            worksheet.Columns().AdjustToContents();


            using var stream = new MemoryStream();

            workbook.SaveAs(stream);

            return stream.ToArray();
        }


        // =========================================================
        // STOCK MOVEMENT - PDF
        // =========================================================

        public byte[] ExportStockMovementPdf(
            StockMovementReportViewModel model)
        {
            var document =
                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(30);

                        page.Header()
                            .Text("Stock Movement Report")
                            .FontSize(20)
                            .Bold();


                        page.Content()
                            .Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn();

                                    columns.RelativeColumn();

                                    columns.RelativeColumn();

                                    columns.ConstantColumn(60);

                                    columns.ConstantColumn(80);
                                });


                                table.Header(header =>
                                {
                                    header.Cell()
                                        .Text("Date")
                                        .Bold();

                                    header.Cell()
                                        .Text("Product")
                                        .Bold();

                                    header.Cell()
                                        .Text("Type")
                                        .Bold();

                                    header.Cell()
                                        .Text("Qty")
                                        .Bold();

                                    header.Cell()
                                        .Text("Cost")
                                        .Bold();
                                });


                                foreach (var item in model.Movements)
                                {
                                    table.Cell()
                                        .Text(
                                            item.Date
                                                .ToString("dd MMM yyyy"));

                                    table.Cell()
                                        .Text(item.ProductName);

                                    table.Cell()
                                        .Text(item.TransactionType);

                                    table.Cell()
                                        .Text(
                                            item.Quantity
                                                .ToString());

                                    table.Cell()
                                        .Text(
                                            $"৳ {item.TotalCost:N2}");
                                }

                            });

                    });
                });


            return document.GeneratePdf();
        }
        
// =========================================================
// DAILY DEMANDING PRODUCTS - EXCEL
// =========================================================

public byte[] ExportDailyDemandingProductsExcel(
    DailyDemandingProductReportViewModel model)
        {
            using var workbook = new XLWorkbook();

            var worksheet =
                workbook.Worksheets.Add(
                    "Demanding Products");


            worksheet.Cell(1, 1).Value =
                "Daily Demanding Product Report";


            worksheet.Cell(2, 1).Value =
                "Start Date";

            worksheet.Cell(2, 2).Value =
                model.StartDate?
                    .ToString("dd MMM yyyy")
                ?? "All";


            worksheet.Cell(3, 1).Value =
                "End Date";

            worksheet.Cell(3, 2).Value =
                model.EndDate?
                    .ToString("dd MMM yyyy")
                ?? "All";


            worksheet.Cell(5, 1).Value =
                "Date";

            worksheet.Cell(5, 2).Value =
                "Product";

            worksheet.Cell(5, 3).Value =
                "Category";

            worksheet.Cell(5, 4).Value =
                "Units Sold";

            worksheet.Cell(5, 5).Value =
                "Revenue";

            worksheet.Cell(5, 6).Value =
                "Demand %";


            int row = 6;


            foreach (var item in model.Products)
            {
                worksheet.Cell(row, 1).Value =
                    item.Date;

                worksheet.Cell(row, 2).Value =
                    item.ProductName;

                worksheet.Cell(row, 3).Value =
                    item.CategoryName;

                worksheet.Cell(row, 4).Value =
                    item.UnitsSold;

                worksheet.Cell(row, 5).Value =
                    item.Revenue;

                worksheet.Cell(row, 6).Value =
                    item.DemandPercentage / 100;

                worksheet.Cell(row, 6)
                    .Style.NumberFormat.Format =
                    "0.00%";

                row++;
            }


            worksheet.Columns()
                .AdjustToContents();


            using var stream =
                new MemoryStream();

            workbook.SaveAs(stream);

            return stream.ToArray();
        }


        // =========================================================
        // DAILY DEMANDING PRODUCTS - PDF
        // =========================================================

        public byte[] ExportDailyDemandingProductsPdf(
            DailyDemandingProductReportViewModel model)
        {
            var document =
                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(30);


                        page.Header()
                            .Text(
                                "Daily Demanding Product Report")
                            .FontSize(20)
                            .Bold();


                        page.Content()
                            .Table(table =>
                            {
                                table.ColumnsDefinition(
                                    columns =>
                                    {
                                        columns.RelativeColumn();

                                        columns.RelativeColumn();

                                        columns.RelativeColumn();

                                        columns.ConstantColumn(60);

                                        columns.ConstantColumn(80);

                                        columns.ConstantColumn(70);
                                    });


                                table.Header(header =>
                                {
                                    header.Cell()
                                        .Text("Date")
                                        .Bold();

                                    header.Cell()
                                        .Text("Product")
                                        .Bold();

                                    header.Cell()
                                        .Text("Category")
                                        .Bold();

                                    header.Cell()
                                        .Text("Units")
                                        .Bold();

                                    header.Cell()
                                        .Text("Revenue")
                                        .Bold();

                                    header.Cell()
                                        .Text("Demand %")
                                        .Bold();
                                });


                                foreach (var item in model.Products)
                                {
                                    table.Cell()
                                        .Text(
                                            item.Date
                                                .ToString(
                                                    "dd MMM yyyy"));

                                    table.Cell()
                                        .Text(
                                            item.ProductName);

                                    table.Cell()
                                        .Text(
                                            item.CategoryName);

                                    table.Cell()
                                        .Text(
                                            item.UnitsSold
                                                .ToString());

                                    table.Cell()
                                        .Text(
                                            $"৳ {item.Revenue:N2}");

                                    table.Cell()
                                        .Text(
                                            $"{item.DemandPercentage:0.00}%");
                                }

                            });

                    });
                });


            return document.GeneratePdf();
        }
// =========================================================
// AI COMPLAINT ROUTING - EXCEL
// =========================================================

public byte[] ExportComplaintRoutingExcel(
    ComplaintRoutingReportViewModel model)
        {
            using var workbook = new XLWorkbook();

            var worksheet =
                workbook.Worksheets.Add(
                    "Complaint Routing");


            worksheet.Cell(1, 1).Value =
                "AI Complaint Routing Report";


            worksheet.Cell(2, 1).Value =
                "Start Date";

            worksheet.Cell(2, 2).Value =
                model.StartDate?
                    .ToString("dd MMM yyyy")
                ?? "All";


            worksheet.Cell(3, 1).Value =
                "End Date";

            worksheet.Cell(3, 2).Value =
                model.EndDate?
                    .ToString("dd MMM yyyy")
                ?? "All";


            worksheet.Cell(5, 1).Value =
                "Complaint Code";

            worksheet.Cell(5, 2).Value =
                "Date";

            worksheet.Cell(5, 3).Value =
                "Customer";

            worksheet.Cell(5, 4).Value =
                "Complaint";

            worksheet.Cell(5, 5).Value =
                "Category";

            worksheet.Cell(5, 6).Value =
                "Similarity Score";

            worksheet.Cell(5, 7).Value =
                "Routing Status";


            int row = 6;


            foreach (var item in model.Complaints)
            {
                worksheet.Cell(row, 1).Value =
                    item.ComplaintCode;

                worksheet.Cell(row, 2).Value =
                    item.CreatedAt;

                worksheet.Cell(row, 3).Value =
                    item.CustomerName;

                worksheet.Cell(row, 4).Value =
                    item.ComplaintText;

                worksheet.Cell(row, 5).Value =
                    item.CategoryName;

                worksheet.Cell(row, 6).Value =
                    item.SimilarityScore;

                worksheet.Cell(row, 7).Value =
                    item.RoutingStatus;

                row++;
            }


            worksheet.Columns()
                .AdjustToContents();


            // Keep complaint text readable

            worksheet.Column(4)
                .Width = 50;


            worksheet.Column(4)
                .Style.Alignment.WrapText = true;


            using var stream =
                new MemoryStream();

            workbook.SaveAs(stream);

            return stream.ToArray();
        }


        // =========================================================
        // AI COMPLAINT ROUTING - PDF
        // =========================================================

        public byte[] ExportComplaintRoutingPdf(
            ComplaintRoutingReportViewModel model)
        {
            var document =
                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(25);


                        page.Header()
                            .Text(
                                "AI Complaint Routing Report")
                            .FontSize(20)
                            .Bold();


                        page.Content()
                            .Table(table =>
                            {
                                table.ColumnsDefinition(
                                    columns =>
                                    {
                                        columns.ConstantColumn(65);

                                        columns.ConstantColumn(70);

                                        columns.RelativeColumn();

                                        columns.RelativeColumn();

                                        columns.ConstantColumn(75);

                                        columns.ConstantColumn(70);

                                        columns.ConstantColumn(80);
                                    });


                                table.Header(header =>
                                {
                                    header.Cell()
                                        .Text("Code")
                                        .Bold();

                                    header.Cell()
                                        .Text("Date")
                                        .Bold();

                                    header.Cell()
                                        .Text("Customer")
                                        .Bold();

                                    header.Cell()
                                        .Text("Complaint")
                                        .Bold();

                                    header.Cell()
                                        .Text("Category")
                                        .Bold();

                                    header.Cell()
                                        .Text("Score")
                                        .Bold();

                                    header.Cell()
                                        .Text("Status")
                                        .Bold();
                                });


                                foreach (var item in model.Complaints)
                                {
                                    table.Cell()
                                        .Text(
                                            item.ComplaintCode);

                                    table.Cell()
                                        .Text(
                                            item.CreatedAt
                                                .ToString(
                                                    "dd/MM/yyyy"));

                                    table.Cell()
                                        .Text(
                                            item.CustomerName);

                                    table.Cell()
                                        .Text(
                                            item.ComplaintText);

                                    table.Cell()
                                        .Text(
                                            item.CategoryName);

                                    table.Cell()
                                        .Text(
                                            $"{item.SimilarityScore:0.00}");

                                    table.Cell()
                                        .Text(
                                            item.RoutingStatus);
                                }

                            });

                    });
                });


            return document.GeneratePdf();
        }




    }
}

