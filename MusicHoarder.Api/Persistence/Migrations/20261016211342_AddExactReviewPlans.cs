using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExactReviewPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReviewPlanId",
                table: "Songs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                table: "SongMetadataChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceReferencesJson",
                table: "SongMetadataChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "SongMetadataChanges",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewPlanRevision",
                table: "SongMetadataChanges",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReviewPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    ContextJson = table.Column<string>(type: "text", nullable: false),
                    PreviewJson = table.Column<string>(type: "text", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewPlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewPlans_OwnerUserId_CreatedAtUtc",
                table: "ReviewPlans",
                columns: new[] { "OwnerUserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewPlans");

            migrationBuilder.DropColumn(
                name: "ReviewPlanId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "SongMetadataChanges");

            migrationBuilder.DropColumn(
                name: "EvidenceReferencesJson",
                table: "SongMetadataChanges");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "SongMetadataChanges");

            migrationBuilder.DropColumn(
                name: "ReviewPlanRevision",
                table: "SongMetadataChanges");
        }
    }
}
