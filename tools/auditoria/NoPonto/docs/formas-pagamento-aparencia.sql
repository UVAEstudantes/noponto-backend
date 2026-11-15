START TRANSACTION;
ALTER TABLE "FormasPagamento" ADD "Cor" character varying(7);

ALTER TABLE "FormasPagamento" ADD "Icone" character varying(20);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261023014903_FormaPagamentoAparencia', '9.0.10');

COMMIT;
