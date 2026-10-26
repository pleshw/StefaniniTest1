using Catalog.Application.Products;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Application;

public static class DependencyInjection
{
    /// <summary>Registers the handlers and every FluentValidation validator of this assembly.</summary>
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<UpsertProductCommandValidator>();
        services.AddScoped<ListProductsHandler>();
        services.AddScoped<GetProductHandler>();
        services.AddScoped<QueryPricesHandler>();
        services.AddScoped<UpsertProductHandler>();
        services.AddScoped<ChangePriceHandler>();
        services.AddScoped<DeactivateProductHandler>();

        return services;
    }
}
