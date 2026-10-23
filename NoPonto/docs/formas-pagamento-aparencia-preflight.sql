-- Somente leitura. Executar com psql -X -v ON_ERROR_STOP=1 no banco conferido pelo operador.
SELECT current_database() AS banco, current_user AS operador;
DO $preflight$
DECLARE
    colunas text[];
BEGIN
    IF to_regclass('public."FormasPagamento"') IS NULL
       OR to_regclass('public."__EFMigrationsHistory"') IS NULL THEN
        RAISE EXCEPTION 'Schema public anterior ausente; não aplicar migrations históricas automaticamente.';
    END IF;
    IF (SELECT max("MigrationId") FROM public."__EFMigrationsHistory")
        IS DISTINCT FROM '20261010002416_TarifasPagamentosMvp' THEN
        RAISE EXCEPTION 'Histórico fora da baseline esperada (ou aparência já aplicada). Parar e revisar.';
    END IF;
    SELECT array_agg(column_name::text ORDER BY column_name) INTO colunas
    FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'FormasPagamento';
    IF colunas IS DISTINCT FROM ARRAY['CriadoEmUtc', 'Id', 'Nome', 'NomeNormalizado']::text[] THEN
        RAISE EXCEPTION 'Colunas divergentes ou migration parcialmente aplicada. Parar e revisar.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'FormasPagamento' AND column_name = 'Id' AND data_type = 'uuid')
       OR (SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'FormasPagamento' AND column_name IN ('Nome', 'NomeNormalizado')
        AND data_type = 'character varying' AND character_maximum_length = 100 AND is_nullable = 'NO') <> 2 THEN
        RAISE EXCEPTION 'Tipos do catálogo divergentes da baseline; parar e revisar.';
    END IF;
END
$preflight$;
SELECT column_name, data_type, character_maximum_length, is_nullable
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'FormasPagamento' ORDER BY ordinal_position;
SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;
