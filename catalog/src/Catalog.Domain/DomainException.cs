namespace Catalog.Domain;

public sealed class DomainException(string message) : Exception(message);
