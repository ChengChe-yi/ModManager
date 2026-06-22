using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModManager.Application.Services;
using ModManager.Application.ViewModels;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            services.AddSingleton<IPathManager, PathManager>();
            services.AddScoped<IModCategoryService, ModCategoryService>();
            services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
            services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
            services.AddSingleton<IModDataService, ModDataService>();
            services.AddSingleton<IModPresetService, ModPresetService>();

            // ========== ViewModels ==========
            services.AddScoped<ModManageViewModel>();
            services.AddScoped<CharacterManageViewModel>();

            // ========== 日志 ==========
            services.AddLogging(builder =>
            {
                builder.AddDebug();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            return services;
        }

    }
}
