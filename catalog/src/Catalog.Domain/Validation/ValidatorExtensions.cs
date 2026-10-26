using FluentValidation;

namespace Catalog.Domain.Validation;

public static class ValidatorExtensions
{
    /// <summary>Throws a <see cref="DomainException"/> carrying every failure message when <paramref name="instance"/> is invalid.</summary>
    public static void EnsureValid<T>(this IValidator<T> validator, T instance)
    {
        var result = validator.Validate(instance);

        if (!result.IsValid)
        {
            throw new DomainException(result.ToString(" "));
        }
    }
}
