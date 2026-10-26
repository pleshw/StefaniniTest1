namespace Orders.Domain;

public enum OrderStatus
{
    Pending,
    OnHold,
    Confirmed,
    Blocked,
    Cancelled,
    Failed,
}
