using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddUseAiSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseAiSelection",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseAiSelection",
                table: "UserSettings");
        }
    }
}
