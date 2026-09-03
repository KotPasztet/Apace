using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solace.LauncherUI.Migrations
{
    /// <inheritdoc />
    // Note: the scaffolded model refresh (EF 10.0.5 -> 10.0.7 snapshot) also wants to drop the
    // obsolete AspNetUserPasskeys table; that unrelated/destructive change is intentionally NOT
    // included here - this migration only adds the LinkedGameAccounts table.
    public partial class AddLinkedGameAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkedGameAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PanelUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedGameAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PanelUser_Player",
                table: "LinkedGameAccounts",
                columns: new[] { "PanelUserId", "PlayerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkedGameAccounts");
        }
    }
}
