using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RetailSalesStockAnalytics.Data
{
    public static class DbInitializer
    {
        public static async Task SeedManagerAsync(IServiceProvider serviceProvider)
        {
            await SeedRolesAndUsersAsync(serviceProvider);
        }

        public static async Task SeedRolesAndUsersAsync(IServiceProvider serviceProvider)
        {
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = serviceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var configuration = serviceProvider.GetService<IConfiguration>();
            var environment = serviceProvider.GetService<IWebHostEnvironment>();
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("DbInitializer");

            bool isDevelopment = environment?.IsDevelopment() ?? false;

            // 1. Ensure required application roles exist
            string[] roles = { "Admin", "Manager", "Customer" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                    if (!roleResult.Succeeded)
                    {
                        var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                        throw new Exception($"Failed to create role '{role}': {errors}");
                    }
                }
            }

            // 2. Manager Account Initialization
            var managerEmail = configuration?["DefaultManager:Email"];
            var managerPassword = configuration?["DefaultManager:Password"];

            if (string.IsNullOrWhiteSpace(managerEmail) || string.IsNullOrWhiteSpace(managerPassword))
            {
                logger?.LogWarning("Privileged manager account initialization was skipped: DefaultManager:Email or DefaultManager:Password is not configured. Configure credentials securely via User Secrets ('dotnet user-secrets set \"DefaultManager:Password\" \"<password>\"') or environment variables ('DefaultManager__Password').");
            }
            else
            {
                var existingManager = await userManager.FindByEmailAsync(managerEmail);
                if (existingManager == null)
                {
                    var manager = new IdentityUser
                    {
                        UserName = managerEmail,
                        Email = managerEmail,
                        EmailConfirmed = true
                    };

                    var result = await userManager.CreateAsync(manager, managerPassword);
                    if (!result.Succeeded)
                    {
                        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                        throw new Exception($"Manager account creation failed: {errors}");
                    }
                    existingManager = manager;
                }

                if (existingManager != null && !await userManager.IsInRoleAsync(existingManager, "Manager"))
                {
                    await userManager.AddToRoleAsync(existingManager, "Manager");
                }
            }

            // 3. Admin Account Initialization
            var adminEmail = configuration?["DefaultAdmin:Email"];
            var adminPassword = configuration?["DefaultAdmin:Password"];

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                logger?.LogWarning("Privileged administrator account initialization was skipped: DefaultAdmin:Email or DefaultAdmin:Password is not configured. Configure credentials securely via User Secrets ('dotnet user-secrets set \"DefaultAdmin:Password\" \"<password>\"') or environment variables ('DefaultAdmin__Password').");
            }
            else
            {
                var existingAdmin = await userManager.FindByEmailAsync(adminEmail);
                if (existingAdmin == null)
                {
                    var admin = new IdentityUser
                    {
                        UserName = adminEmail,
                        Email = adminEmail,
                        EmailConfirmed = true
                    };

                    var result = await userManager.CreateAsync(admin, adminPassword);
                    if (!result.Succeeded)
                    {
                        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                        throw new Exception($"Admin account creation failed: {errors}");
                    }
                    existingAdmin = admin;
                }

                if (existingAdmin != null && !await userManager.IsInRoleAsync(existingAdmin, "Admin"))
                {
                    await userManager.AddToRoleAsync(existingAdmin, "Admin");
                }
            }
        }
    }
}
