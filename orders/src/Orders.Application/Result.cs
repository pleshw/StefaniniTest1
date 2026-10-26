namespace Orders.Application;

public enum ErrorKind
{
    Validation,
    Unprocessable,
    CatalogUnavailable,
    NotFound,
    Forbidden,
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
