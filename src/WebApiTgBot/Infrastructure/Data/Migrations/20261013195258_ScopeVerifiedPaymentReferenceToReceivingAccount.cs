using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApiTgBot.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeVerifiedPaymentReferenceToReceivingAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Paid_ProviderTransactionId",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Paid_Account_ProviderTransactionId",
                table: "Payments",
                columns: new[] { "VerifiedReceivingAccount", "ProviderTransactionId" },
                unique: true,
                filter: "\"Status\" = 'Paid' AND \"VerifiedReceivingAccount\" IS NOT NULL AND \"ProviderTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderTransactionId",
                table: "Payments",
                column: "ProviderTransactionId",
                filter: "\"ProviderTransactionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Paid_Account_ProviderTransactionId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ProviderTransactionId",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Paid_ProviderTransactionId",
                table: "Payments",
                column: "ProviderTransactionId",
                unique: true,
                filter: "\"Status\" = 'Paid' AND \"ProviderTransactionId\" IS NOT NULL");
        }
    }
}
