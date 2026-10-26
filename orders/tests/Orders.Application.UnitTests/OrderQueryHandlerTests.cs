using Orders.Application.Ordering;
using Orders.Domain;

namespace Orders.Application.UnitTests;

public sealed class OrderQueryHandlerTests
{
    private static readonly Guid _shopper = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _orderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly IOrderQueries _queries = Substitute.For<IOrderQueries>();
    private readonly ListOrdersHandler _list;
    private readonly GetOrderHandler _get;
    private readonly GetOrderHistoryHandler _history;

    public OrderQueryHandlerTests()
    {
        _queries.ListAsync(default!, Token).ReturnsForAnyArgs([]);
        _list = new ListOrdersHandler(_queries, new ListOrdersQueryValidator());
        _get = new GetOrderHandler(_queries, new GetOrderQueryValidator());
        _history = new GetOrderHistoryHandler(_queries, new GetOrderHistoryQueryValidator());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_NoFilters_AsksForTheDefaultLimitPlusOne()
    {
        var result = await _list.HandleAsync(new ListOrdersQuery(null, null, null, null, null, null, null, null), Token);

        result.IsSuccess.ShouldBeTrue();
        await _queries.Received(1).ListAsync(
            new OrderListFilter(null, null, null, null, null, null, ListOrdersHandler.DefaultLimit + 1), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_EveryFilter_PassesItToTheQueryNormalized()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-3));
        var to = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var after = new OrderCursor(_now, _orderId);

        await _list.HandleAsync(new ListOrdersQuery(null, _shopper, "onhold", from, to, "  ana  ", after.Encode(), 5), Token);

        await _queries.Received(1).ListAsync(
            new OrderListFilter(_shopper, OrderStatus.OnHold, from.ToUniversalTime(), to, "ana", after, 6), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_Shopper_IsAlwaysRestrictedToTheirOwnOrders()
    {
        await _list.HandleAsync(new ListOrdersQuery(_shopper, null, null, null, null, null, null, null), Token);

        await _queries.Received(1).ListAsync(Arg.Is<OrderListFilter>(filter => filter.ClientId == _shopper), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_ShopperAskingForAnotherClient_IsForbidden()
    {
        var result = await _list.HandleAsync(new ListOrdersQuery(_shopper, Guid.NewGuid(), null, null, null, null, null, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _queries.DidNotReceiveWithAnyArgs().ListAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_MoreRowsThanTheLimit_ReturnsTheLimitAndACursorAfterTheLastOne()
    {
        var rows = Enumerable.Range(0, 3).Select(index => Summary(Guid.NewGuid(), _now.AddMinutes(-index))).ToList();
        _queries.ListAsync(default!, Token).ReturnsForAnyArgs(rows);

        var page = (await _list.HandleAsync(new ListOrdersQuery(null, null, null, null, null, null, null, 2), Token)).Value;

        page.Items.Count.ShouldBe(2);
        page.Next.ShouldBe(new OrderCursor(rows[1].CreatedAt, rows[1].Id).Encode());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_RowsWithinTheLimit_HasNoNextCursor()
    {
        _queries.ListAsync(default!, Token).ReturnsForAnyArgs([Summary(_orderId, _now)]);

        var page = (await _list.HandleAsync(new ListOrdersQuery(null, null, null, null, null, null, null, 2), Token)).Value;

        page.Items.ShouldHaveSingleItem();
        page.Next.ShouldBeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, null, null)]
    [InlineData(101, null, null)]
    [InlineData(10, "Shipped", null)]
    [InlineData(10, null, "not-a-cursor")]
    public async Task ListAsync_InvalidLimitStatusOrCursor_ReturnsValidationError(int limit, string? status, string? after)
    {
        var result = await _list.HandleAsync(new ListOrdersQuery(null, null, status, null, null, null, after, limit), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _queries.DidNotReceiveWithAnyArgs().ListAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_FromAfterTo_ReturnsValidationError()
    {
        var result = await _list.HandleAsync(new ListOrdersQuery(null, null, null, _now, _now.AddDays(-1), null, null, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_SearchTextTooLong_ReturnsValidationError()
    {
        var result = await _list.HandleAsync(new ListOrdersQuery(null, null, null, null, null, new string('a', 201), null, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_BlankSearchText_IsNoSearch()
    {
        await _list.HandleAsync(new ListOrdersQuery(null, null, null, null, null, "   ", null, null), Token);

        await _queries.Received(1).ListAsync(Arg.Is<OrderListFilter>(filter => filter.Search == null), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_OwnOrder_ReturnsIt()
    {
        var order = Detail();
        _queries.GetAsync(_orderId, _shopper, Token).Returns(order);

        var result = await _get.HandleAsync(new GetOrderQuery(_orderId, _shopper), Token);

        result.Value.ShouldBe(order);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_MissingOrAnotherClientsOrder_ReturnsNotFound()
    {
        _queries.GetAsync(_orderId, _shopper, Token).Returns((OrderDto?)null);

        var result = await _get.HandleAsync(new GetOrderQuery(_orderId, _shopper), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_EmptyId_ReturnsValidationError()
    {
        var result = await _get.HandleAsync(new GetOrderQuery(Guid.Empty, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetHistoryAsync_OwnOrder_ReturnsTheEntries()
    {
        IReadOnlyList<OrderStatusChangeDto> entries = [new(1, null, "Pending", null, "shopper-1", _now)];
        _queries.GetHistoryAsync(_orderId, _shopper, Token).Returns(entries);

        var result = await _history.HandleAsync(new GetOrderHistoryQuery(_orderId, _shopper), Token);

        result.Value.ShouldBe(entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetHistoryAsync_MissingOrAnotherClientsOrder_ReturnsNotFound()
    {
        _queries.GetHistoryAsync(_orderId, _shopper, Token).Returns((IReadOnlyList<OrderStatusChangeDto>?)null);

        var result = await _history.HandleAsync(new GetOrderHistoryQuery(_orderId, _shopper), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetHistoryAsync_EmptyId_ReturnsValidationError()
    {
        var result = await _history.HandleAsync(new GetOrderHistoryQuery(Guid.Empty, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderCursor_EncodeThenDecode_RoundTrips()
    {
        var cursor = new OrderCursor(_now.AddTicks(10), _orderId);

        OrderCursor.TryDecode(cursor.Encode(), out var decoded).ShouldBeTrue();

        decoded.ShouldBe(cursor);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("WyJ4Il0")]
    public void OrderCursor_TryDecode_GarbageIsRejected(string value)
    {
        OrderCursor.TryDecode(value, out var cursor).ShouldBeFalse();
        cursor.ShouldBeNull();
    }

    private static OrderSummaryDto Summary(Guid id, DateTimeOffset createdAt) =>
        new(id, "ORD-2026-000000000001", new ClientDto(_shopper, "Ana"), "Pending", "shop", "BRL", 1, 10m, 0m, 10m, createdAt);

    private static OrderDto Detail() =>
        new(_orderId, "ORD-2026-000000000001", new ClientDto(_shopper, "Ana"), "Pending", "shop", "BRL", 1, 10m, 0m, 10m, [], _now);

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
