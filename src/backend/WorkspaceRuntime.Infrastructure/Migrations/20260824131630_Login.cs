using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Login : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "runtime_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PasswordSetAt",
                table: "runtime_users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "runtime_api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_api_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_api_keys_SecretHash",
                table: "runtime_api_keys",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_sessions_SecretHash",
                table: "runtime_sessions",
                column: "SecretHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_api_keys");

            migrationBuilder.DropTable(
                name: "runtime_sessions");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "runtime_users");

            migrationBuilder.DropColumn(
                name: "PasswordSetAt",
                table: "runtime_users");
        }
    }
}
