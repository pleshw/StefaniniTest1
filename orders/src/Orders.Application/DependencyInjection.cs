using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Application.Pricing;

namespace Orders.Application;

public static class DependencyInjection
{
    /// <summary>Registers the services and every FluentValidation validator of this assembly.</summary>
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CartValidator>();
        services.AddScoped<PricingService>();
        services.AddScoped<IdempotencyService>();
        services.AddScoped<OrderDrafter>();
        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<QuoteOrderHandler>();
        services.AddScoped<ListOrdersHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<GetOrderHistoryHandler>();

        return services;
    }
}
