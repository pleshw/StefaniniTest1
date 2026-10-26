using FluentValidation.Results;

namespace Orders.Application;

public static class ValidationResults
{
    /// <summary>A <see cref="ErrorKind.Validation"/> failure carrying every message of <paramref name="result"/>.</summary>
    public static Result<T> ToFailure<T>(this ValidationResult result) =>
        Result.Fail<T>(ErrorKind.Validation, result.ToString(" "));
}
