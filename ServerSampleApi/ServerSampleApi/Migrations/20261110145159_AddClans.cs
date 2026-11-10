using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerSampleApi.Migrations
{
    /// <inheritdoc />
    public partial class AddClans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 20, nullable: false),
                    LeaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clans_Players_LeaderId",
                        column: x => x.LeaderId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClanMembers",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClanId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClanMembers", x => x.PlayerId);
                    table.ForeignKey(
                        name: "FK_ClanMembers_Clans_ClanId",
                        column: x => x.ClanId,
                        principalTable: "Clans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClanMembers_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClanMembers_ClanId",
                table: "ClanMembers",
                column: "ClanId");

            migrationBuilder.CreateIndex(
                name: "IX_Clans_LeaderId",
                table: "Clans",
                column: "LeaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Clans_Name",
                table: "Clans",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClanMembers");

            migrationBuilder.DropTable(
                name: "Clans");
        }
    }
}
