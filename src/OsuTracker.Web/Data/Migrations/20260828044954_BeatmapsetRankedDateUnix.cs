using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsuTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BeatmapsetRankedDateUnix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Beatmapsets_RankedDate",
                table: "Beatmapsets");

            migrationBuilder.AddColumn<long>(
                name: "RankedDateUnix",
                table: "Beatmapsets",
                type: "INTEGER",
                nullable: true);

            // Backfill every set that was written before the shadow column existed —
            // without it the whole catalogue sorts as if it had never been ranked. Same
            // shape as the ScorePlayedAtUnix backfill: SQLite holds these as text like
            // "2024-10-07 08:00:00+00:00", the first 19 characters are what strftime
            // parses, and every stored offset is +00:00 so there is nothing to adjust.
            migrationBuilder.Sql(
                "UPDATE Beatmapsets SET RankedDateUnix = " +
                "CAST(strftime('%s', substr(RankedDate, 1, 19)) AS INTEGER) " +
                "WHERE RankedDate IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Beatmapsets_RankedDateUnix",
                table: "Beatmapsets",
                column: "RankedDateUnix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Beatmapsets_RankedDateUnix",
                table: "Beatmapsets");

            migrationBuilder.DropColumn(
                name: "RankedDateUnix",
                table: "Beatmapsets");

            migrationBuilder.CreateIndex(
                name: "IX_Beatmapsets_RankedDate",
                table: "Beatmapsets",
                column: "RankedDate");
        }
    }
}
