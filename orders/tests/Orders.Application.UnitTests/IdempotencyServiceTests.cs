using Orders.Application.Idempotency;

namespace Orders.Application.UnitTests;

public sealed class IdempotencyServiceTests
{
    private static readonly Guid _client = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _key = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly byte[] _hash = [1, 2, 3];

    private readonly IIdempotencyStore _store = Substitute.For<IIdempotencyStore>();
    private readonly IdempotencyService _service;

    public IdempotencyServiceTests()
    {
        _service = new IdempotencyService(_store);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckAsync_UnknownKey_LetsTheRequestProceed()
    {
        var result = await _service.CheckAsync(_client, _key, _hash, Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckAsync_KnownKeyWithTheSameHash_ReturnsTheStoredResponseForReplay()
    {
        var stored = new IdempotencyRecord(_client, _key, [1, 2, 3], 201, """{"id":"x"}""", Guid.NewGuid());
        _store.TryGetAsync(_client, _key, Token).Returns(stored);

        var result = await _service.CheckAsync(_client, _key, _hash, Token);

        result.Value.ShouldBe(stored);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckAsync_KnownKeyWithAnotherHash_ReturnsUnprocessable()
    {
        _store.TryGetAsync(_client, _key, Token).Returns(new IdempotencyRecord(_client, _key, [9, 9, 9], 201, "{}", Guid.NewGuid()));

        var result = await _service.CheckAsync(_client, _key, _hash, Token);

        result.Error!.Kind.ShouldBe(ErrorKind.Unprocessable);
        result.Error.Message.ShouldContain("Idempotency-Key");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("3f2b8c4e-9a1d-4e7b-8c5a-1d2e3f4a5b6c")]
    [InlineData("  3f2b8c4e-9a1d-4e7b-8c5a-1d2e3f4a5b6c  ")]
    public void Parse_ValidUuid_ReturnsTheKey(string header)
    {
        var result = IdempotencyKey.Parse(header);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(Guid.Parse("3f2b8c4e-9a1d-4e7b-8c5a-1d2e3f4a5b6c"));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Parse_MissingOrInvalid_ReturnsValidationError(string? header)
    {
        var result = IdempotencyKey.Parse(header);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("Idempotency-Key");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
