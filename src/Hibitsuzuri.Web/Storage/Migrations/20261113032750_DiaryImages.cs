using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DiaryImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepOriginalImages",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DiaryImages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    NotebookId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiaryImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiaryImages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiaryImages_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageDraftLeases",
                columns: table => new
                {
                    ImageId = table.Column<string>(type: "TEXT", nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageDraftLeases", x => new { x.ImageId, x.DraftId });
                    table.ForeignKey(
                        name: "FK_ImageDraftLeases_DiaryImages_ImageId",
                        column: x => x.ImageId,
                        principalTable: "DiaryImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiaryImages_NotebookId",
                table: "DiaryImages",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_DiaryImages_UserId",
                table: "DiaryImages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageDraftLeases_ExpiresAt",
                table: "ImageDraftLeases",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageDraftLeases");

            migrationBuilder.DropTable(
                name: "DiaryImages");

            migrationBuilder.DropColumn(
                name: "KeepOriginalImages",
                table: "AspNetUsers");
        }
    }
}
