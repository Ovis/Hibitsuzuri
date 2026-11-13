using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ImageReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiaryImageReferences",
                columns: table => new
                {
                    ImageId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiaryImageReferences", x => new { x.ImageId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_DiaryImageReferences_DiaryEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "DiaryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiaryImageReferences_DiaryImages_ImageId",
                        column: x => x.ImageId,
                        principalTable: "DiaryImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiaryImageReferences_EntryId",
                table: "DiaryImageReferences",
                column: "EntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiaryImageReferences");
        }
    }
}
