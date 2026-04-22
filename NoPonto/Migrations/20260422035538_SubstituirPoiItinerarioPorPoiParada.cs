using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class SubstituirPoiItinerarioPorPoiParada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoiItinerarios");

            migrationBuilder.CreateTable(
                name: "PoiParadas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PoiId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    DistanciaMetros = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoiParadas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PoiParadas_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoiParadas_Pois_PoiId",
                        column: x => x.PoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoiParadas_ParadaId",
                table: "PoiParadas",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_PoiParadas_ParadaId_PoiId",
                table: "PoiParadas",
                columns: new[] { "ParadaId", "PoiId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PoiParadas_PoiId",
                table: "PoiParadas",
                column: "PoiId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoiParadas");

            migrationBuilder.CreateTable(
                name: "PoiItinerarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItinerarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    PoiId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanciaMetros = table.Column<double>(type: "double precision", nullable: false),
                    PosicaoLinha = table.Column<double>(type: "double precision", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoiItinerarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PoiItinerarios_Itinerarios_ItinerarioId",
                        column: x => x.ItinerarioId,
                        principalTable: "Itinerarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoiItinerarios_Pois_PoiId",
                        column: x => x.PoiId,
                        principalTable: "Pois",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoiItinerarios_ItinerarioId",
                table: "PoiItinerarios",
                column: "ItinerarioId");

            migrationBuilder.CreateIndex(
                name: "IX_PoiItinerarios_PoiId",
                table: "PoiItinerarios",
                column: "PoiId");
        }
    }
}
