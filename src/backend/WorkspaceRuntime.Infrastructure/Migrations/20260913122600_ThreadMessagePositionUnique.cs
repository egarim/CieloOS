using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThreadMessagePositionUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill BEFORE the index exists. The migration that added Sequence
            // defaulted every existing row to 0, so any thread already holding two
            // messages would collide on (ThreadId, 0) and the unique index would fail
            // to create — taking the whole startup down with it, because Migrate()
            // runs before the service binds. A correlated count is portable across
            // SQLite and Postgres, where UPDATE...FROM and window syntax are not.
            migrationBuilder.Sql("""
                UPDATE runtime_thread_messages
                SET Sequence = (
                    SELECT COUNT(*)
                    FROM runtime_thread_messages AS earlier
                    WHERE earlier.ThreadId = runtime_thread_messages.ThreadId
                      AND (earlier.CreatedAtTicks < runtime_thread_messages.CreatedAtTicks
                           OR (earlier.CreatedAtTicks = runtime_thread_messages.CreatedAtTicks
                               AND earlier.Id <= runtime_thread_messages.Id))
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_thread_messages_ThreadId_Sequence",
                table: "runtime_thread_messages",
                columns: new[] { "ThreadId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runtime_thread_messages_ThreadId_Sequence",
                table: "runtime_thread_messages");
        }
    }
}
