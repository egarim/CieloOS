using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeskProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is "office", not the generated "": every user that
            // exists before desk profiles did has the office desk, and SQLite's
            // ADD COLUMN writes this default into those rows. An empty string
            // would resolve to the same desk, but the stored value should say
            // what is true rather than rely on a fallback.
            migrationBuilder.AddColumn<string>(
                name: "DeskProfile",
                table: "runtime_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "office");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeskProfile",
                table: "runtime_users");
        }
    }
}
