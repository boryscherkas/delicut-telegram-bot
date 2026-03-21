using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMacroGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CarbGoalGrams",
                table: "UserSettings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FatGoalGrams",
                table: "UserSettings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ProteinGoalGrams",
                table: "UserSettings",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CarbGoalGrams",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "FatGoalGrams",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ProteinGoalGrams",
                table: "UserSettings");
        }
    }
}
