namespace Orders.Application;

public enum ErrorKind
{
    Validation,
    Unprocessable,
    CatalogUnavailable,
    NotFound,
    Forbidden,

    /// <summary>A once-per-client discount was already redeemed by the client (HTTP 409 discount-already-used).</summary>
    DiscountAlreadyUsed,

    /// <summary>The If-Match version is not the current one (HTTP 412).</summary>
    PreconditionFailed,

    /// <summary>An update needs an If-Match header and none was sent (HTTP 428).</summary>
    PreconditionRequired,

    /// <summary>The order state machine does not allow the requested status change (HTTP 409 invalid-transition).</summary>
    InvalidTransition,
}

public sealed record ResultError(ErrorKind Kind, string Message);

public static class Result
{
    public static Result<T> Ok<T>(T value) => new(value, null);

    public static Result<T> Fail<T>(ErrorKind kind, string message) => new(default, new ResultError(kind, message));
}

public sealed class Result<T>
{
    private readonly T? _value;

    internal Result(T? value, ResultError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public ResultError? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"The result is a failure: {Error!.Message}");
}
