using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableCopyAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CopyAuditJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeJson = table.Column<string>(type: "text", nullable: false),
                    ExpectedContextRevision = table.Column<string>(type: "text", nullable: false),
                    ObservedContextRevision = table.Column<string>(type: "text", nullable: true),
                    ScopeState = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    WaitReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopyAuditJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CopyAuditTracks",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    ExpectedDbRevision = table.Column<string>(type: "text", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    TransientFailures = table.Column<int>(type: "integer", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceiptJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopyAuditTracks", x => new { x.JobId, x.SongId });
                    table.ForeignKey(
                        name: "FK_CopyAuditTracks_CopyAuditJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CopyAuditJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CopyAuditAttempts",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BytesHashed = table.Column<long>(type: "bigint", nullable: false),
                    DecodeAttempts = table.Column<int>(type: "integer", nullable: false),
                    CacheHits = table.Column<int>(type: "integer", nullable: false),
                    ReceiptJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopyAuditAttempts", x => new { x.JobId, x.SongId, x.Number });
                    table.ForeignKey(
                        name: "FK_CopyAuditAttempts_CopyAuditTracks_JobId_SongId",
                        columns: x => new { x.JobId, x.SongId },
                        principalTable: "CopyAuditTracks",
                        principalColumns: new[] { "JobId", "SongId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CopyAuditJobs_OwnerUserId_RequestId",
                table: "CopyAuditJobs",
                columns: new[] { "OwnerUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopyAuditJobs_State_NextAttemptAtUtc_CreatedAtUtc",
                table: "CopyAuditJobs",
                columns: new[] { "State", "NextAttemptAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CopyAuditTracks_State_NextAttemptAtUtc",
                table: "CopyAuditTracks",
                columns: new[] { "State", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CopyAuditAttempts");

            migrationBuilder.DropTable(
                name: "CopyAuditTracks");

            migrationBuilder.DropTable(
                name: "CopyAuditJobs");
        }
    }
}
