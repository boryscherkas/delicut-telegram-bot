using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchesOriginalToPendingSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MatchesOriginal",
                table: "PendingSelections",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchesOriginal",
                table: "PendingSelections");
        }
    }
}
