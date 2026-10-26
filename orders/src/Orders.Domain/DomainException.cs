namespace Orders.Domain;

public class DomainException(string message) : Exception(message);

/// <summary>A status change the state machine does not allow.</summary>
public sealed class InvalidStatusTransitionException(OrderStatus from, OrderStatus to)
    : DomainException($"An order cannot change from {from} to {to}.")
{
    public OrderStatus From { get; } = from;

    public OrderStatus To { get; } = to;
}
