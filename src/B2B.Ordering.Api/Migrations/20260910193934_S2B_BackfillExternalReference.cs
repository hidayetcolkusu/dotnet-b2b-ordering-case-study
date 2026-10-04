using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace B2B.Ordering.Api.Migrations
{
    /// <summary>
    /// The final backfill, and its own step on purpose.
    ///
    /// S2 copies the rows that exist when the expand runs, but a V1 instance keeps writing only the
    /// old column for as long as it stays up. Those rows are invisible to V3, which reads only the
    /// new column. This migration is the gate between "the last V1 and V2 writer has stopped" and
    /// "V3 may be switched on"; folding it into the expand or the contract would leave that window
    /// uncovered in one direction or the other.
    ///
    /// The statement is re-runnable: it touches only rows that have not been copied yet, so an
    /// operator can run it again after stopping a straggling instance
    /// (<c>MigrationRunner.BackfillSql</c> is the same statement).
    ///
    /// Copying is only half of the step. Because this migration is the gate that opens V3, it also
    /// verifies that the two columns now agree, and fails if they do not. Without that check the
    /// gate would report success on a database where an old and a new value disagree, and V3 would
    /// be switched on over the new value while the old one silently became unreachable at S3.
    /// </summary>
    public partial class S2B_BackfillExternalReference : Migration
    {
        /// <summary>
        /// The agreement check, shared with S3 and with <c>MigrationRunner.BackfillSql</c>.
        ///
        /// Two decisions are deliberate. First, only rows whose <c>CustomerReference</c> is
        /// populated are examined: a row V3 wrote holds the new column only, and that is a correct
        /// state, not a mismatch. Second, the comparison is forced to a binary collation. The
        /// database's default collation is case- and accent-insensitive, so a plain <c>&lt;&gt;</c>
        /// would call <c>PO-Ref</c> and <c>PO-REF</c> equal and let a real difference in the text
        /// pass the gate unnoticed. References are opaque identifiers; a difference in case is a
        /// difference.
        /// </summary>
        /// <param name="abortMessage">What the caller was about to do, named in the SQL error.</param>
        public static string AgreementCheckSql(string abortMessage) => $"""
            IF EXISTS (
                SELECT 1 FROM [Orders]
                WHERE [CustomerReference] IS NOT NULL
                  AND ([ExternalReference] IS NULL
                       OR [ExternalReference] COLLATE Latin1_General_BIN2
                          <> [CustomerReference] COLLATE Latin1_General_BIN2)
            )
                THROW 50001, 'Backfill mismatch: CustomerReference and ExternalReference disagree. {abortMessage}', 1;
            """;

        /// <summary>The copy half of the step, on its own. Re-runnable by construction.</summary>
        public const string CopySql = """
            UPDATE [Orders]
            SET [ExternalReference] = [CustomerReference]
            WHERE [ExternalReference] IS NULL AND [CustomerReference] IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CopySql);

            // EF runs the migration in a transaction, so a throw here rolls the copy back too and
            // both columns are left exactly as the operator will want to inspect them.
            migrationBuilder.Sql(
                AgreementCheckSql("Backfill gate aborted; both columns were left in place."));
        }

        /// <summary>
        /// Copying values is not undone: the old column still holds them, and clearing the new
        /// column would destroy data that V3 may since have written.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
