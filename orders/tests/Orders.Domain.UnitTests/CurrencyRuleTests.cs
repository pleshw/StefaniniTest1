using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain.UnitTests;

public sealed class CurrencyRuleTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(" brl ", true)]
    [InlineData("USD", true)]
    [InlineData("REAL", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryCreate_Code_ReportsWhetherItIsValid(string? code, bool expected)
    {
        Currency.TryCreate(code, out var currency).ShouldBe(expected);
        (currency is not null).ShouldBe(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null, true)]
    [InlineData("BRL", true)]
    [InlineData("REAL", false)]
    public void ValidCurrencyOrDefault_Code_AcceptsNullAndValidCodesOnly(string? code, bool expected)
    {
        new CodeValidator().Validate(new Holder(code)).IsValid.ShouldBe(expected);
    }

    private sealed record Holder(string? Currency);

    private sealed class CodeValidator : AbstractValidator<Holder>
    {
        public CodeValidator() => RuleFor(holder => holder.Currency).ValidCurrencyOrDefault();
    }
}
