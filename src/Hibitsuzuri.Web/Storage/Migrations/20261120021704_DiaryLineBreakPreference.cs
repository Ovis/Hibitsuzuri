using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DiaryLineBreakPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireExplicitDiaryLineBreaks",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireExplicitDiaryLineBreaks",
                table: "AspNetUsers");
        }
    }
}
