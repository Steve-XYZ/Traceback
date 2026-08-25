using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Traceback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "build_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    digest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    canonical_key = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                    uri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_artifacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "engineers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engineers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "observations",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_type_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_key = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observations", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    team = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_services", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    repository = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    authored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    author_engineer_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commits", x => x.id);
                    table.ForeignKey(
                        name: "fk_commits_engineers_author_engineer_id",
                        column: x => x.author_engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pull_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    external_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    repository = table.Column<string>(type: "text", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    merged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    author_engineer_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pull_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_pull_requests_engineers_author_engineer_id",
                        column: x => x.author_engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "work_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    assignee_engineer_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_items_engineers_assignee_engineer_id",
                        column: x => x.assignee_engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "service_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    external_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_instances_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_service_instances_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    external_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    workflow_name = table.Column<string>(type: "text", nullable: true),
                    run_number = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    conclusion = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    commit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_runs_commits_commit_id",
                        column: x => x.commit_id,
                        principalTable: "commits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pull_request_commits",
                columns: table => new
                {
                    pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    established_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pull_request_commits", x => new { x.pull_request_id, x.commit_id });
                    table.ForeignKey(
                        name: "fk_pull_request_commits_commits_commit_id",
                        column: x => x.commit_id,
                        principalTable: "commits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pull_request_commits_pull_requests_pull_request_id",
                        column: x => x.pull_request_id,
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_item_pull_requests",
                columns: table => new
                {
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    established_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_item_pull_requests", x => new { x.work_item_id, x.pull_request_id });
                    table.ForeignKey(
                        name: "fk_work_item_pull_requests_pull_requests_pull_request_id",
                        column: x => x.pull_request_id,
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_item_pull_requests_work_items_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    workflow_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ingested_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployments_build_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalTable: "build_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_workflow_runs_workflow_run_id",
                        column: x => x.workflow_run_id,
                        principalTable: "workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_run_artifacts",
                columns: table => new
                {
                    workflow_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    build_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    established_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_run_artifacts", x => new { x.workflow_run_id, x.build_artifact_id });
                    table.ForeignKey(
                        name: "fk_workflow_run_artifacts_build_artifacts_build_artifact_id",
                        column: x => x.build_artifact_id,
                        principalTable: "build_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_run_artifacts_workflow_runs_workflow_run_id",
                        column: x => x.workflow_run_id,
                        principalTable: "workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_key = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                    engineer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pull_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    commit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workflow_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    build_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identities", x => x.id);
                    table.CheckConstraint("ck_external_identities_type_match", "(entity_type_name = 'engineer' AND engineer_id IS NOT NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'work_item' AND work_item_id IS NOT NULL AND engineer_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'pull_request' AND pull_request_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'commit' AND commit_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'workflow_run' AND workflow_run_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'build_artifact' AND build_artifact_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'deployment' AND deployment_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND service_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service' AND service_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND environment_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'environment' AND environment_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND service_instance_id IS NULL) OR (entity_type_name = 'service_instance' AND service_instance_id IS NOT NULL AND engineer_id IS NULL AND work_item_id IS NULL AND pull_request_id IS NULL AND commit_id IS NULL AND workflow_run_id IS NULL AND build_artifact_id IS NULL AND deployment_id IS NULL AND service_id IS NULL AND environment_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_external_identities_build_artifacts_build_artifact_id",
                        column: x => x.build_artifact_id,
                        principalTable: "build_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_commits_commit_id",
                        column: x => x.commit_id,
                        principalTable: "commits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_engineers_engineer_id",
                        column: x => x.engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_pull_requests_pull_request_id",
                        column: x => x.pull_request_id,
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_service_instances_service_instance_id",
                        column: x => x.service_instance_id,
                        principalTable: "service_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_work_items_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_external_identities_workflow_runs_workflow_run_id",
                        column: x => x.workflow_run_id,
                        principalTable: "workflow_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_build_artifacts_canonical_key",
                table: "build_artifacts",
                column: "canonical_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commits_author_engineer_id",
                table: "commits",
                column: "author_engineer_id");

            migrationBuilder.CreateIndex(
                name: "ix_commits_sha",
                table: "commits",
                column: "sha",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_artifact_id_service_id_environment_id_deployed_",
                table: "deployments",
                columns: new[] { "artifact_id", "service_id", "environment_id", "deployed_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_environment_id",
                table: "deployments",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_service_id_environment_id_deployed_at",
                table: "deployments",
                columns: new[] { "service_id", "environment_id", "deployed_at" },
                descending: new[] { false, false, true })
                .Annotation("Npgsql:IndexMethod", "btree");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_workflow_run_id",
                table: "deployments",
                column: "workflow_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_engineers_email",
                table: "engineers",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_environments_name",
                table: "environments",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_build_artifact_id",
                table: "external_identities",
                column: "build_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_commit_id",
                table: "external_identities",
                column: "commit_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_deployment_id",
                table: "external_identities",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_engineer_id",
                table: "external_identities",
                column: "engineer_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_entity_type_name_external_key",
                table: "external_identities",
                columns: new[] { "entity_type_name", "external_key" });

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_environment_id",
                table: "external_identities",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_provider_entity_type_name_external_key",
                table: "external_identities",
                columns: new[] { "provider", "entity_type_name", "external_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_pull_request_id",
                table: "external_identities",
                column: "pull_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_service_id",
                table: "external_identities",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_service_instance_id",
                table: "external_identities",
                column: "service_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_work_item_id",
                table: "external_identities",
                column: "work_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_workflow_run_id",
                table: "external_identities",
                column: "workflow_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_observations_entity_type_name_external_key",
                table: "observations",
                columns: new[] { "entity_type_name", "external_key" });

            migrationBuilder.CreateIndex(
                name: "ix_observations_fingerprint",
                table: "observations",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_observations_observed_at",
                table: "observations",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "ix_pull_request_commits_commit_id",
                table: "pull_request_commits",
                column: "commit_id");

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_author_engineer_id",
                table: "pull_requests",
                column: "author_engineer_id");

            migrationBuilder.CreateIndex(
                name: "ix_pull_requests_external_name",
                table: "pull_requests",
                column: "external_name");

            migrationBuilder.CreateIndex(
                name: "ix_service_instances_environment_id",
                table: "service_instances",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_instances_service_id",
                table: "service_instances",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_services_name",
                table: "services",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_item_pull_requests_pull_request_id",
                table: "work_item_pull_requests",
                column: "pull_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_assignee_engineer_id",
                table: "work_items",
                column: "assignee_engineer_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_key",
                table: "work_items",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_run_artifacts_build_artifact_id",
                table: "workflow_run_artifacts",
                column: "build_artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_commit_id",
                table: "workflow_runs",
                column: "commit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identities");

            migrationBuilder.DropTable(
                name: "observations");

            migrationBuilder.DropTable(
                name: "pull_request_commits");

            migrationBuilder.DropTable(
                name: "work_item_pull_requests");

            migrationBuilder.DropTable(
                name: "workflow_run_artifacts");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "service_instances");

            migrationBuilder.DropTable(
                name: "pull_requests");

            migrationBuilder.DropTable(
                name: "work_items");

            migrationBuilder.DropTable(
                name: "build_artifacts");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "environments");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropTable(
                name: "commits");

            migrationBuilder.DropTable(
                name: "engineers");
        }
    }
}
