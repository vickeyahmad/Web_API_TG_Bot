using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApiTgBot.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualWalletTopUpsAndPaymentPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId_IdempotencyKey",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ProviderTransactionId",
                table: "Payments");

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                table: "Payments",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "AdminNotes",
                table: "Payments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerNotes",
                table: "Payments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerProofSubmittedAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerReference",
                table: "Payments",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSenderAccount",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerTransferDate",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "Payments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "OrderPurchase");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedByUserId",
                table: "Payments",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedReceivingAccount",
                table: "Payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletId",
                table: "Payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_IdempotencyKey",
                table: "Payments",
                columns: new[] { "OrderId", "IdempotencyKey" },
                unique: true,
                filter: "\"OrderId\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Paid_ProviderTransactionId",
                table: "Payments",
                column: "ProviderTransactionId",
                unique: true,
                filter: "\"Status\" = 'Paid' AND \"ProviderTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_VerifiedByUserId",
                table: "Payments",
                column: "VerifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_WalletId_IdempotencyKey",
                table: "Payments",
                columns: new[] { "WalletId", "IdempotencyKey" },
                unique: true,
                filter: "\"WalletId\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_WalletId_Status",
                table: "Payments",
                columns: new[] { "WalletId", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Amount_Positive",
                table: "Payments",
                sql: "\"Amount\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Purpose_Target",
                table: "Payments",
                sql: "(\"Purpose\" = 'OrderPurchase' AND \"OrderId\" IS NOT NULL AND \"WalletId\" IS NULL) OR (\"Purpose\" = 'WalletTopUp' AND \"WalletId\" IS NOT NULL AND \"OrderId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_AspNetUsers_VerifiedByUserId",
                table: "Payments",
                column: "VerifiedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Wallets_WalletId",
                table: "Payments",
                column: "WalletId",
                principalTable: "Wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_AspNetUsers_VerifiedByUserId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Wallets_WalletId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId_IdempotencyKey",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Paid_ProviderTransactionId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_VerifiedByUserId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_WalletId_IdempotencyKey",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_WalletId_Status",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Amount_Positive",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Purpose_Target",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AdminNotes",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomerNotes",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomerProofSubmittedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomerReference",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomerSenderAccount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomerTransferDate",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "VerifiedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "VerifiedReceivingAccount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "Payments");

            migrationBuilder.AlterColumn<long>(
                name: "OrderId",
                table: "Payments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_IdempotencyKey",
                table: "Payments",
                columns: new[] { "OrderId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderTransactionId",
                table: "Payments",
                column: "ProviderTransactionId",
                filter: "\"ProviderTransactionId\" IS NOT NULL");
        }
    }
}
