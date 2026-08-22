using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsuTracker.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Beatmapsets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatorUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RankedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SubmittedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Bpm = table.Column<double>(type: "REAL", nullable: false),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SeenInRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Beatmapsets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobs",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ItemsDone = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RunStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobs", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Beatmaps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    BeatmapsetId = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    DifficultyName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StarRating = table.Column<double>(type: "REAL", nullable: false),
                    TotalLength = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCombo = table.Column<int>(type: "INTEGER", nullable: true),
                    Cs = table.Column<double>(type: "REAL", nullable: false),
                    Ar = table.Column<double>(type: "REAL", nullable: false),
                    Od = table.Column<double>(type: "REAL", nullable: false),
                    Hp = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenInRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Beatmaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Beatmaps_Beatmapsets_BeatmapsetId",
                        column: x => x.BeatmapsetId,
                        principalTable: "Beatmapsets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayCounts",
                columns: table => new
                {
                    BeatmapId = table.Column<long>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayCounts", x => x.BeatmapId);
                    table.ForeignKey(
                        name: "FK_PlayCounts_Beatmaps_BeatmapId",
                        column: x => x.BeatmapId,
                        principalTable: "Beatmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Scores",
                columns: table => new
                {
                    BeatmapId = table.Column<long>(type: "INTEGER", nullable: false),
                    ScoreId = table.Column<long>(type: "INTEGER", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Grade = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    MaxCombo = table.Column<int>(type: "INTEGER", nullable: true),
                    Mods = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsLazer = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scores", x => x.BeatmapId);
                    table.ForeignKey(
                        name: "FK_Scores_Beatmaps_BeatmapId",
                        column: x => x.BeatmapId,
                        principalTable: "Beatmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beatmap_mode_stars",
                table: "Beatmaps",
                columns: new[] { "Mode", "StarRating" });

            migrationBuilder.CreateIndex(
                name: "ix_beatmap_mode_status",
                table: "Beatmaps",
                columns: new[] { "Mode", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_beatmap_set",
                table: "Beatmaps",
                column: "BeatmapsetId");

            migrationBuilder.CreateIndex(
                name: "IX_Beatmapsets_RankedDate",
                table: "Beatmapsets",
                column: "RankedDate");

            migrationBuilder.CreateIndex(
                name: "IX_Beatmapsets_Status",
                table: "Beatmapsets",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayCounts");

            migrationBuilder.DropTable(
                name: "Scores");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SyncJobs");

            migrationBuilder.DropTable(
                name: "Beatmaps");

            migrationBuilder.DropTable(
                name: "Beatmapsets");
        }
    }
}
