using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Tasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "TasksPausedOn",
                table: "Notebooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskMutations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskMutations", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_TaskMutations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskPauses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NotebookId = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Until = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskPauses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskPauses_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskSeries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NotebookId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSeries_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NotebookId = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<string>(type: "TEXT", nullable: true),
                    SegmentId = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduledDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", nullable: false),
                    SearchText = table.Column<string>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Skipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    Edited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRecords_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskRecords_TaskSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "TaskSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskSegments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", nullable: false),
                    SearchText = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RuleJson = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSegments_TaskSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "TaskSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskSlots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    SegmentId = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Waiting = table.Column<bool>(type: "INTEGER", nullable: false),
                    Frozen = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSlots_TaskSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "TaskSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskWindows",
                columns: table => new
                {
                    SegmentId = table.Column<string>(type: "TEXT", nullable: false),
                    Month = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    HolidayRevision = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskWindows", x => new { x.SegmentId, x.Month });
                    table.ForeignKey(
                        name: "FK_TaskWindows_TaskSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "TaskSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskPauses_NotebookId",
                table: "TaskPauses",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_NotebookId_CompletedAt_Id",
                table: "TaskRecords",
                columns: new[] { "NotebookId", "CompletedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_NotebookId_DueDate",
                table: "TaskRecords",
                columns: new[] { "NotebookId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_SeriesId_ScheduledDate",
                table: "TaskRecords",
                columns: new[] { "SeriesId", "ScheduledDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskSegments_SeriesId",
                table: "TaskSegments",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSeries_NotebookId",
                table: "TaskSeries",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskSlots_SegmentId_DueDate",
                table: "TaskSlots",
                columns: new[] { "SegmentId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskSlots_SegmentId_ScheduledDate",
                table: "TaskSlots",
                columns: new[] { "SegmentId", "ScheduledDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskMutations");

            migrationBuilder.DropTable(
                name: "TaskPauses");

            migrationBuilder.DropTable(
                name: "TaskRecords");

            migrationBuilder.DropTable(
                name: "TaskSlots");

            migrationBuilder.DropTable(
                name: "TaskWindows");

            migrationBuilder.DropTable(
                name: "TaskSegments");

            migrationBuilder.DropTable(
                name: "TaskSeries");

            migrationBuilder.DropColumn(
                name: "TasksPausedOn",
                table: "Notebooks");
        }
    }
}
