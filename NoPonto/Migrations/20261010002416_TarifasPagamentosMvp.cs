using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class TarifasPagamentosMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Destrutiva por autorização: descarta exclusivamente os registros tarifários legados.
            // Sem CASCADE: qualquer dependência externa inesperada bloqueia a operação antes da exclusão.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '15s';
                LOCK TABLE "Tarifas" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint
                               WHERE contype = 'f' AND confrelid = '"Tarifas"'::regclass) THEN
                        RAISE EXCEPTION 'Tarifas possui foreign keys de entrada não previstas; revisar dependências';
                    END IF;
                END $$;
                DELETE FROM "Tarifas";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Tarifas_LinhaId",
                table: "Tarifas");

            migrationBuilder.DropIndex(
                name: "IX_Tarifas_LinhaId_ValidoDe",
                table: "Tarifas");

            migrationBuilder.DropIndex(
                name: "IX_Tarifas_ModalId",
                table: "Tarifas");

            migrationBuilder.DropColumn(
                name: "Ativo",
                table: "Tarifas");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Tarifas");

            migrationBuilder.DropColumn(
                name: "ValidoAte",
                table: "Tarifas");

            migrationBuilder.RenameColumn(
                name: "Tarifa",
                table: "Tarifas",
                newName: "Valor");

            migrationBuilder.RenameColumn(
                name: "ValidoDe",
                table: "Tarifas",
                newName: "CriadoEmUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "Tarifas",
                newName: "AtualizadoEmUtc");

            migrationBuilder.AlterColumn<Guid>(
                name: "ModalId",
                table: "Tarifas",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "LinhaId",
                table: "Tarifas",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Fonte",
                table: "Tarifas",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<decimal>(
                name: "Valor",
                table: "Tarifas",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.CreateTable(
                name: "FormasPagamento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NomeNormalizado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormasPagamento", x => x.Id);
                    table.CheckConstraint("CK_FormasPagamento_Nome", "char_length(\"Nome\") BETWEEN 1 AND 100 AND char_length(\"NomeNormalizado\") BETWEEN 1 AND 100 AND \"Nome\" = btrim(\"Nome\") AND \"NomeNormalizado\" = btrim(\"NomeNormalizado\") AND \"Nome\" !~ '[[:cntrl:]]' AND \"NomeNormalizado\" !~ '[[:cntrl:]]' AND \"Nome\" !~ '  ' AND \"NomeNormalizado\" !~ '  ' AND \"NomeNormalizado\" = normalize(\"NomeNormalizado\", NFC) AND \"Nome\" = normalize(\"Nome\", NFC)");
                });

            migrationBuilder.CreateTable(
                name: "FormasPagamentoVinculos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormaPagamentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModalId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinhaId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormasPagamentoVinculos", x => x.Id);
                    table.CheckConstraint("CK_FormasPagamentoVinculos_Escopo", "(\"ModalId\" IS NULL) <> (\"LinhaId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_FormasPagamentoVinculos_FormasPagamento_FormaPagamentoId",
                        column: x => x.FormaPagamentoId,
                        principalTable: "FormasPagamento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FormasPagamentoVinculos_Linhas_LinhaId",
                        column: x => x.LinhaId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FormasPagamentoVinculos_Modais_ModalId",
                        column: x => x.ModalId,
                        principalTable: "Modais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tarifas_LinhaId",
                table: "Tarifas",
                column: "LinhaId",
                unique: true,
                filter: "\"LinhaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tarifas_ModalId",
                table: "Tarifas",
                column: "ModalId",
                unique: true,
                filter: "\"ModalId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tarifas_Escopo",
                table: "Tarifas",
                sql: "(\"ModalId\" IS NULL) <> (\"LinhaId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tarifas_Fonte",
                table: "Tarifas",
                sql: "\"Fonte\" IN ('MANUAL','ARCGIS_SPPO')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tarifas_Valor",
                table: "Tarifas",
                sql: "\"Valor\" >= 0 AND \"Valor\" <= 99999999.99");

            migrationBuilder.CreateIndex(
                name: "IX_FormasPagamento_NomeNormalizado",
                table: "FormasPagamento",
                column: "NomeNormalizado",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormasPagamentoVinculos_FormaPagamentoId",
                table: "FormasPagamentoVinculos",
                column: "FormaPagamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_FormasPagamentoVinculos_LinhaId_FormaPagamentoId",
                table: "FormasPagamentoVinculos",
                columns: new[] { "LinhaId", "FormaPagamentoId" },
                unique: true,
                filter: "\"LinhaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormasPagamentoVinculos_ModalId_FormaPagamentoId",
                table: "FormasPagamentoVinculos",
                columns: new[] { "ModalId", "FormaPagamentoId" },
                unique: true,
                filter: "\"ModalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down restaura somente a estrutura antiga; não recupera os dados apagados no Up.
            // Descarta também valores e pagamentos novos. Recuperação produtiva exige backup.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '15s';
                LOCK TABLE "Tarifas" IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint
                               WHERE contype = 'f' AND confrelid = '"Tarifas"'::regclass) THEN
                        RAISE EXCEPTION 'Tarifas possui foreign keys de entrada não previstas; revisar dependências';
                    END IF;
                END $$;
                DELETE FROM "Tarifas";
                """);

            migrationBuilder.DropTable(
                name: "FormasPagamentoVinculos");

            migrationBuilder.DropTable(
                name: "FormasPagamento");

            migrationBuilder.DropIndex(
                name: "IX_Tarifas_LinhaId",
                table: "Tarifas");

            migrationBuilder.DropIndex(
                name: "IX_Tarifas_ModalId",
                table: "Tarifas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tarifas_Escopo",
                table: "Tarifas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tarifas_Fonte",
                table: "Tarifas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tarifas_Valor",
                table: "Tarifas");

            migrationBuilder.RenameColumn(
                name: "Valor",
                table: "Tarifas",
                newName: "Tarifa");

            migrationBuilder.RenameColumn(
                name: "CriadoEmUtc",
                table: "Tarifas",
                newName: "ValidoDe");

            migrationBuilder.RenameColumn(
                name: "AtualizadoEmUtc",
                table: "Tarifas",
                newName: "CreatedAt");

            migrationBuilder.AlterColumn<Guid>(
                name: "ModalId",
                table: "Tarifas",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LinhaId",
                table: "Tarifas",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Fonte",
                table: "Tarifas",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<decimal>(
                name: "Tarifa",
                table: "Tarifas",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)",
                oldPrecision: 10,
                oldScale: 2);

            migrationBuilder.AddColumn<bool>(
                name: "Ativo",
                table: "Tarifas",
                type: "boolean",
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Tarifas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidoAte",
                table: "Tarifas",
                type: "timestamp with time zone",
                nullable: true);

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
    }
}
