using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedReviewJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewAudioCache",
                columns: table => new
                {
                    Version = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "text", nullable: false),
                    ProofJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewAudioCache", x => new { x.Version, x.ContentSha256 });
                });

            migrationBuilder.CreateTable(
                name: "ReviewCopyJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RolledBackAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCopyJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewCopyJobs_ReviewPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "ReviewPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewCopyTracks",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureStage = table.Column<string>(type: "text", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    StagingPath = table.Column<string>(type: "text", nullable: false),
                    BackupPath = table.Column<string>(type: "text", nullable: false),
                    OldDestinationPath = table.Column<string>(type: "text", nullable: true),
                    OldPreviousPath = table.Column<string>(type: "text", nullable: true),
                    BeforeJson = table.Column<string>(type: "text", nullable: false),
                    AfterJson = table.Column<string>(type: "text", nullable: false),
                    SourceSha256 = table.Column<string>(type: "text", nullable: false),
                    DestinationBeforeSha256 = table.Column<string>(type: "text", nullable: true),
                    CandidateSha256 = table.Column<string>(type: "text", nullable: true),
                    ProofJson = table.Column<string>(type: "text", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCopyTracks", x => new { x.JobId, x.SongId });
                    table.ForeignKey(
                        name: "FK_ReviewCopyTracks_ReviewCopyJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ReviewCopyJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCopyJobs_OwnerUserId_RequestId",
                table: "ReviewCopyJobs",
                columns: new[] { "OwnerUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCopyJobs_PlanId",
                table: "ReviewCopyJobs",
                column: "PlanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCopyJobs_State_CreatedAtUtc",
                table: "ReviewCopyJobs",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCopyTracks_SongId_State",
                table: "ReviewCopyTracks",
                columns: new[] { "SongId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewAudioCache");

            migrationBuilder.DropTable(
                name: "ReviewCopyTracks");

            migrationBuilder.DropTable(
                name: "ReviewCopyJobs");
        }
    }
}
