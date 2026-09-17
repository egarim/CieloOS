using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Invites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                table: "runtime_users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "runtime_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RedeemedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RedeemedFrom = table.Column<string>(type: "TEXT", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SupersededAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstPreviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstPreviewedFrom = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_invites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_invites_CodeHash",
                table: "runtime_invites",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_invites_UserId",
                table: "runtime_invites",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_invites");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                table: "runtime_users");
        }
    }
}
