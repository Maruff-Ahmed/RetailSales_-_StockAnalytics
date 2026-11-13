using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Services;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Support optional local development configuration (git-ignored) with proper precedence
ConfigurationHelper.ConfigureLocalSettings(builder);

// ============================================================
// DATABASE
// ============================================================

var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(defaultConnection) ||
    defaultConnection.Contains("YOUR_SERVER", StringComparison.OrdinalIgnoreCase) ||
    defaultConnection.Contains("YOUR_DATABASE", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Database connection string 'ConnectionStrings:DefaultConnection' is not configured. " +
        "Configure your local SQL Server connection string using .NET User Secrets ('dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<your-connection-string>\"'), " +
        "the environment variable 'ConnectionStrings__DefaultConnection', or a local development configuration file ('appsettings.Local.json').");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(defaultConnection));


// ============================================================
// ASP.NET CORE IDENTITY
// ============================================================

builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        // Email confirmation is not required for login
        options.SignIn.RequireConfirmedAccount = false;

        // Password requirements (Task 5.1 hardened)
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Account Lockout configuration (Task 5.1 hardened)
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});


// ============================================================
// MVC & CACHING
// ============================================================

builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();


// ============================================================
// APPLICATION SERVICES
// ============================================================

builder.Services.AddScoped<ISalesAnalyticsService, SalesAnalyticsService>();
builder.Services.AddScoped<ComplaintRoutingService>();
builder.Services.AddScoped<IComplaintCodeGenerator, ComplaintCodeGenerator>();
builder.Services.AddScoped<CustomerPurchasePatternService>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IStockTransactionService, StockTransactionService>();

builder.Services.AddScoped<ReportExportService>();
builder.Services.AddScoped<IReportDataService, ReportDataService>();
builder.Services.AddScoped<ISalesCheckoutService, SalesCheckoutService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;


var app = builder.Build();


// ============================================================
// DATABASE MIGRATIONS & SEEDING
// ============================================================

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        // 1. Ensure database schema is migrated to the latest version
        logger.LogInformation("Applying database migrations...");
        var connectionString = defaultConnection;
        var migrationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var migrationContext = new ApplicationDbContext(migrationOptions))
        {
            await migrationContext.Database.MigrateAsync();
        }
        logger.LogInformation("Database migrations applied successfully.");

        var context = services.GetRequiredService<ApplicationDbContext>();

        // 2. Seed product categories and AI complaint categories
        logger.LogInformation("Seeding complaint and product categories...");
        await ComplaintCategorySeeder.SeedAsync(context);
        logger.LogInformation("Category seeding completed successfully.");

        // 3. Seed default manager account
        logger.LogInformation("Seeding default manager account...");
        await DbInitializer.SeedManagerAsync(services);
        logger.LogInformation("Default manager account verification completed.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "A critical error occurred during database migration or seeding.");
        throw;
    }
}


// ============================================================
// HTTP REQUEST PIPELINE
// ============================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");

    app.UseHsts();
}


app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();


// ============================================================
// AUTHENTICATION & AUTHORIZATION
// ============================================================

app.UseAuthentication();

app.UseAuthorization();


// ============================================================
// STATIC ASSETS
// ============================================================

app.MapStaticAssets();


// ============================================================
// MVC ROUTING
// ============================================================

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


// ============================================================
// IDENTITY RAZOR PAGES
// ============================================================

app.MapRazorPages();


app.Run();
