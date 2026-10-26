using FluentValidation;

namespace Orders.Application.Idempotency;

public static class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    private static readonly HeaderValidator _validator = new();

    /// <summary>Reads the header value as a UUID. A missing or malformed key is a validation error (HTTP 400).</summary>
    public static Result<Guid> Parse(string? header)
    {
        var validation = _validator.Validate(header?.Trim() ?? string.Empty);

        return validation.IsValid
            ? Result.Ok(Guid.Parse(header!.Trim()))
            : validation.ToFailure<Guid>();
    }

    private sealed class HeaderValidator : AbstractValidator<string?>
    {
        public HeaderValidator()
        {
            RuleFor(header => header)
                .Must(header => Guid.TryParse(header, out var key) && key != Guid.Empty)
                .WithMessage($"The {HeaderName} header is required and must be a UUID.");
        }
    }
}

public sealed class IdempotencyService(IIdempotencyStore store)
{
    /// <summary>
    /// Step 1 of the create flow. Success with a record means replay it; success with null means the key is new;
    /// an Unprocessable error means the key was used for a different request.
    /// </summary>
    public async Task<Result<IdempotencyRecord?>> CheckAsync(
        Guid clientId, Guid key, byte[] requestHash, CancellationToken cancellationToken)
    {
        var stored = await store.TryGetAsync(clientId, key, cancellationToken);

        if (stored is null)
        {
            return Result.Ok<IdempotencyRecord?>(null);
        }

        return stored.RequestHash.AsSpan().SequenceEqual(requestHash)
            ? Result.Ok<IdempotencyRecord?>(stored)
            : Result.Fail<IdempotencyRecord?>(
                ErrorKind.Unprocessable, $"The {IdempotencyKey.HeaderName} was already used with a different request.");
    }
}
