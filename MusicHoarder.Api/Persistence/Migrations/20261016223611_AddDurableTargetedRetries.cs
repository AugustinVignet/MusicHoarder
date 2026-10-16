using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableTargetedRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomationHeld",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PipelineStageAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    InputSha256 = table.Column<string>(type: "text", nullable: true),
                    CandidateJson = table.Column<string>(type: "text", nullable: true),
                    AttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextRetryAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStageAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStageStates",
                columns: table => new
                {
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextRetryAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InputSha256 = table.Column<string>(type: "text", nullable: true),
                    CandidateJson = table.Column<string>(type: "text", nullable: true),
                    SourceWarningCode = table.Column<string>(type: "text", nullable: true),
                    SourceWarningInputSha256 = table.Column<string>(type: "text", nullable: true),
                    Targeted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStageStates", x => new { x.SongId, x.Stage });
                });

            migrationBuilder.CreateTable(
                name: "TargetedRetryJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetedRetryJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetedRetryItems",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: true),
                    InputSha256 = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetedRetryItems", x => new { x.JobId, x.SongId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_TargetedRetryItems_TargetedRetryJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "TargetedRetryJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStageAttempts_JobId_SongId_Stage",
                table: "PipelineStageAttempts",
                columns: new[] { "JobId", "SongId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStageAttempts_OwnerUserId_SongId_Stage_AttemptedAtU~",
                table: "PipelineStageAttempts",
                columns: new[] { "OwnerUserId", "SongId", "Stage", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStageStates_OwnerUserId_Stage_State_SongId",
                table: "PipelineStageStates",
                columns: new[] { "OwnerUserId", "Stage", "State", "SongId" });

            migrationBuilder.CreateIndex(
                name: "IX_TargetedRetryJobs_OwnerUserId_RequestId",
                table: "TargetedRetryJobs",
                columns: new[] { "OwnerUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetedRetryJobs_State_CreatedAtUtc",
                table: "TargetedRetryJobs",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineStageAttempts");

            migrationBuilder.DropTable(
                name: "PipelineStageStates");

            migrationBuilder.DropTable(
                name: "TargetedRetryItems");

            migrationBuilder.DropTable(
                name: "TargetedRetryJobs");

            migrationBuilder.DropColumn(
                name: "AutomationHeld",
                table: "Songs");
        }
    }
}
