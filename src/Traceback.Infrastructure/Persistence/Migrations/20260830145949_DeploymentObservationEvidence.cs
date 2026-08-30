using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Traceback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentObservationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "deployment_id",
                table: "observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_observations_deployment_id",
                table: "observations",
                column: "deployment_id");

            migrationBuilder.AddForeignKey(
                name: "fk_observations_deployments_deployment_id",
                table: "observations",
                column: "deployment_id",
                principalTable: "deployments",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_observations_deployments_deployment_id",
                table: "observations");

            migrationBuilder.DropIndex(
                name: "ix_observations_deployment_id",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "deployment_id",
                table: "observations");
        }
    }
}
