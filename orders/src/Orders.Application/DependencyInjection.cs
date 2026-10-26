using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Discounts;
using Orders.Application.Idempotency;
using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain.Discounts;

namespace Orders.Application;

public static class DependencyInjection
{
    /// <summary>Registers the services and every FluentValidation validator of this assembly.</summary>
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CartValidator>();
        services.AddScoped<PricingService>();
        services.AddScoped<IdempotencyService>();
        services.AddSingleton(provider => new DiscountEngine(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<DiscountOptions>()?.MaxDiscountPercent ?? DiscountEngine.DefaultMaxDiscountPercent));
        services.AddScoped<OrderDrafter>();
        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<QuoteOrderHandler>();
        services.AddScoped<ListOrdersHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<GetOrderHistoryHandler>();
        services.AddScoped<ListDiscountRulesHandler>();
        services.AddScoped<GetDiscountRuleHandler>();
        services.AddScoped<CreateDiscountRuleHandler>();
        services.AddScoped<ReplaceDiscountRuleHandler>();
        services.AddScoped<DeactivateDiscountRuleHandler>();
        services.AddScoped<PreviewDiscountRuleHandler>();

        return services;
    }
}
