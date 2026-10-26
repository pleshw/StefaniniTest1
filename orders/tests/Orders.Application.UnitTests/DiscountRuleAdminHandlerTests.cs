using System.Text.Json;
using Orders.Application.Catalog;
using Orders.Application.Discounts;
using Orders.Application.Ordering;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Discounts;

namespace Orders.Application.UnitTests;

public sealed class DiscountRuleAdminHandlerTests
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly Guid _id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Currency _brl = Currency.Create("BRL");
    private const string _percent = """{ "percent": 10, "appliesTo": "order" }""";

    private readonly IDiscountRuleRepository _repository = Substitute.For<IDiscountRuleRepository>();
    private readonly IActiveDiscountRules _active = Substitute.For<IActiveDiscountRules>();
    private readonly TimeProvider _clock = new FixedTimeProvider(_now);

    public DiscountRuleAdminHandlerTests()
    {
        _repository.AddVersionAsync(default!, Token).ReturnsForAnyArgs(true);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Create_ValidRule_StoresVersionOneAndInvalidatesTheCache()
    {
        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(Definition(), "admin-1"), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe(1);
        result.Value.Id.ShouldNotBe(Guid.Empty);
        result.Value.CreatedBy.ShouldBe("admin-1");
        result.Value.CreatedAt.ShouldBe(_now);
        result.Value.ValidFrom.ShouldBe(_now);
        await _repository.Received(1).AddVersionAsync(
            Arg.Is<DiscountRuleDto>(rule => rule.Version == 1 && rule.Name == "Ten percent" && rule.Type == "Percentage"), Token);
        _active.Received(1).Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("Percentage", """{ "percent": 0, "appliesTo": "order" }""")]
    [InlineData("Percentage", """{ "percent": 10, "appliesTo": "order", "surprise": 1 }""")]
    [InlineData("FixedAmount", """{ "amounts": [] }""")]
    [InlineData("BuyXGetY", """{ "buy": 0, "get": 1 }""")]
    [InlineData("Tiered", """{ "currency": "BRL", "tiers": [] }""")]
    [InlineData("Percentage", "not json")]
    [InlineData("Percentage", null)]
    [InlineData("Unknown", """{ "percent": 10 }""")]
    [InlineData(null, """{ "percent": 10 }""")]
    public async Task Create_InvalidTypeOrParameters_IsAValidationErrorAndWritesNothing(string? type, string? parameters)
    {
        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(Definition() with { Type = type, Parameters = parameters }, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
        _active.DidNotReceive().Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""[{ "kind": "Nope" }]""")]
    [InlineData("""[{ "kind": "MinSubtotal", "amount": -1, "currency": "BRL" }]""")]
    [InlineData("""{ "kind": "FirstOrderOfClient" }""")]
    public async Task Create_InvalidConditions_IsAValidationErrorAndWritesNothing(string conditions)
    {
        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(Definition() with { Conditions = conditions }, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("conditions");
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Create_MissingConditions_MeansNoConditions()
    {
        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(Definition() with { Conditions = null }, "admin-1"), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Conditions.GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Create_BlankName_IsAValidationError(string? name)
    {
        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(Definition() with { Name = name }, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Create_ValidToNotAfterValidFrom_IsAValidationError()
    {
        var definition = Definition() with { ValidFrom = _now, ValidTo = _now };

        var result = await CreateHandler().HandleAsync(new CreateDiscountRuleCommand(definition, "admin-1"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_CurrentVersion_StoresTheNextVersionAndInvalidatesTheCache()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await ReplaceHandler().HandleAsync(
            new ReplaceDiscountRuleCommand(_id, "\"3\"", Definition() with { Name = "Twenty" }, "admin-2"), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe(4);
        result.Value.Name.ShouldBe("Twenty");
        result.Value.CreatedBy.ShouldBe("admin-2");
        await _repository.Received(1).AddVersionAsync(Arg.Is<DiscountRuleDto>(rule => rule.Id == _id && rule.Version == 4), Token);
        _active.Received(1).Invalidate();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("\"2\"")]
    [InlineData("W/\"2\"")]
    public async Task Replace_StaleVersion_IsPreconditionFailedAndWritesNothing(string ifMatch)
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await ReplaceHandler().HandleAsync(new ReplaceDiscountRuleCommand(_id, ifMatch, Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
        _active.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_WithoutIfMatch_IsPreconditionRequired()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await ReplaceHandler().HandleAsync(new ReplaceDiscountRuleCommand(_id, null, Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionRequired);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_MalformedIfMatch_IsAValidationError()
    {
        var result = await ReplaceHandler().HandleAsync(new ReplaceDiscountRuleCommand(_id, "three", Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_UnknownRule_IsNotFound()
    {
        _repository.GetLatestAsync(_id, Token).Returns((DiscountRuleDto?)null);

        var result = await ReplaceHandler().HandleAsync(new ReplaceDiscountRuleCommand(_id, "\"1\"", Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_ConcurrentEditTookTheNextVersion_IsPreconditionFailed()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));
        _repository.AddVersionAsync(default!, Token).ReturnsForAnyArgs(false);

        var result = await ReplaceHandler().HandleAsync(new ReplaceDiscountRuleCommand(_id, "\"3\"", Definition(), "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        _active.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Replace_InvalidParameters_IsAValidationErrorBeforeAnyLookup()
    {
        var result = await ReplaceHandler().HandleAsync(
            new ReplaceDiscountRuleCommand(_id, "\"3\"", Definition() with { Parameters = """{ "percent": 500, "appliesTo": "order" }""" }, "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _repository.DidNotReceiveWithAnyArgs().GetLatestAsync(default, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_ActiveRule_StoresAnInactiveNextVersionKeepingTheRest()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await DeactivateHandler().HandleAsync(new DeactivateDiscountRuleCommand(_id, "\"3\"", "admin-2"), Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Version.ShouldBe(4);
        result.Value.IsActive.ShouldBeFalse();
        result.Value.Name.ShouldBe("Ten percent");
        result.Value.Parameters.GetProperty("percent").GetDecimal().ShouldBe(10m);
        _active.Received(1).Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_StaleVersion_IsPreconditionFailed()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await DeactivateHandler().HandleAsync(new DeactivateDiscountRuleCommand(_id, "\"1\"", "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        _active.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_WithoutIfMatch_DeactivatesTheLatestVersion()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await DeactivateHandler().HandleAsync(new DeactivateDiscountRuleCommand(_id, null, "admin-2"), Token);

        result.Value.Version.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Deactivate_UnknownRule_IsNotFound()
    {
        var result = await DeactivateHandler().HandleAsync(new DeactivateDiscountRuleCommand(_id, null, "admin-2"), Token);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Get_UnknownRule_IsNotFound()
    {
        var handler = new GetDiscountRuleHandler(_repository, new GetDiscountRuleQueryValidator());

        (await handler.HandleAsync(new GetDiscountRuleQuery(_id), Token)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await handler.HandleAsync(new GetDiscountRuleQuery(Guid.Empty), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Get_ExistingRule_ReturnsTheLatestVersion()
    {
        _repository.GetLatestAsync(_id, Token).Returns(Stored(version: 3));

        var result = await new GetDiscountRuleHandler(_repository, new GetDiscountRuleQueryValidator()).HandleAsync(new GetDiscountRuleQuery(_id), Token);

        result.Value.Version.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task List_PassesTheActiveFilterToTheRepository()
    {
        _repository.ListLatestAsync(true, Token).Returns([Stored(version: 1)]);

        var result = await new ListDiscountRulesHandler(_repository, new ListDiscountRulesQueryValidator()).HandleAsync(new ListDiscountRulesQuery(true), Token);

        result.Value.ShouldHaveSingleItem().Id.ShouldBe(_id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Preview_NoRule_EqualsTheQuoteOfTheSameCart()
    {
        var catalog = Catalog();
        var rules = Substitute.For<IActiveDiscountRules>();
        var stored = DiscountRule.Create(Guid.NewGuid(), 2, "stored", DiscountType.Percentage, _percent, "[]", 1, false, false, _now.AddDays(-1), null, true);
        rules.GetAsync(_brl, Token).Returns([stored]);
        var queries = Substitute.For<IOrderQueries>();
        var client = Guid.NewGuid();
        var preview = new PreviewDiscountRuleHandler(
            new PricingService(catalog, new CartValidator(new CartLineValidator())), rules, queries, new DiscountEngine(_clock), _clock, new PreviewDiscountRuleQueryValidator());
        var quote = new QuoteOrderHandler(
            new OrderDrafter(new PricingService(catalog, new CartValidator(new CartLineValidator())), rules, queries, new DiscountEngine(_clock), _clock),
            new QuoteOrderQueryValidator());

        var previewed = await preview.HandleAsync(new PreviewDiscountRuleQuery(client, "BRL", [new CartLine("KB-1", 2)], null), Token);
        var quoted = await quote.HandleAsync(new QuoteOrderQuery(client, "Preview", OrderChannel.Shop, "BRL", [new CartLine("KB-1", 2)]), Token);

        previewed.IsSuccess.ShouldBeTrue();
        previewed.Value.Quote.ShouldBeEquivalentTo(quoted.Value);
        previewed.Value.Quote.DiscountTotal.ShouldBe(4m);
        previewed.Value.Applied.ShouldHaveSingleItem().RuleId.ShouldBe(stored.Id);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Preview_CandidateRule_AppliesWithoutBeingStored()
    {
        var rules = Substitute.For<IActiveDiscountRules>();
        rules.GetAsync(_brl, Token).Returns([]);
        var preview = new PreviewDiscountRuleHandler(
            new PricingService(Catalog(), new CartValidator(new CartLineValidator())),
            rules, Substitute.For<IOrderQueries>(), new DiscountEngine(_clock), _clock, new PreviewDiscountRuleQueryValidator());

        var result = await preview.HandleAsync(new PreviewDiscountRuleQuery(null, "BRL", [new CartLine("KB-1", 2)], Definition()), Token);

        result.Value.Quote.DiscountTotal.ShouldBe(4m);
        result.Value.Quote.Total.ShouldBe(36m);
        await _repository.DidNotReceiveWithAnyArgs().AddVersionAsync(default!, Token);
        rules.DidNotReceive().Invalidate();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Preview_CandidateWithRuleId_ReplacesTheActiveRuleWithThatId()
    {
        var stored = DiscountRule.Create(_id, 2, "stored", DiscountType.Percentage, """{ "percent": 50, "appliesTo": "order" }""", "[]", 1, false, false, _now.AddDays(-1), null, true);
        var rules = Substitute.For<IActiveDiscountRules>();
        rules.GetAsync(_brl, Token).Returns([stored]);
        var preview = new PreviewDiscountRuleHandler(
            new PricingService(Catalog(), new CartValidator(new CartLineValidator())),
            rules, Substitute.For<IOrderQueries>(), new DiscountEngine(_clock), _clock, new PreviewDiscountRuleQueryValidator());

        var result = await preview.HandleAsync(new PreviewDiscountRuleQuery(null, "BRL", [new CartLine("KB-1", 2)], Definition(), _id), Token);

        result.Value.Quote.DiscountTotal.ShouldBe(4m);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Preview_InvalidCandidateOrEmptyCart_IsAValidationError()
    {
        var preview = new PreviewDiscountRuleHandler(
            new PricingService(Catalog(), new CartValidator(new CartLineValidator())),
            Substitute.For<IActiveDiscountRules>(), Substitute.For<IOrderQueries>(), new DiscountEngine(_clock), _clock, new PreviewDiscountRuleQueryValidator());

        (await preview.HandleAsync(new PreviewDiscountRuleQuery(null, "BRL", [new CartLine("KB-1", 1)], Definition() with { Parameters = "{}" }), Token))
            .Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await preview.HandleAsync(new PreviewDiscountRuleQuery(null, "BRL", [], null), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await preview.HandleAsync(new PreviewDiscountRuleQuery(null, "REAL", [new CartLine("KB-1", 1)], null), Token)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    private static ICatalogPriceProvider Catalog()
    {
        var catalog = Substitute.For<ICatalogPriceProvider>();
        catalog.GetPricesAsync(_brl, Arg.Any<IReadOnlyCollection<string>>(), Token).Returns(Result.Ok(new CatalogPrices(
            [new CatalogPrice("KB-1", "Keyboard", "peripherals", Money.Create(20m, _brl), 3, true)], [])));

        return catalog;
    }

    private static RuleDefinition Definition() => new("Ten percent", "Percentage", _percent, "[]", 1, false, false, null, null, true);

    private static DiscountRuleDto Stored(int version) => new(
        _id,
        version,
        "Ten percent",
        "Percentage",
        JsonDocument.Parse(_percent).RootElement.Clone(),
        JsonDocument.Parse("[]").RootElement.Clone(),
        1,
        false,
        false,
        _now.AddDays(-1),
        null,
        true,
        "admin-1",
        _now.AddDays(-1));

    private CreateDiscountRuleHandler CreateHandler() => new(_repository, _active, _clock, new CreateDiscountRuleCommandValidator());

    private ReplaceDiscountRuleHandler ReplaceHandler() => new(_repository, _active, _clock, new ReplaceDiscountRuleCommandValidator());

    private DeactivateDiscountRuleHandler DeactivateHandler() => new(_repository, _active, _clock, new DeactivateDiscountRuleCommandValidator());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
