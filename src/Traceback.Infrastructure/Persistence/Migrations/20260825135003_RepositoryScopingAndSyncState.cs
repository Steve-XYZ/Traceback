using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Traceback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryScopingAndSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_external_identities_type_match",
                table: "external_identities");

            migrationBuilder.DropIndex(
                name: "ix_commits_sha",
                table: "commits");

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "workflow_runs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_state_at",
                table: "workflow_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository",
                table: "workflow_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "run_attempt",
                table: "workflow_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "run_id",
                table: "workflow_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_repository_id",
                table: "workflow_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trigger_event",
                table: "workflow_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "url",
                table: "workflow_runs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "base_branch",
                table: "pull_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "head_branch",
                table: "pull_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "head_sha",
                table: "pull_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "merge_commit_sha",
                table: "pull_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_state_at",
                table: "pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_repository_id",
                table: "pull_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_repository_id",
                table: "external_identities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "committed_at",
                table: "commits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "committer_engineer_id",
                table: "commits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_repository_id",
                table: "commits",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "source_repositories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    full_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    default_branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    provider_state_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_repositories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_states",
                columns: table => new
                {
                    integration_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cursor = table.Column<string>(type: "text", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_states", x => new { x.integration_id, x.resource_type });
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_source_repository_id_run_id_run_attempt",
                table: "workflow_runs",
                columns: new[] { "source_repository_id", "run_id", "run_attempt" },
                unique: true,
                filter: "source_repository_id IS NOT NULL AND run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_source_repository_id_started_at",
                table: "workflow_runs",
                columns: new[] { "source_repository_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_merged_at",
                table: "pull_requests",
                column: "merged_at");

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_source_repository_id_number",
                table: "pull_requests",
                columns: new[] { "source_repository_id", "number" },
                unique: true,
                filter: "source_repository_id IS NOT NULL AND number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_source_repository_id_updated_at",
                table: "pull_requests",
                columns: new[] { "source_repository_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_source_repository_id",
                table: "external_identities",
                column: "source_repository_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_external_identities_type_match",
                table: "external_identities",
                sql: "(entity_type_name = 'engineer' AND engineer_id IS NOT NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'repository' AND source_repository_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'work_item' AND work_item_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'pull_request' AND pull_request_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'commit' AND commit_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'workflow_run' AND workflow_run_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'build_artifact' AND build_artifact_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'deployment' AND deployment_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service' AND service_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'environment' AND environment_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service_instance' AND service_instance_id IS NOT NULL AND engineer_id IS NULL AND source_repository_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_commits_committer_engineer_id",
                table: "commits",
                column: "committer_engineer_id");

            migrationBuilder.CreateIndex(
                name: "ix_commits_sha",
                table: "commits",
                column: "sha");

            migrationBuilder.CreateIndex(
                name: "ix_commits_source_repository_id_authored_at",
                table: "commits",
                columns: new[] { "source_repository_id", "authored_at" });

            migrationBuilder.CreateIndex(
                name: "ix_commits_source_repository_id_sha",
                table: "commits",
                columns: new[] { "source_repository_id", "sha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_repositories_created_by_provider_key",
                table: "source_repositories",
                columns: new[] { "created_by_provider", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_states_updated_at",
                table: "sync_states",
                column: "updated_at");

            migrationBuilder.AddForeignKey(
                name: "fk_commits_engineers_committer_engineer_id",
                table: "commits",
                column: "committer_engineer_id",
                principalTable: "engineers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_commits_source_repositories_source_repository_id",
                table: "commits",
                column: "source_repository_id",
                principalTable: "source_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_external_identities_source_repositories_source_repository_id",
                table: "external_identities",
                column: "source_repository_id",
                principalTable: "source_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_pull_requests_source_repositories_source_repository_id",
                table: "pull_requests",
                column: "source_repository_id",
                principalTable: "source_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_runs_source_repositories_source_repository_id",
                table: "workflow_runs",
                column: "source_repository_id",
                principalTable: "source_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_commits_engineers_committer_engineer_id",
                table: "commits");

            migrationBuilder.DropForeignKey(
                name: "fk_commits_source_repositories_source_repository_id",
                table: "commits");

            migrationBuilder.DropForeignKey(
                name: "fk_external_identities_source_repositories_source_repository_id",
                table: "external_identities");

            migrationBuilder.DropForeignKey(
                name: "fk_pull_requests_source_repositories_source_repository_id",
                table: "pull_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_workflow_runs_source_repositories_source_repository_id",
                table: "workflow_runs");

            migrationBuilder.DropTable(
                name: "source_repositories");

            migrationBuilder.DropTable(
                name: "sync_states");

            migrationBuilder.DropIndex(
                name: "ix_workflow_runs_source_repository_id_run_id_run_attempt",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "ix_workflow_runs_source_repository_id_started_at",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "ix_pull_requests_merged_at",
                table: "pull_requests");

            migrationBuilder.DropIndex(
                name: "ix_pull_requests_source_repository_id_number",
                table: "pull_requests");

            migrationBuilder.DropIndex(
                name: "ix_pull_requests_source_repository_id_updated_at",
                table: "pull_requests");

            migrationBuilder.DropIndex(
                name: "ix_external_identities_source_repository_id",
                table: "external_identities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_external_identities_type_match",
                table: "external_identities");

            migrationBuilder.DropIndex(
                name: "ix_commits_committer_engineer_id",
                table: "commits");

            migrationBuilder.DropIndex(
                name: "ix_commits_sha",
                table: "commits");

            migrationBuilder.DropIndex(
                name: "ix_commits_source_repository_id_authored_at",
                table: "commits");

            migrationBuilder.DropIndex(
                name: "ix_commits_source_repository_id_sha",
                table: "commits");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "provider_state_at",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "repository",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "run_attempt",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "run_id",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "source_repository_id",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "trigger_event",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "url",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "base_branch",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "head_branch",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "head_sha",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "merge_commit_sha",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "provider_state_at",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "source_repository_id",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "pull_requests");

            migrationBuilder.DropColumn(
                name: "source_repository_id",
                table: "external_identities");

            migrationBuilder.DropColumn(
                name: "committed_at",
                table: "commits");

            migrationBuilder.DropColumn(
                name: "committer_engineer_id",
                table: "commits");

            migrationBuilder.DropColumn(
                name: "source_repository_id",
                table: "commits");

            migrationBuilder.AddCheckConstraint(
                name: "ck_external_identities_type_match",
                table: "external_identities",
                sql: "(entity_type_name = 'engineer' AND engineer_id IS NOT NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'work_item' AND work_item_id IS NOT NULL AND engineer_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'pull_request' AND pull_request_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'commit' AND commit_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'workflow_run' AND workflow_run_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'build_artifact' AND build_artifact_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'deployment' AND deployment_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service' AND service_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'environment' AND environment_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service_instance' AND service_instance_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_commits_sha",
                table: "commits",
                column: "sha",
                unique: true);
        }
    }
}
