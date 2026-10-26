using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;

namespace Catalog.Infrastructure.Persistence;

internal static class CatalogClassMaps
{
    private static readonly Lock _gate = new();
    private static bool _registered;

    /// <summary>Registers the maps once per process; the driver rejects a second registration of the same type.</summary>
    public static void Register()
    {
        lock (_gate)
        {
            if (_registered)
            {
                return;
            }

            BsonClassMap.RegisterClassMap<ProductDocument>(map =>
            {
                map.MapIdMember(document => document.Id).SetIdGenerator(ObjectIdGenerator.Instance);
                map.MapMember(document => document.Sku).SetElementName("sku");
                map.MapMember(document => document.Name).SetElementName("name");
                map.MapMember(document => document.Description).SetElementName("description");
                map.MapMember(document => document.Category).SetElementName("category");
                map.MapMember(document => document.Attributes).SetElementName("attributes");
                map.MapMember(document => document.Prices).SetElementName("prices");
                map.MapMember(document => document.IsActive).SetElementName("isActive");
                map.MapMember(document => document.Version).SetElementName("version");
                map.MapMember(document => document.UpdatedAt).SetElementName("updatedAt")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(document => document.UpdatedBy).SetElementName("updatedBy");
            });

            BsonClassMap.RegisterClassMap<PriceDocument>(map =>
            {
                map.MapMember(document => document.Currency).SetElementName("currency");
                map.MapMember(document => document.Amount).SetElementName("amount")
                    .SetSerializer(new DecimalSerializer(BsonType.Decimal128));
            });

            BsonClassMap.RegisterClassMap<PriceHistoryDocument>(map =>
            {
                map.MapIdMember(document => document.Id).SetIdGenerator(ObjectIdGenerator.Instance);
                map.MapMember(document => document.Sku).SetElementName("sku");
                map.MapMember(document => document.Currency).SetElementName("currency");
                map.MapMember(document => document.OldPrice).SetElementName("oldPrice")
                    .SetSerializer(new NullableSerializer<decimal>(new DecimalSerializer(BsonType.Decimal128)));
                map.MapMember(document => document.NewPrice).SetElementName("newPrice")
                    .SetSerializer(new DecimalSerializer(BsonType.Decimal128));
                map.MapMember(document => document.Version).SetElementName("version");
                map.MapMember(document => document.ChangedBy).SetElementName("changedBy");
                map.MapMember(document => document.ChangedAt).SetElementName("changedAt")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(document => document.TraceId).SetElementName("traceId");
            });

            BsonClassMap.RegisterClassMap<IdempotencyDocument>(map =>
            {
                map.MapIdMember(document => document.Id).SetIdGenerator(ObjectIdGenerator.Instance);
                map.MapMember(document => document.Scope).SetElementName("scope");
                map.MapMember(document => document.Key).SetElementName("key");
                map.MapMember(document => document.RequestHash).SetElementName("requestHash");
                map.MapMember(document => document.ResponseStatus).SetElementName("responseStatus");
                map.MapMember(document => document.ResponseBody).SetElementName("responseBody");
                map.MapMember(document => document.ResourceId).SetElementName("resourceId");
                map.MapMember(document => document.CreatedAt).SetElementName("createdAt")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(document => document.ExpiresAt).SetElementName("expiresAt")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
            });

            _registered = true;
        }
    }
}
