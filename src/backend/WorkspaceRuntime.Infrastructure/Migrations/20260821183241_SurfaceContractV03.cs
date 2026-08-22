using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkspaceRuntime.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SurfaceContractV03 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "runtime_spreadsheets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "runtime_audit_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Principal",
                table: "runtime_audit_events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "runtime_approvals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "runtime_spreadsheets");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "runtime_audit_events");

            migrationBuilder.DropColumn(
                name: "Principal",
                table: "runtime_audit_events");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "runtime_approvals");
        }
    }
}
