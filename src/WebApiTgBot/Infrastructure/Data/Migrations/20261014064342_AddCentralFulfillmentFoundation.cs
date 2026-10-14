using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebApiTgBot.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralFulfillmentFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Fulfillments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FulfillmentMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QuantityRequested = table.Column<int>(type: "integer", nullable: false),
                    QuantityFulfilled = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fulfillments", x => x.Id);
                    table.CheckConstraint("CK_Fulfillments_QuantityFulfilled_Valid", "\"QuantityFulfilled\" >= 0 AND \"QuantityFulfilled\" <= \"QuantityRequested\"");
                    table.CheckConstraint("CK_Fulfillments_QuantityRequested_Positive", "\"QuantityRequested\" > 0");
                    table.ForeignKey(
                        name: "FK_Fulfillments_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Fulfillments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FulfillmentAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FulfillmentId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    AttemptKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VendorProductId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExternalVendorOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureCategory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SafeErrorSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfillmentAttempts", x => x.Id);
                    table.CheckConstraint("CK_FulfillmentAttempts_AttemptNumber_Positive", "\"AttemptNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_FulfillmentAttempts_Fulfillments_FulfillmentId",
                        column: x => x.FulfillmentId,
                        principalTable: "Fulfillments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FulfillmentAttempts_VendorProducts_VendorProductId",
                        column: x => x.VendorProductId,
                        principalTable: "VendorProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_AttemptKey",
                table: "FulfillmentAttempts",
                column: "AttemptKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_FulfillmentId_AttemptNumber",
                table: "FulfillmentAttempts",
                columns: new[] { "FulfillmentId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_Status",
                table: "FulfillmentAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_VendorProductId",
                table: "FulfillmentAttempts",
                column: "VendorProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Fulfillments_OrderId",
                table: "Fulfillments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Fulfillments_OrderItemId",
                table: "Fulfillments",
                column: "OrderItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fulfillments_Status",
                table: "Fulfillments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FulfillmentAttempts");

            migrationBuilder.DropTable(
                name: "Fulfillments");
        }
    }
}
