using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain;
using Orders.Domain.Confirmation;

namespace Orders.Infrastructure.Persistence;

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();

    public DbSet<OrderItemRecord> OrderItems => Set<OrderItemRecord>();

    public DbSet<OrderStatusHistoryRecord> OrderStatusHistory => Set<OrderStatusHistoryRecord>();

    public DbSet<IdempotencyRequestRecord> IdempotencyRequests => Set<IdempotencyRequestRecord>();

    public DbSet<DiscountRuleRecord> DiscountRules => Set<DiscountRuleRecord>();

    public DbSet<OrderDiscountRecord> OrderDiscounts => Set<OrderDiscountRecord>();

    public DbSet<DiscountRedemptionRecord> DiscountRedemptions => Set<DiscountRedemptionRecord>();

    public DbSet<ConfirmationRuleRecord> ConfirmationRules => Set<ConfirmationRuleRecord>();

    public DbSet<OrderEventLogRecord> OrderEventLog => Set<OrderEventLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        ConfigureOrders(modelBuilder.Entity<OrderRecord>());
        ConfigureItems(modelBuilder.Entity<OrderItemRecord>());
        ConfigureHistory(modelBuilder.Entity<OrderStatusHistoryRecord>());
        ConfigureIdempotency(modelBuilder.Entity<IdempotencyRequestRecord>());
        ConfigureDiscountRules(modelBuilder.Entity<DiscountRuleRecord>());
        ConfigureOrderDiscounts(modelBuilder.Entity<OrderDiscountRecord>());
        ConfigureRedemptions(modelBuilder.Entity<DiscountRedemptionRecord>());
        ConfigureConfirmationRules(modelBuilder.Entity<ConfirmationRuleRecord>());
        ConfigureEventLog(modelBuilder.Entity<OrderEventLogRecord>());
    }

    private static void ConfigureOrders(EntityTypeBuilder<OrderRecord> order)
    {
        order.ToTable("orders", table => table.HasCheckConstraint("ck_orders_item_count", "item_count > 0"));
        order.HasKey(record => record.Id);
        order.Property(record => record.Id).HasColumnName("id");
        order.Property(record => record.Number).HasColumnName("number").IsRequired();
        order.Property(record => record.ClientId).HasColumnName("client_id");
        order.Property(record => record.ClientName).HasColumnName("client_name").IsRequired();
        order.Property(record => record.CreatedBy).HasColumnName("created_by").IsRequired();
        order.Property(record => record.Channel).HasColumnName("channel").HasConversion(
            channel => channel == OrderChannel.Shop ? "shop" : "admin",
            text => text == "shop" ? OrderChannel.Shop : OrderChannel.Admin);
        order.Property(record => record.Status).HasColumnName("status").HasConversion<string>();
        order.Property(record => record.ItemCount).HasColumnName("item_count");
        order.Property(record => record.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        order.Property(record => record.Subtotal).HasColumnName("subtotal").HasColumnType("numeric(19,4)");
        order.Property(record => record.DiscountTotal).HasColumnName("discount_total").HasColumnType("numeric(19,4)").HasDefaultValue(0m);
        order.Property(record => record.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
        order.Property(record => record.CreatedAt).HasColumnName("created_at");
        order.Property(record => record.UpdatedAt).HasColumnName("updated_at");
        order.Property(record => record.RowVersion).HasColumnName("row_version").HasDefaultValue(0).IsConcurrencyToken();
        order.Property(record => record.SearchVector).HasColumnName("search_vector");
        order.HasGeneratedTsVectorColumn(record => record.SearchVector!, "simple", record => new { record.Number, record.ClientName });

        order.HasIndex(record => record.Number).IsUnique().HasDatabaseName("ux_orders_number");
        order.HasIndex(record => new { record.ClientId, record.CreatedAt, record.Id }).IsDescending(false, true, false)
            .HasDatabaseName("ix_orders_client_created");
        order.HasIndex(record => new { record.Status, record.CreatedAt, record.Id }).IsDescending(false, true, false)
            .HasDatabaseName("ix_orders_status_created");
        order.HasIndex(record => record.SearchVector).HasMethod("GIN").HasDatabaseName("ix_orders_search");
        order.HasIndex(record => record.ClientName).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_orders_client_trgm");

        order.HasMany(record => record.Items).WithOne().HasForeignKey(item => item.OrderId);
        order.HasMany(record => record.History).WithOne().HasForeignKey(change => change.OrderId);
    }

    private static void ConfigureItems(EntityTypeBuilder<OrderItemRecord> item)
    {
        item.ToTable("order_items", table => table.HasCheckConstraint("ck_order_items_quantity", "quantity > 0"));
        item.HasKey(record => new { record.OrderId, record.LineNo });
        item.Property(record => record.OrderId).HasColumnName("order_id");
        item.Property(record => record.LineNo).HasColumnName("line_no");
        item.Property(record => record.Sku).HasColumnName("sku").IsRequired();
        item.Property(record => record.ProductName).HasColumnName("product_name").IsRequired();
        item.Property(record => record.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(19,4)");
        item.Property(record => record.CatalogVersion).HasColumnName("catalog_version");
        item.Property(record => record.Quantity).HasColumnName("quantity");
        item.Property(record => record.LineDiscount).HasColumnName("line_discount").HasColumnType("numeric(19,4)").HasDefaultValue(0m);
        item.Property(record => record.LineTotal).HasColumnName("line_total").HasColumnType("numeric(19,4)");
    }

    private static void ConfigureHistory(EntityTypeBuilder<OrderStatusHistoryRecord> change)
    {
        change.ToTable("order_status_history");
        change.HasKey(record => new { record.OrderId, record.Seq });
        change.Property(record => record.OrderId).HasColumnName("order_id");
        change.Property(record => record.Seq).HasColumnName("seq");
        change.Property(record => record.FromStatus).HasColumnName("from_status").HasConversion<string>();
        change.Property(record => record.ToStatus).HasColumnName("to_status").HasConversion<string>();
        change.Property(record => record.Reason).HasColumnName("reason");
        change.Property(record => record.Actor).HasColumnName("actor").IsRequired();
        change.Property(record => record.TraceId).HasColumnName("trace_id");
        change.Property(record => record.ChangedAt).HasColumnName("changed_at");
    }

    private static void ConfigureIdempotency(EntityTypeBuilder<IdempotencyRequestRecord> request)
    {
        request.ToTable("idempotency_requests");
        request.HasKey(record => new { record.ClientId, record.IdempotencyKey });
        request.Property(record => record.ClientId).HasColumnName("client_id");
        request.Property(record => record.IdempotencyKey).HasColumnName("idempotency_key");
        request.Property(record => record.RequestHash).HasColumnName("request_hash").HasColumnType("bytea");
        request.Property(record => record.ResponseStatus).HasColumnName("response_status");
        request.Property(record => record.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        request.Property(record => record.OrderId).HasColumnName("order_id");
        request.Property(record => record.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        request.Property(record => record.ExpiresAt).HasColumnName("expires_at");
        request.HasIndex(record => record.ExpiresAt).HasDatabaseName("ix_idempotency_expires");
    }

    private static void ConfigureDiscountRules(EntityTypeBuilder<DiscountRuleRecord> rule)
    {
        rule.ToTable("discount_rules");
        rule.HasKey(record => new { record.Id, record.Version });
        rule.Property(record => record.Id).HasColumnName("id");
        rule.Property(record => record.Version).HasColumnName("version");
        rule.Property(record => record.Name).HasColumnName("name").IsRequired();
        rule.Property(record => record.Type).HasColumnName("type").HasConversion<string>().IsRequired();
        rule.Property(record => record.Parameters).HasColumnName("parameters").HasColumnType("jsonb").IsRequired();
        rule.Property(record => record.Conditions).HasColumnName("conditions").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        rule.Property(record => record.Priority).HasColumnName("priority");
        rule.Property(record => record.Stackable).HasColumnName("stackable").HasDefaultValue(false);
        rule.Property(record => record.OncePerClient).HasColumnName("once_per_client").HasDefaultValue(false);
        rule.Property(record => record.ValidFrom).HasColumnName("valid_from");
        rule.Property(record => record.ValidTo).HasColumnName("valid_to");
        rule.Property(record => record.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        rule.Property(record => record.CreatedBy).HasColumnName("created_by").IsRequired();
        rule.Property(record => record.CreatedAt).HasColumnName("created_at");
    }

    private static void ConfigureOrderDiscounts(EntityTypeBuilder<OrderDiscountRecord> discount)
    {
        discount.ToTable("order_discounts");
        discount.HasKey(record => new { record.OrderId, record.DiscountRuleId });
        discount.Property(record => record.OrderId).HasColumnName("order_id");
        discount.Property(record => record.DiscountRuleId).HasColumnName("discount_rule_id");
        discount.Property(record => record.RuleVersion).HasColumnName("rule_version");
        discount.Property(record => record.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
        discount.HasOne<OrderRecord>().WithMany().HasForeignKey(record => record.OrderId);
        discount.HasOne<DiscountRuleRecord>().WithMany()
            .HasForeignKey(record => new { record.DiscountRuleId, record.RuleVersion })
            .HasPrincipalKey(rule => new { rule.Id, rule.Version })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRedemptions(EntityTypeBuilder<DiscountRedemptionRecord> redemption)
    {
        redemption.ToTable("discount_redemptions");
        redemption.HasKey(record => new { record.DiscountRuleId, record.OrderId });
        redemption.Property(record => record.DiscountRuleId).HasColumnName("discount_rule_id");
        redemption.Property(record => record.ClientId).HasColumnName("client_id");
        redemption.Property(record => record.OrderId).HasColumnName("order_id");
        redemption.Property(record => record.OncePerClient).HasColumnName("once_per_client");
        redemption.Property(record => record.RedeemedAt).HasColumnName("redeemed_at").HasDefaultValueSql("now()");
        redemption.HasOne<OrderRecord>().WithMany().HasForeignKey(record => record.OrderId);

        // At most one redemption per client of a once-per-client rule: a second order racing for it fails here.
        redemption.HasIndex(record => new { record.DiscountRuleId, record.ClientId })
            .IsUnique()
            .HasFilter("once_per_client")
            .HasDatabaseName(DiscountRedemptionRecord.OncePerClientIndex);
    }

    private static void ConfigureConfirmationRules(EntityTypeBuilder<ConfirmationRuleRecord> rule)
    {
        rule.ToTable("confirmation_rules", table => table.HasCheckConstraint("ck_confirmation_rules_action", "action IN ('Hold')"));
        rule.HasKey(record => new { record.Id, record.Version });
        rule.Property(record => record.Id).HasColumnName("id");
        rule.Property(record => record.Version).HasColumnName("version");
        rule.Property(record => record.Name).HasColumnName("name").IsRequired();
        rule.Property(record => record.Action).HasColumnName("action").HasConversion<string>().IsRequired();
        rule.Property(record => record.Conditions).HasColumnName("conditions").HasColumnType("jsonb").IsRequired();
        rule.Property(record => record.Priority).HasColumnName("priority");
        rule.Property(record => record.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        rule.Property(record => record.CreatedBy).HasColumnName("created_by").IsRequired();
        rule.Property(record => record.CreatedAt).HasColumnName("created_at");
    }

    private static void ConfigureEventLog(EntityTypeBuilder<OrderEventLogRecord> log)
    {
        log.ToTable("order_event_log");
        log.HasKey(record => record.Id);
        log.Property(record => record.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        log.Property(record => record.OrderId).HasColumnName("order_id");
        log.Property(record => record.Type).HasColumnName("type").IsRequired();
        log.Property(record => record.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        log.Property(record => record.OccurredAt).HasColumnName("occurred_at");
        log.HasIndex(record => new { record.OccurredAt, record.Id }).HasDatabaseName("ix_order_event_log_occurred");
        log.HasIndex(record => record.OrderId).HasDatabaseName("ix_order_event_log_order");

        // The log outlives anything done to an order: deleting an order must not delete its history.
        log.HasOne<OrderRecord>().WithMany().HasForeignKey(record => record.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
