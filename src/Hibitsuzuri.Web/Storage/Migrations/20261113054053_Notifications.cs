using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hibitsuzuri.Web.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NotificationAfter",
                table: "TaskSlots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ReminderJson",
                table: "TaskSegments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "NotificationAfter",
                table: "TaskRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ReminderJson",
                table: "TaskRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "NotificationAfter",
                table: "Notebooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "PushDevices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    BrowserId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedSubscription = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowName = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnabledSince = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTestAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDevices_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    ReminderId = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    Test = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedToken = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDeliveries_PushDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "PushDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_DeviceId_Generation_TaskId_ReminderId_ScheduledAt",
                table: "PushDeliveries",
                columns: new[] { "DeviceId", "Generation", "TaskId", "ReminderId", "ScheduledAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_Finished_NextAttemptAt",
                table: "PushDeliveries",
                columns: new[] { "Finished", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_EndpointHash",
                table: "PushDevices",
                column: "EndpointHash");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_UserId_BrowserId",
                table: "PushDevices",
                columns: new[] { "UserId", "BrowserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDeliveries");

            migrationBuilder.DropTable(
                name: "PushDevices");

            migrationBuilder.DropColumn(
                name: "NotificationAfter",
                table: "TaskSlots");

            migrationBuilder.DropColumn(
                name: "ReminderJson",
                table: "TaskSegments");

            migrationBuilder.DropColumn(
                name: "NotificationAfter",
                table: "TaskRecords");

            migrationBuilder.DropColumn(
                name: "ReminderJson",
                table: "TaskRecords");

            migrationBuilder.DropColumn(
                name: "NotificationAfter",
                table: "Notebooks");
        }
    }
}
