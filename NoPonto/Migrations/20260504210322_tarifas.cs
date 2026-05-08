using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class tarifas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tarifas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinhaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tarifa = table.Column<decimal>(type: "numeric", nullable: false),
                    ValidoDe = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidoAte = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Fonte = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tarifas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tarifas_Linhas_LinhaId",
                        column: x => x.LinhaId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tarifas_Modais_ModalId",
                        column: x => x.ModalId,
                        principalTable: "Modais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tarifas_LinhaId",
                table: "Tarifas",
                column: "LinhaId");

            migrationBuilder.CreateIndex(
                name: "IX_Tarifas_LinhaId_ValidoDe",
                table: "Tarifas",
                columns: new[] { "LinhaId", "ValidoDe" });

            migrationBuilder.CreateIndex(
                name: "IX_Tarifas_ModalId",
                table: "Tarifas",
                column: "ModalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tarifas");
        }
    }
}
