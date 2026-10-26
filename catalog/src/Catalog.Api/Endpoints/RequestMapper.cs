using Catalog.Application.Products;
using Riok.Mapperly.Abstractions;

namespace Catalog.Api.Endpoints;

/// <summary>Turns HTTP request bodies, plus the route, header and user values, into application commands and queries.</summary>
[Mapper]
internal static partial class RequestMapper
{
    public static partial UpsertProductCommand ToCommand(
        this AdminProductEndpoints.UpsertProductRequest request, string sku, string? ifMatch, string updatedBy);

    public static partial ChangePriceCommand ToCommand(
        this AdminProductEndpoints.ChangePriceRequest request, string sku, string? ifMatch, string updatedBy);

    public static partial PriceQuery ToQuery(this ProductEndpoints.PriceQueryRequest request);
}
