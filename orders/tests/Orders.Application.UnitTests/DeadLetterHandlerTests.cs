using System.Text.Json;
using NSubstitute.ExceptionExtensions;
using Orders.Application.DeadLetters;
using Orders.Application.Ordering;

namespace Orders.Application.UnitTests;

public sealed class DeadLetterHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _order = Guid.Parse("3f9a1c2b-7d40-4e8a-b1c2-9d8e7f6a5b4c");

    private readonly IDeadLetters _queue = Substitute.For<IDeadLetters>();
    private readonly IOrderQueries _orders = Substitute.For<IOrderQueries>();
    private readonly IOrderEventLog _log = Substitute.For<IOrderEventLog>();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task List_NoLimit_PeeksTheHundredMessageMaximum()
    {
        _queue.PeekAsync(100, Token).Returns([Letter("m-1")]);

        var result = await new ListDeadLettersHandler(_queue, new ListDeadLettersQueryValidator()).HandleAsync(new ListDeadLettersQuery(null), Token);

        result.Value.ShouldHaveSingleItem().MessageId.ShouldBe("m-1");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public async Task List_LimitOutsideOneToAHundred_IsAValidationError(int limit)
    {
        var result = await new ListDeadLettersHandler(_queue, new ListDeadLettersQueryValidator()).HandleAsync(new ListDeadLettersQuery(limit), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _queue.DidNotReceiveWithAnyArgs().PeekAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task List_LimitWithinRange_IsPassedOn()
    {
        _queue.PeekAsync(7, Token).Returns([]);

        (await new ListDeadLettersHandler(_queue, new ListDeadLettersQueryValidator()).HandleAsync(new ListDeadLettersQuery(7), Token)).IsSuccess.ShouldBeTrue();
        await _queue.Received(1).PeekAsync(7, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replay_MessageInTheQueue_RepublishesAndLogsTheReplayForTheOrder()
    {
        var letter = Letter("m-1");
        var replay = new DeadLetterReplay(letter, "m-2", "orders.events", "order.placed", "admin-1");
        _orders.GetAsync(_order, null, Token).Returns(OrderDto());
        _queue.ReplayAsync("m-1", "admin-1", Arg.Any<DeadLetterReplayHooks>(), Token).Returns(async call =>
        {
            var hooks = call.Arg<DeadLetterReplayHooks>();
            (await hooks.Check(letter, Token)).ShouldBeNull();
            await hooks.Record(replay, Token);

            return Result.Ok(replay);
        });

        var result = await Handler().HandleAsync(new ReplayDeadLetterCommand("m-1", "admin-1"), Token);

        result.Value.ShouldBe(replay);
        await _log.Received(1).AppendEntryAsync(
            Arg.Is<OrderEventLogEntry>(entry => entry.OrderId == _order && entry.Type == "DeadLetterReplayed.v1" && entry.OccurredAt == _now), Token);
        var payload = (string)_log.ReceivedCalls().Single().GetArguments().OfType<OrderEventLogEntry>().Single().Payload;
        using var document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("messageId").GetString().ShouldBe("m-1");
        document.RootElement.GetProperty("newMessageId").GetString().ShouldBe("m-2");
        document.RootElement.GetProperty("exchange").GetString().ShouldBe("orders.events");
        document.RootElement.GetProperty("routingKey").GetString().ShouldBe("order.placed");
        document.RootElement.GetProperty("replayedBy").GetString().ShouldBe("admin-1");
        document.RootElement.GetProperty("exceptionType").GetString().ShouldBe("System.InvalidOperationException");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replay_MessageNotInTheQueue_IsNotFound()
    {
        _queue.ReplayAsync("m-9", "admin-1", Arg.Any<DeadLetterReplayHooks>(), Token)
            .Returns(Result.Fail<DeadLetterReplay>(ErrorKind.NotFound, "No dead letter has the message id m-9."));

        var result = await Handler().HandleAsync(new ReplayDeadLetterCommand("m-9", "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _log.DidNotReceiveWithAnyArgs().AppendEntryAsync(default!, Token);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{ "number": "ORD-1" }""")]
    [InlineData("""{ "orderId": "not-a-guid" }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task Replay_PayloadWithoutAnOrderId_IsVetoedBeforeAnythingIsPublished(string payload)
    {
        ResultError? veto = null;
        _queue.ReplayAsync("m-1", "admin-1", Arg.Any<DeadLetterReplayHooks>(), Token).Returns(async call =>
        {
            veto = await call.Arg<DeadLetterReplayHooks>().Check(Letter("m-1", payload), Token);

            return Result.Fail<DeadLetterReplay>(veto!.Kind, veto.Message);
        });

        var result = await Handler().HandleAsync(new ReplayDeadLetterCommand("m-1", "admin-1"), Token);

        veto!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        await _log.DidNotReceiveWithAnyArgs().AppendEntryAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replay_OrderNoLongerExists_IsVetoed()
    {
        _orders.GetAsync(_order, null, Token).Returns((OrderDto?)null);
        ResultError? veto = null;
        _queue.ReplayAsync("m-1", "admin-1", Arg.Any<DeadLetterReplayHooks>(), Token).Returns(async call =>
        {
            veto = await call.Arg<DeadLetterReplayHooks>().Check(Letter("m-1"), Token);

            return Result.Fail<DeadLetterReplay>(veto!.Kind, veto.Message);
        });

        await Handler().HandleAsync(new ReplayDeadLetterCommand("m-1", "admin-1"), Token);

        veto!.Kind.ShouldBe(ErrorKind.Unprocessable);
        veto.Message.ShouldContain(_order.ToString());
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("", "admin-1")]
    [InlineData(" ", "admin-1")]
    [InlineData("m-1", "")]
    public async Task Replay_BlankMessageIdOrActor_IsAValidationError(string messageId, string actor)
    {
        var result = await Handler().HandleAsync(new ReplayDeadLetterCommand(messageId, actor), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _queue.DidNotReceiveWithAnyArgs().ReplayAsync(default!, default!, default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAndReplay_BrokerUnreachable_AreMessagingUnavailable()
    {
        _queue.PeekAsync(default, Token).ThrowsAsyncForAnyArgs(new MessagingUnavailableException("down", new InvalidOperationException()));
        _queue.ReplayAsync(default!, default!, default!, Token).ThrowsAsyncForAnyArgs(new MessagingUnavailableException("down", new InvalidOperationException()));

        (await new ListDeadLettersHandler(_queue, new ListDeadLettersQueryValidator()).HandleAsync(new ListDeadLettersQuery(null), Token))
            .Error!.Kind.ShouldBe(ErrorKind.MessagingUnavailable);
        (await Handler().HandleAsync(new ReplayDeadLetterCommand("m-1", "admin-1"), Token)).Error!.Kind.ShouldBe(ErrorKind.MessagingUnavailable);
    }

    private ReplayDeadLetterHandler Handler() => new(_queue, _orders, _log, new FixedTimeProvider(_now), new ReplayDeadLetterCommandValidator());

    private static DeadLetterDto Letter(string messageId, string? payload = null) => new(
        messageId,
        "Orders.Contracts.OrderPlacedV1",
        "rabbitmq://queue/orders.worker.placed",
        "System.InvalidOperationException",
        "boom",
        4,
        payload ?? $$"""{ "orderId": "{{_order}}", "number": "ORD-1" }""",
        new Dictionary<string, string> { ["x-attempts"] = "4" });

    private static OrderDto OrderDto() => new(
        _order, "ORD-1", new ClientDto(Guid.NewGuid(), "Ana"), "Pending", "shop", "BRL", 1, 10m, 0m, 10m, [], _now, 0);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
