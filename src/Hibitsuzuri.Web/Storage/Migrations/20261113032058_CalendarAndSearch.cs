using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CalendarAndSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlainText",
                table: "DiaryEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "DiaryEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Anniversaries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    LeapDayPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    AllNotebooks = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anniversaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Anniversaries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnniversaryNotebook",
                columns: table => new
                {
                    AnniversaryId = table.Column<string>(type: "TEXT", nullable: false),
                    NotebookId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnniversaryNotebook", x => new { x.AnniversaryId, x.NotebookId });
                    table.ForeignKey(
                        name: "FK_AnniversaryNotebook_Anniversaries_AnniversaryId",
                        column: x => x.AnniversaryId,
                        principalTable: "Anniversaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnniversaryNotebook_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Anniversaries_UserId",
                table: "Anniversaries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AnniversaryNotebook_NotebookId",
                table: "AnniversaryNotebook",
                column: "NotebookId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnniversaryNotebook");

            migrationBuilder.DropTable(
                name: "Anniversaries");

            migrationBuilder.DropColumn(
                name: "PlainText",
                table: "DiaryEntries");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "DiaryEntries");
        }
    }
}
