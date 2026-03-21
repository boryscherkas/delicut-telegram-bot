using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddProteinVariantAndFavourites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<List<string>>(
                name: "StopWords",
                table: "UserSettings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "FavouriteDishNames",
                table: "UserSettings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<int>(
                name: "MinFavouritesPerWeek",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PreferredProteinVariant",
                table: "UserSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FavouriteDishNames",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "MinFavouritesPerWeek",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PreferredProteinVariant",
                table: "UserSettings");

            migrationBuilder.AlterColumn<List<string>>(
                name: "StopWords",
                table: "UserSettings",
                type: "text[]",
                nullable: false,
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValueSql: "'{}'::text[]");
        }
    }
}
