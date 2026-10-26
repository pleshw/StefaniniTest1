using FluentValidation;
using Orders.Domain.Validation;

namespace Orders.Domain;

/// <summary>The client an order belongs to; the name is a snapshot taken when the order is placed.</summary>
public sealed record ClientRef
{
    private static readonly InputValidator _validator = new();

    private ClientRef(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }

    public string Name { get; }

    public static ClientRef Create(Guid id, string? name)
    {
        var trimmed = name?.Trim();
        _validator.EnsureValid(new Input(id, trimmed));

        return new ClientRef(id, trimmed!);
    }

    private sealed record Input(Guid Id, string? Name);

    private sealed class InputValidator : AbstractValidator<Input>
    {
        public InputValidator()
        {
            RuleFor(input => input.Id).NotEqual(Guid.Empty).WithMessage("Client id must not be empty.");
            RuleFor(input => input.Name).NotEmpty().WithMessage("Client name must not be empty.");
        }
    }
}
