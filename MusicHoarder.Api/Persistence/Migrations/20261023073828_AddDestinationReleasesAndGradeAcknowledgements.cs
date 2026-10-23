using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDestinationReleasesAndGradeAcknowledgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewDestinationReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    FoldedPath = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedJobIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    ClearedPreviousSongIds = table.Column<List<int>>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewDestinationReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SongGradeAcknowledgements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    GradeId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongGradeAcknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongGradeAcknowledgements_SongQualityGrades_GradeId",
                        column: x => x.GradeId,
                        principalTable: "SongQualityGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDestinationReleases_FoldedPath",
                table: "ReviewDestinationReleases",
                column: "FoldedPath");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDestinationReleases_OwnerUserId_RequestId",
                table: "ReviewDestinationReleases",
                columns: new[] { "OwnerUserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SongGradeAcknowledgements_GradeId",
                table: "SongGradeAcknowledgements",
                column: "GradeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SongGradeAcknowledgements_OwnerUserId_SongId",
                table: "SongGradeAcknowledgements",
                columns: new[] { "OwnerUserId", "SongId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewDestinationReleases");

            migrationBuilder.DropTable(
                name: "SongGradeAcknowledgements");
        }
    }
}
