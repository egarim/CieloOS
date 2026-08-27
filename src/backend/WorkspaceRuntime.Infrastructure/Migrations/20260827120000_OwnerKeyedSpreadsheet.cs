using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OwnerKeyedSpreadsheet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerSlug",
                table: "runtime_spreadsheets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE runtime_spreadsheets SET OwnerSlug = (SELECT Slug FROM runtime_users ORDER BY rowid LIMIT 1) WHERE Id = 1 AND EXISTS (SELECT 1 FROM runtime_users);");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_spreadsheets_OwnerSlug",
                table: "runtime_spreadsheets",
                column: "OwnerSlug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runtime_spreadsheets_OwnerSlug",
                table: "runtime_spreadsheets");

            migrationBuilder.DropColumn(
                name: "OwnerSlug",
                table: "runtime_spreadsheets");
        }
    }
}
