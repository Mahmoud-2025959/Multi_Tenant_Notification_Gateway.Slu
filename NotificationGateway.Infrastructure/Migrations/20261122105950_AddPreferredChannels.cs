using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationGateway.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferredChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredChannels",
                table: "NotificationUserPreferences",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredChannels",
                table: "NotificationUserPreferences");
        }
    }
}
