using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _0001_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "idempotency_requests",
                columns: table => new
                {
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    response_status = table.Column<short>(type: "smallint", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_requests", x => new { x.client_id, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_name = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", nullable: false, defaultValue: 0m),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "number", "client_name" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_item_count", "item_count > 0");
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    catalog_version = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_discount = table.Column<decimal>(type: "numeric(19,4)", nullable: false, defaultValue: 0m),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_items", x => new { x.order_id, x.line_no });
                    table.CheckConstraint("ck_order_items_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_status_history", x => new { x.order_id, x.seq });
                    table.ForeignKey(
                        name: "FK_order_status_history_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_expires",
                table: "idempotency_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_orders_client_created",
                table: "orders",
                columns: new[] { "client_id", "created_at", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_orders_client_trgm",
                table: "orders",
                column: "client_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_search",
                table: "orders",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_created",
                table: "orders",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "ux_orders_number",
                table: "orders",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_requests");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "order_status_history");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
