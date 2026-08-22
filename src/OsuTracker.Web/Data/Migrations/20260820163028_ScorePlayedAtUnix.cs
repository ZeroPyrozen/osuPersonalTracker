using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsuTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScorePlayedAtUnix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PlayedAtUnix",
                table: "Scores",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill the shadow column for rows written before it existed. SQLite
            // stores DateTimeOffset as text like "2021-06-28 07:13:55+00:00"; the first
            // 19 characters are exactly what strftime understands, and every stored
            // value is already UTC so no offset arithmetic is needed.
            migrationBuilder.Sql(
                "UPDATE Scores SET PlayedAtUnix = " +
                "CAST(strftime('%s', substr(PlayedAt, 1, 19)) AS INTEGER) " +
                "WHERE PlayedAtUnix = 0 AND PlayedAt IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_score_played",
                table: "Scores",
                column: "PlayedAtUnix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_score_played",
                table: "Scores");

            migrationBuilder.DropColumn(
                name: "PlayedAtUnix",
                table: "Scores");
        }
    }
}
