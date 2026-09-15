using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Organizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMachineOwner",
                table: "runtime_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OrgSlug",
                table: "runtime_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "runtime_organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_organizations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_users_OrgSlug",
                table: "runtime_users",
                column: "OrgSlug");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_organizations_Slug",
                table: "runtime_organizations",
                column: "Slug",
                unique: true);

            // Everything above this line is schema. Everything below is the part
            // that decides whether a machine that already has people on it still
            // works on Monday.
            //
            // The generated defaults alone would leave every existing user with
            // OrgSlug = '' — an organization that does not exist, so they can see
            // nobody, not even each other — and IsMachineOwner = 0 for all of them,
            // so NOBODY could create a user or an organization ever again. The
            // machine would come up, serve every page, and be quietly bricked.
            //
            // Raw SQL because a migration must not depend on the C# model, which
            // will have moved on by the time anyone runs this on an old database.
            // Formats are the ones EF's SQLite provider reads back: a lowercase
            // hyphenated GUID, and 'yyyy-MM-dd HH:mm:ss.fffffffzzz' for a
            // DateTimeOffset.
            migrationBuilder.Sql(
                """
                INSERT INTO runtime_organizations (Id, Slug, DisplayName, CreatedAt, CreatedTicks)
                SELECT '44444444-4444-4444-4444-444444444441', 'main', 'Main',
                       '2026-09-15 00:00:00.0000000+00:00', 639000000000000000
                WHERE NOT EXISTS (SELECT 1 FROM runtime_organizations WHERE Slug = 'main');
                """);

            // The people who predate organizations join the founding one, and keep
            // their bare slugs. A prefix records how a user was minted, not where
            // they belong, so there is nothing to rename — which is the whole reason
            // this upgrade does not have to touch a podman volume, a token file, an
            // audit principal or a spreadsheet key.
            migrationBuilder.Sql("UPDATE runtime_users SET OrgSlug = 'main' WHERE OrgSlug = '';");

            // Exactly one machine owner, and only if there is not one already.
            //
            // `ORDER BY rowid LIMIT 1` is the same founding-owner heuristic the
            // owner-keyed-spreadsheet migration used, and it is a heuristic: on a
            // machine where the first row is not the person who claimed it, the
            // wrong human gets the flag. That is recoverable by hand in one UPDATE.
            // Marking nobody is not recoverable at all through the product.
            migrationBuilder.Sql(
                """
                UPDATE runtime_users SET IsMachineOwner = 1
                WHERE Id = (SELECT Id FROM runtime_users ORDER BY rowid LIMIT 1)
                  AND NOT EXISTS (SELECT 1 FROM runtime_users WHERE IsMachineOwner = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_organizations");

            migrationBuilder.DropIndex(
                name: "IX_runtime_users_OrgSlug",
                table: "runtime_users");

            migrationBuilder.DropColumn(
                name: "IsMachineOwner",
                table: "runtime_users");

            migrationBuilder.DropColumn(
                name: "OrgSlug",
                table: "runtime_users");
        }
    }
}
