using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_project_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberSlug = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AddedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_project_members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_project_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthorSlug = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_project_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_project_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssigneeSlug = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_project_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrgSlug = table.Column<string>(type: "TEXT", nullable: false),
                    LeadSlug = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_members_MemberSlug",
                table: "runtime_project_members",
                column: "MemberSlug");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_members_ProjectId_MemberSlug",
                table: "runtime_project_members",
                columns: new[] { "ProjectId", "MemberSlug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_reports_TaskId",
                table: "runtime_project_reports",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_reports_TaskId_Sequence",
                table: "runtime_project_reports",
                columns: new[] { "TaskId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_tasks_AssigneeSlug",
                table: "runtime_project_tasks",
                column: "AssigneeSlug");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_tasks_ProjectId",
                table: "runtime_project_tasks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_project_tasks_ProjectId_Sequence",
                table: "runtime_project_tasks",
                columns: new[] { "ProjectId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_projects_LeadSlug",
                table: "runtime_projects",
                column: "LeadSlug");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_projects_OrgSlug",
                table: "runtime_projects",
                column: "OrgSlug");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_project_members");

            migrationBuilder.DropTable(
                name: "runtime_project_reports");

            migrationBuilder.DropTable(
                name: "runtime_project_tasks");

            migrationBuilder.DropTable(
                name: "runtime_projects");
        }
    }
}
