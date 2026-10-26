using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _0002_Discounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discount_redemptions",
                columns: table => new
                {
                    discount_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    once_per_client = table.Column<bool>(type: "boolean", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_redemptions", x => new { x.discount_rule_id, x.order_id });
                    table.ForeignKey(
                        name: "FK_discount_redemptions_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discount_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    stackable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    once_per_client = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_rules", x => new { x.id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "order_discounts",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_discounts", x => new { x.order_id, x.discount_rule_id });
                    table.ForeignKey(
                        name: "FK_order_discounts_discount_rules_discount_rule_id_rule_version",
                        columns: x => new { x.discount_rule_id, x.rule_version },
                        principalTable: "discount_rules",
                        principalColumns: new[] { "id", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_discounts_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discount_redemptions_order_id",
                table: "discount_redemptions",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ux_redemption_once_per_client",
                table: "discount_redemptions",
                columns: new[] { "discount_rule_id", "client_id" },
                unique: true,
                filter: "once_per_client");

            migrationBuilder.CreateIndex(
                name: "IX_order_discounts_discount_rule_id_rule_version",
                table: "order_discounts",
                columns: new[] { "discount_rule_id", "rule_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discount_redemptions");

            migrationBuilder.DropTable(
                name: "order_discounts");

            migrationBuilder.DropTable(
                name: "discount_rules");
        }
    }
}
