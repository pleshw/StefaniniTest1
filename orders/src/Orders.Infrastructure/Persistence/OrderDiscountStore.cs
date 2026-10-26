using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Application.Discounts;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Infrastructure.Persistence;

internal sealed class OrderDiscountStore(OrdersDbContext context) : IOrderDiscountStore
{
    public async Task AddAsync(Order order, IReadOnlyList<AppliedDiscount> discounts, CancellationToken cancellationToken)
    {
        if (discounts.Count == 0)
        {
            return;
        }

        foreach (var discount in discounts)
        {
            context.OrderDiscounts.Add(new OrderDiscountRecord
            {
                OrderId = order.Id,
                DiscountRuleId = discount.RuleId,
                RuleVersion = discount.RuleVersion,
                Amount = discount.Amount.Amount,
            });
            context.DiscountRedemptions.Add(new DiscountRedemptionRecord
            {
                DiscountRuleId = discount.RuleId,
                ClientId = order.Client.Id,
                OrderId = order.Id,
                OncePerClient = discount.OncePerClient,
                RedeemedAt = order.CreatedAt,
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DiscountRedemptionRecord.OncePerClientIndex,
        })
        {
            throw new DiscountAlreadyUsedException("A once-per-client discount in this order was already used by the client.", exception);
        }
    }
}
