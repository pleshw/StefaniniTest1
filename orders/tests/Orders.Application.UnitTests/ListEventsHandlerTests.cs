using System.Text.Json;
using Orders.Application.Events;

namespace Orders.Application.UnitTests;

public sealed class ListEventsHandlerTests
{
    private static readonly DateTimeOffset _at = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _order = Guid.Parse("3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c");

    private readonly IOrderEventLogQueries _queries = Substitute.For<IOrderEventLogQueries>();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_NoParameters_ReadsTheDefaultPageSizePlusOneWithoutFilters()
    {
        _queries.ListAsync(Arg.Any<EventFilter>(), Token).Returns([Event(1)]);

        var result = await Handler().HandleAsync(new ListEventsQuery(null, null, null, null), Token);

        result.Value.Items.ShouldHaveSingleItem().Id.ShouldBe(1);
        result.Value.Next.ShouldBeNull();
        await _queries.Received(1).ListAsync(new EventFilter(null, null, null, ListEventsHandler.DefaultLimit + 1), Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_MoreRowsThanTheLimit_ReturnsTheLimitAndACursorForTheNextPage()
    {
        _queries.ListAsync(Arg.Any<EventFilter>(), Token).Returns([Event(1), Event(2), Event(3)]);

        var result = await Handler().HandleAsync(new ListEventsQuery(null, 2, null, null), Token);

        result.Value.Items.Select(item => item.Id).ShouldBe([1L, 2L]);
        EventCursor.TryDecode(result.Value.Next!, out var cursor).ShouldBeTrue();
        cursor.ShouldBe(new EventCursor(_at.AddMinutes(2), 2));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_AfterTypeAndOrderId_AreAppliedToTheFilter()
    {
        _queries.ListAsync(Arg.Any<EventFilter>(), Token).Returns([]);
        var after = new EventCursor(_at, 41).Encode();

        await Handler().HandleAsync(new ListEventsQuery(after, 10, "OrderStatusChanged.v1", _order), Token);

        await _queries.Received(1).ListAsync(new EventFilter(_order, "OrderStatusChanged.v1", new EventCursor(_at, 41), 11), Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task HandleAsync_LimitOutsideOneToAHundred_IsAValidationError(int limit)
    {
        var result = await Handler().HandleAsync(new ListEventsQuery(null, limit, null, null), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _queries.DidNotReceiveWithAnyArgs().ListAsync(default!, Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("e30")]
    public async Task HandleAsync_AfterThatIsNotACursor_IsAValidationError(string after)
    {
        (await Handler().HandleAsync(new ListEventsQuery(after, null, null, null), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleAsync_VeryLongTypeOrEmptyOrderId_IsAValidationError()
    {
        (await Handler().HandleAsync(new ListEventsQuery(null, null, new string('x', 101), null), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Handler().HandleAsync(new ListEventsQuery(null, null, null, Guid.Empty), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EventCursor_EncodeThenDecode_RoundTrips()
    {
        var cursor = new EventCursor(_at, long.MaxValue);

        EventCursor.TryDecode(cursor.Encode(), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);
    }

    private ListEventsHandler Handler() => new(_queries, new ListEventsQueryValidator());

    private static OrderEventDto Event(long id) => new(id, _order, "OrderStatusChanged.v1", JsonDocument.Parse("{}").RootElement.Clone(), _at.AddMinutes(id));

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
