using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration.Json;

namespace RetailSalesStockAnalytics
{
    public static class ConfigurationHelper
    {
        /// <summary>
        /// Inserts 'appsettings.Local.json' into the configuration sources immediately
        /// after 'appsettings.{Environment}.json' (or 'appsettings.json'), ensuring that
        /// local overrides take precedence over base appsettings but yield to User Secrets,
        /// environment variables, and command-line arguments.
        /// </summary>
        public static void ConfigureLocalSettings(WebApplicationBuilder builder)
        {
            int localSettingsInsertIndex = -1;
            for (int i = 0; i < builder.Configuration.Sources.Count; i++)
            {
                if (builder.Configuration.Sources[i] is JsonConfigurationSource jsonSource &&
                    string.Equals(jsonSource.Path, $"appsettings.{builder.Environment.EnvironmentName}.json", StringComparison.OrdinalIgnoreCase))
                {
                    localSettingsInsertIndex = i + 1;
                    break;
                }
            }

            if (localSettingsInsertIndex == -1)
            {
                for (int i = 0; i < builder.Configuration.Sources.Count; i++)
                {
                    if (builder.Configuration.Sources[i] is JsonConfigurationSource jsonSource &&
                        string.Equals(jsonSource.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        localSettingsInsertIndex = i + 1;
                        break;
                    }
                }
            }

            var localConfig = new JsonConfigurationSource
            {
                Path = "appsettings.Local.json",
                Optional = true,
                ReloadOnChange = true
            };
            localConfig.ResolveFileProvider();

            if (localSettingsInsertIndex >= 0)
            {
                builder.Configuration.Sources.Insert(localSettingsInsertIndex, localConfig);
            }
            else
            {
                builder.Configuration.Sources.Add(localConfig);
            }
        }
    }
}

