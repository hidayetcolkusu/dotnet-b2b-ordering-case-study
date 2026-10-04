using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace B2B.Ordering.Api.Migrations
{
    /// <summary>
    /// S2, the expand step. EF's generated diff was a RENAME, which would have broken every V1
    /// instance the moment it ran. It is replaced by an additive change: the new nullable column
    /// appears, the old one stays, and V1 and V2 can run against the same database.
    /// </summary>
    public partial class S2_ExpandExternalReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalReference",
                table: "Orders",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            // Re-runnable: only rows that have not been copied yet are touched.
            migrationBuilder.Sql(
                """
                UPDATE [Orders]
                SET [ExternalReference] = [CustomerReference]
                WHERE [ExternalReference] IS NULL AND [CustomerReference] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalReference",
                table: "Orders");
        }
    }
}
