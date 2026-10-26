using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Pricing;

namespace Orders.Application;

public static class DependencyInjection
{
    /// <summary>Registers the services and every FluentValidation validator of this assembly.</summary>
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CartValidator>();
        services.AddScoped<PricingService>();

        return services;
    }
}
