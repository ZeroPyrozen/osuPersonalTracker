using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsuTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScoreCountsAsPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CountsAsPass",
                table: "Scores",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountsAsPass",
                table: "Scores");
        }
    }
}
