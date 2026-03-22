using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMealTypeToPendingSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MealType",
                table: "PendingSelections",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MealType",
                table: "PendingSelections");
        }
    }
}
