using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DirectMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_direct_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: false),
                    FromSlug = table.Column<string>(type: "TEXT", nullable: false),
                    ToSlug = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_direct_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_direct_messages_ConversationKey",
                table: "runtime_direct_messages",
                column: "ConversationKey");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_direct_messages_ConversationKey_Sequence",
                table: "runtime_direct_messages",
                columns: new[] { "ConversationKey", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_direct_messages_ToSlug_ReadAt",
                table: "runtime_direct_messages",
                columns: new[] { "ToSlug", "ReadAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_direct_messages");
        }
    }
}
