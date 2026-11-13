using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDataTransfersAndNotebookLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notebooks_UserId_Name",
                table: "Notebooks");

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "Notebooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Notebooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RestoreTransfers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Ready = table.Column<bool>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestoreTransfers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_UserId_Name",
                table: "Notebooks",
                columns: new[] { "UserId", "Name" },
                unique: true,
                filter: "DeletedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreTransfers_UserId",
                table: "RestoreTransfers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestoreTransfers");

            migrationBuilder.DropIndex(
                name: "IX_Notebooks_UserId_Name",
                table: "Notebooks");

            migrationBuilder.DropColumn(
                name: "Archived",
                table: "Notebooks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Notebooks");

            migrationBuilder.CreateIndex(
                name: "IX_Notebooks_UserId_Name",
                table: "Notebooks",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }
    }
}
