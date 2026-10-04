using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace B2B.Ordering.Api.Migrations
{
    /// <summary>
    /// Adds the ownership constraints that were missing: an order must belong to a company that
    /// exists and be created by a user that exists, a negotiated price must name a real company,
    /// and an idempotency record must name a real company and user.
    ///
    /// The tenant write guard compares an entity's company against the caller's; it cannot tell
    /// whether that company exists at all, so without these an orphan order could be written.
    ///
    /// This is an ordinary later schema change, not a fourth state of the S1/S2/S3 reference
    /// experiment — that experiment is about one column moving and is unchanged by it.
    /// </summary>
    public partial class S4_AddCompanyAndUserForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedByUserId",
                table: "Orders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_UserId",
                table: "IdempotencyRecords",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyProductPrices_Companies_CompanyId",
                table: "CompanyProductPrices",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IdempotencyRecords_Companies_CompanyId",
                table: "IdempotencyRecords",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IdempotencyRecords_Users_UserId",
                table: "IdempotencyRecords",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Companies_CompanyId",
                table: "Orders",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_CreatedByUserId",
                table: "Orders",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyProductPrices_Companies_CompanyId",
                table: "CompanyProductPrices");

            migrationBuilder.DropForeignKey(
                name: "FK_IdempotencyRecords_Companies_CompanyId",
                table: "IdempotencyRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_IdempotencyRecords_Users_UserId",
                table: "IdempotencyRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Companies_CompanyId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_CreatedByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyRecords_UserId",
                table: "IdempotencyRecords");
        }
    }
}
