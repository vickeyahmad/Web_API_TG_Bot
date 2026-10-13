using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApiTgBot.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUniquePaidOrderPaymentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_Paid_OrderId",
                table: "Payments",
                column: "OrderId",
                unique: true,
                filter: "\"Status\" = 'Paid' AND \"OrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Paid_OrderId",
                table: "Payments");
        }
    }
}
