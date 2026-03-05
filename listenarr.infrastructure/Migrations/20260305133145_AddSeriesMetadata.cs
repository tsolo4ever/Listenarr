using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeriesMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesAsin = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    TotalBooks = table.Column<int>(type: "INTEGER", nullable: true),
                    IsComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCompleteInferred = table.Column<bool>(type: "INTEGER", nullable: true),
                    NewestBookDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMetadata", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesMetadata_SeriesAsin",
                table: "SeriesMetadata",
                column: "SeriesAsin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesMetadata");
        }
    }
}
