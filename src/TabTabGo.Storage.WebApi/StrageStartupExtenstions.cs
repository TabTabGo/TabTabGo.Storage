using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Reflection;
using TabTabGo.Storage.Data.EF;

namespace TabTabGo.Storage.WebApi
{
    public static class StorageStartupExtensions
    {
      
        public static IServiceCollection RegisterStorageDBProvider(this IServiceCollection services)
        {
            services.AddScoped<IRepositoryProvider, StorageDbProvider>();
            return services;
        }

        public static IMvcBuilder RegisterStorageControllers(this IMvcBuilder services)
        {
            var assemblyRef = Assembly.GetExecutingAssembly();
            services.AddApplicationPart(assemblyRef).AddControllersAsServices();
            return services;
        }

    }
}
