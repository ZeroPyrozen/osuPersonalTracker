using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsuTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayCountCheckedCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckedCount",
                table: "PlayCounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckedCount",
                table: "PlayCounts");
        }
    }
}
