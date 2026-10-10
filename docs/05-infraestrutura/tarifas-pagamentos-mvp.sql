START TRANSACTION;
SET LOCAL lock_timeout = '15s';
LOCK TABLE "Tarifas" IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint
               WHERE contype = 'f' AND confrelid = '"Tarifas"'::regclass) THEN
        RAISE EXCEPTION 'Tarifas possui foreign keys de entrada não previstas; revisar dependências';
    END IF;
END $$;
DELETE FROM "Tarifas";

DROP INDEX "IX_Tarifas_LinhaId";

DROP INDEX "IX_Tarifas_LinhaId_ValidoDe";

DROP INDEX "IX_Tarifas_ModalId";

ALTER TABLE "Tarifas" DROP COLUMN "Ativo";

ALTER TABLE "Tarifas" DROP COLUMN "UpdatedAt";

ALTER TABLE "Tarifas" DROP COLUMN "ValidoAte";

ALTER TABLE "Tarifas" RENAME COLUMN "Tarifa" TO "Valor";

ALTER TABLE "Tarifas" RENAME COLUMN "ValidoDe" TO "CriadoEmUtc";

ALTER TABLE "Tarifas" RENAME COLUMN "CreatedAt" TO "AtualizadoEmUtc";

ALTER TABLE "Tarifas" ALTER COLUMN "ModalId" DROP NOT NULL;

ALTER TABLE "Tarifas" ALTER COLUMN "LinhaId" DROP NOT NULL;

ALTER TABLE "Tarifas" ALTER COLUMN "Fonte" TYPE character varying(20);

ALTER TABLE "Tarifas" ALTER COLUMN "Valor" TYPE numeric(10,2);

CREATE TABLE "FormasPagamento" (
    "Id" uuid NOT NULL,
    "Nome" character varying(100) NOT NULL,
    "NomeNormalizado" character varying(100) NOT NULL,
    "CriadoEmUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_FormasPagamento" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_FormasPagamento_Nome" CHECK (char_length("Nome") BETWEEN 1 AND 100 AND char_length("NomeNormalizado") BETWEEN 1 AND 100 AND "Nome" = btrim("Nome") AND "NomeNormalizado" = btrim("NomeNormalizado") AND "Nome" !~ '[[:cntrl:]]' AND "NomeNormalizado" !~ '[[:cntrl:]]' AND "Nome" !~ '  ' AND "NomeNormalizado" !~ '  ' AND "NomeNormalizado" = normalize("NomeNormalizado", NFC) AND "Nome" = normalize("Nome", NFC))
);

CREATE TABLE "FormasPagamentoVinculos" (
    "Id" uuid NOT NULL,
    "FormaPagamentoId" uuid NOT NULL,
    "ModalId" uuid,
    "LinhaId" uuid,
    CONSTRAINT "PK_FormasPagamentoVinculos" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_FormasPagamentoVinculos_Escopo" CHECK (("ModalId" IS NULL) <> ("LinhaId" IS NULL)),
    CONSTRAINT "FK_FormasPagamentoVinculos_FormasPagamento_FormaPagamentoId" FOREIGN KEY ("FormaPagamentoId") REFERENCES "FormasPagamento" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_FormasPagamentoVinculos_Linhas_LinhaId" FOREIGN KEY ("LinhaId") REFERENCES "Linhas" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_FormasPagamentoVinculos_Modais_ModalId" FOREIGN KEY ("ModalId") REFERENCES "Modais" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_Tarifas_LinhaId" ON "Tarifas" ("LinhaId") WHERE "LinhaId" IS NOT NULL;

CREATE UNIQUE INDEX "IX_Tarifas_ModalId" ON "Tarifas" ("ModalId") WHERE "ModalId" IS NOT NULL;

ALTER TABLE "Tarifas" ADD CONSTRAINT "CK_Tarifas_Escopo" CHECK (("ModalId" IS NULL) <> ("LinhaId" IS NULL));

ALTER TABLE "Tarifas" ADD CONSTRAINT "CK_Tarifas_Fonte" CHECK ("Fonte" IN ('MANUAL','ARCGIS_SPPO'));

ALTER TABLE "Tarifas" ADD CONSTRAINT "CK_Tarifas_Valor" CHECK ("Valor" >= 0 AND "Valor" <= 99999999.99);

CREATE UNIQUE INDEX "IX_FormasPagamento_NomeNormalizado" ON "FormasPagamento" ("NomeNormalizado");

CREATE INDEX "IX_FormasPagamentoVinculos_FormaPagamentoId" ON "FormasPagamentoVinculos" ("FormaPagamentoId");

CREATE UNIQUE INDEX "IX_FormasPagamentoVinculos_LinhaId_FormaPagamentoId" ON "FormasPagamentoVinculos" ("LinhaId", "FormaPagamentoId") WHERE "LinhaId" IS NOT NULL;

CREATE UNIQUE INDEX "IX_FormasPagamentoVinculos_ModalId_FormaPagamentoId" ON "FormasPagamentoVinculos" ("ModalId", "FormaPagamentoId") WHERE "ModalId" IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261010002416_TarifasPagamentosMvp', '9.0.10');

COMMIT;

