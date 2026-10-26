using System.Text.Json;
using Orders.Application.Confirmation;
using Orders.Domain.Confirmation;

namespace Orders.Application.UnitTests;

public sealed class ConfirmationRuleAdminHandlerTests
{
    private const string _large = """[{ "kind": "TotalAbove", "amount": 5000, "currency": "BRL" }]""";

    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly IConfirmationRuleRepository _repository = Substitute.For<IConfirmationRuleRepository>();
    private readonly IActiveConfirmationRules _active = Substitute.For<IActiveConfirmationRules>();
    private readonly TimeProvider _clock = new FixedTimeProvider(_now);

    public ConfirmationRuleAdminHandlerTests()
    {
        _repository.AddVersionAsync(default!, Token).ReturnsForAnyArgs(true);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Create_ValidRule_StoresVersionOneAndInvalidatesTheCache()
    {
        var result = await new CreateConfirmationRuleHandler(_repository, _active, _clock, new CreateConfirmationRuleCommandValidator())
            .HandleAsync(new CreateConfirmationRuleCommand(Definition(), "admin-1"), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe(1);
        result.Value.Id.ShouldNotBe(Guid.Empty);
        result.Value.Action.ShouldBe("Hold");
        result.Value.CreatedBy.ShouldBe("admin-1");
        result.Value.CreatedAt.ShouldBe(_now);
        await _repository.Received(1).AddVersionAsync(Arg.Is<ConfirmationRuleDto>(rule => rule.Version == 1 && rule.Name == "Large BRL order"), Token);
        _active.Received(1).Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{ "kind": "Nope" }]""")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": 5, "currency": "BRL" }]""")]
    [InlineData("""[{ "kind": "TotalAbove", "amount": -1, "currency": "BRL" }]""")]
    [InlineData("""{ "kind": "FirstOrderOfClient" }""")]
    public async Task Create_InvalidConditions_IsAValidationErrorAndWritesNothing(string? conditions)
    {
        var result = await new CreateConfirmationRuleHandler(_repository, _active, _clock, new CreateConfirmationRuleCommandValidator())
            .HandleAsync(new CreateConfirmationRuleCommand(Definition() with { Conditions = conditions }, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("conditions");
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
        _active.DidNotReceive().Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null, "Hold")]
    [InlineData(" ", "Hold")]
    [InlineData("x", null)]
    [InlineData("x", "Block")]
    [InlineData("x", "0")]
    public async Task Create_BlankNameOrUnknownAction_IsAValidationError(string? name, string? action)
    {
        var result = await new CreateConfirmationRuleHandler(_repository, _active, _clock, new CreateConfirmationRuleCommandValidator())
            .HandleAsync(new CreateConfirmationRuleCommand(Definition() with { Name = name, Action = action }, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_CurrentVersion_StoresTheNextVersionAndInvalidatesTheCache()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await ReplaceHandler().HandleAsync(
            new ReplaceConfirmationRuleCommand(_id, "\"3\"", Definition() with { Name = "Renamed" }, "admin-2"), Token);

        result.Value.Version.ShouldBe(4);
        result.Value.Name.ShouldBe("Renamed");
        result.Value.CreatedBy.ShouldBe("admin-2");
        await _repository.Received(1).AddVersionAsync(Arg.Is<ConfirmationRuleDto>(rule => rule.Id == _id && rule.Version == 4), Token);
        _active.Received(1).Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("\"2\"")]
    [InlineData("W/\"9\"")]
    public async Task Replace_StaleVersion_IsPreconditionFailedAndWritesNothing(string ifMatch)
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, ifMatch, Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
        _active.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_WithoutIfMatch_IsPreconditionRequired()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        (await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, null, Definition(), "admin-2"), Token))
            .Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_UnknownRuleOrMalformedIfMatchOrInvalidConditions_AreNotFoundAndValidationErrors()
    {
        (await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, "\"1\"", Definition(), "admin-2"), Token))
            .Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, "one", Definition(), "admin-2"), Token))
            .Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, "\"1\"", Definition() with { Conditions = "[]" }, "admin-2"), Token))
            .Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_ConcurrentEditTookTheNextVersion_IsPreconditionFailed()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));
        _repository.AddVersionAsync(default!, Token).ReturnsForAnyArgs(false);

        var result = await ReplaceHandler().HandleAsync(new ReplaceConfirmationRuleCommand(_id, "\"3\"", Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        _active.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_ActiveRule_StoresAnInactiveNextVersionKeepingTheRest()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await new DeactivateConfirmationRuleHandler(_repository, _active, _clock, new DeactivateConfirmationRuleCommandValidator())
            .HandleAsync(new DeactivateConfirmationRuleCommand(_id, "\"3\"", "admin-2"), Token);

        result.Value.Version.ShouldBe(4);
        result.Value.IsActive.ShouldBeFalse();
        result.Value.Name.ShouldBe("Large BRL order");
        result.Value.Conditions.GetArrayLength().ShouldBe(1);
        _active.Received(1).Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_StaleUnknownOrWithoutIfMatch_AreHandled()
    {
        var handler = new DeactivateConfirmationRuleHandler(_repository, _active, _clock, new DeactivateConfirmationRuleCommandValidator());
        (await handler.HandleAsync(new DeactivateConfirmationRuleCommand(_id, null, "admin-2"), Token)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));
        (await handler.HandleAsync(new DeactivateConfirmationRuleCommand(_id, "\"1\"", "admin-2"), Token)).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        (await handler.HandleAsync(new DeactivateConfirmationRuleCommand(_id, null, "admin-2"), Token)).Value.Version.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAndList_ReturnTheLatestVersions()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));
        _repository.ListLatestAsync(true, Token).Returns([Stored(version: 3)]);

        (await new GetConfirmationRuleHandler(_repository, new GetConfirmationRuleQueryValidator()).HandleAsync(new GetConfirmationRuleQuery(_id), Token)).Value.Version.ShouldBe(3);
        (await new GetConfirmationRuleHandler(_repository, new GetConfirmationRuleQueryValidator()).HandleAsync(new GetConfirmationRuleQuery(Guid.NewGuid()), Token))
            .Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new ListConfirmationRulesHandler(_repository, new ListConfirmationRulesQueryValidator()).HandleAsync(new ListConfirmationRulesQuery(true), Token))
            .Value.ShouldHaveSingleItem().Id.ShouldBe(_id);
    }

    private static ConfirmationRuleDefinition Definition() => new("Large BRL order", "Hold", _large, 10, true);

    private static ConfirmationRuleDto Stored(int version) => new(
        _id, version, "Large BRL order", "Hold", JsonDocument.Parse(_large).RootElement.Clone(), 10, true, "admin-1", _now.AddDays(-1));

    private ReplaceConfirmationRuleHandler ReplaceHandler() => new(_repository, _active, _clock, new ReplaceConfirmationRuleCommandValidator());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
