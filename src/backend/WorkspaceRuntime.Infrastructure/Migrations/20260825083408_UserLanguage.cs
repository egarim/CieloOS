using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "runtime_users",
                type: "TEXT",
                nullable: false,
                // Every user that existed before languages did is English, stated rather
                // than left empty: "" resolves to English anyway, but a blank in the
                // column reads as "nobody chose", which is a different fact.
                defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "runtime_users");
        }
    }
}
