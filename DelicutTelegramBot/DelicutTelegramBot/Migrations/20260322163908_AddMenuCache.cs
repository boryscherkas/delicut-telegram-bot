using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicutTelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MenuCaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MealCategory = table.Column<string>(type: "text", nullable: false),
                    Dishes = table.Column<string>(type: "jsonb", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuCaches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MenuCaches_UserId_DeliveryDate_MealCategory",
                table: "MenuCaches",
                columns: new[] { "UserId", "DeliveryDate", "MealCategory" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MenuCaches");
        }
    }
}
