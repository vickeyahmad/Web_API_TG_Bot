using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebApiTgBot.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualFulfillmentAndAuditHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmedByUserId",
                table: "FulfillmentAttempts",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryChannel",
                table: "FulfillmentAttempts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryReference",
                table: "FulfillmentAttempts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatedByUserId",
                table: "FulfillmentAttempts",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "FulfillmentAttempts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FulfillmentStatusHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FulfillmentId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfillmentStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FulfillmentStatusHistories_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FulfillmentStatusHistories_Fulfillments_FulfillmentId",
                        column: x => x.FulfillmentId,
                        principalTable: "Fulfillments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_ConfirmedByUserId",
                table: "FulfillmentAttempts",
                column: "ConfirmedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentAttempts_InitiatedByUserId",
                table: "FulfillmentAttempts",
                column: "InitiatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentStatusHistories_ChangedAt",
                table: "FulfillmentStatusHistories",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentStatusHistories_ChangedByUserId",
                table: "FulfillmentStatusHistories",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentStatusHistories_FulfillmentId",
                table: "FulfillmentStatusHistories",
                column: "FulfillmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_FulfillmentAttempts_AspNetUsers_ConfirmedByUserId",
                table: "FulfillmentAttempts",
                column: "ConfirmedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_FulfillmentAttempts_AspNetUsers_InitiatedByUserId",
                table: "FulfillmentAttempts",
                column: "InitiatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FulfillmentAttempts_AspNetUsers_ConfirmedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropForeignKey(
                name: "FK_FulfillmentAttempts_AspNetUsers_InitiatedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropTable(
                name: "FulfillmentStatusHistories");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentAttempts_ConfirmedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_FulfillmentAttempts_InitiatedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropColumn(
                name: "ConfirmedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropColumn(
                name: "DeliveryChannel",
                table: "FulfillmentAttempts");

            migrationBuilder.DropColumn(
                name: "DeliveryReference",
                table: "FulfillmentAttempts");

            migrationBuilder.DropColumn(
                name: "InitiatedByUserId",
                table: "FulfillmentAttempts");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "FulfillmentAttempts");
        }
    }
}
