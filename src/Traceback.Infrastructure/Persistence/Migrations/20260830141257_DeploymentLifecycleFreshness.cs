using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Traceback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentLifecycleFreshness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_state_at",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "provider_state_at",
                table: "deployments");
        }
    }
}
