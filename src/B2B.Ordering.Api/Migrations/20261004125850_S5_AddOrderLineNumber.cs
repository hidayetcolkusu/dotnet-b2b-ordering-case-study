using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace B2B.Ordering.Api.Migrations
{
    /// <summary>
    /// Stores each line's explicit 1-based position in the submitted request, so reads return
    /// lines in submission order. The version 7 Guid id cannot serve that purpose: SQL Server does
    /// not order <c>uniqueidentifier</c> by creation time, so sorting by id would let a GET disagree
    /// with the create response and its stored replay.
    ///
    /// Lines written before this migration never recorded their position, so the backfill numbers
    /// them by id — the same order reads already returned for them. It cannot recover the original
    /// submission order, and does not pretend to.
    ///
    /// Like S4, this is an ordinary later schema change, not part of the S1/S2/S3 experiment.
    /// </summary>
    public partial class S5_AddOrderLineNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderLines_CompanyId_OrderId",
                table: "OrderLines");

            migrationBuilder.AddColumn<int>(
                name: "LineNumber",
                table: "OrderLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT [LineNumber],
                           ROW_NUMBER() OVER (PARTITION BY [OrderId] ORDER BY [Id]) AS [Position]
                    FROM [OrderLines]
                )
                UPDATE numbered SET [LineNumber] = [Position];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_CompanyId_OrderId_LineNumber",
                table: "OrderLines",
                columns: new[] { "CompanyId", "OrderId", "LineNumber" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderLines_LineNumber",
                table: "OrderLines",
                sql: "[LineNumber] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderLines_CompanyId_OrderId_LineNumber",
                table: "OrderLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderLines_LineNumber",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "LineNumber",
                table: "OrderLines");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_CompanyId_OrderId",
                table: "OrderLines",
                columns: new[] { "CompanyId", "OrderId" });
        }
    }
}
