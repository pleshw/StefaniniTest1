namespace Orders.Domain.Rules;

public interface ISpecification<in T>
{
    bool IsSatisfiedBy(T candidate);
}

/// <summary>Holds when every specification holds; no specifications always hold.</summary>
public sealed class AllOf<T>(IReadOnlyList<ISpecification<T>> specifications) : ISpecification<T>
{
    public IReadOnlyList<ISpecification<T>> Specifications { get; } = specifications;

    public bool IsSatisfiedBy(T candidate) => Specifications.All(specification => specification.IsSatisfiedBy(candidate));
}
