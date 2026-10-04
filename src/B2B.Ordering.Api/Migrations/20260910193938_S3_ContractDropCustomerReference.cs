using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace B2B.Ordering.Api.Migrations
{
    /// <summary>
    /// S3, the contract step. It runs only after every V1 and V2 writer has stopped. The final
    /// backfill is repeated here so a late V1 write made just before shutdown is not lost, and the
    /// drop is refused if the two columns still disagree.
    /// </summary>
    public partial class S3_ContractDropCustomerReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(S2B_BackfillExternalReference.CopySql);

            // Fail loudly rather than drop a column whose value never reached the new one. The
            // predicate is the one S2B already applied at the gate - shared rather than restated,
            // because two copies of a subtle comparison drift apart.
            migrationBuilder.Sql(
                S2B_BackfillExternalReference.AgreementCheckSql(
                    "Contract migration aborted; the old column was left in place."));

            migrationBuilder.DropColumn(
                name: "CustomerReference",
                table: "Orders");
        }

        /// <summary>
        /// Recreates the column and copies the values back. This is a schema repair, not a
        /// recovery: any value that only ever existed in the dropped column is gone for good.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerReference",
                table: "Orders",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [Orders]
                SET [CustomerReference] = [ExternalReference]
                WHERE [ExternalReference] IS NOT NULL;
                """);
        }
    }
}
