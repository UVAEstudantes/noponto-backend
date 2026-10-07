-- Slice de schema EXCLUSIVO do container efêmero integration_postgis.py.
-- Tipos, chaves estruturais e unicidade relevantes ao SQL 3A; não é migration da API.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE "Linhas" ("Id" uuid PRIMARY KEY, "Codigo" text NOT NULL);
CREATE TABLE "Sentidos" ("Id" uuid PRIMARY KEY, "LinhaId" uuid NOT NULL REFERENCES "Linhas");
CREATE TABLE "PadroesOperacionais" ("Id" uuid PRIMARY KEY, "SentidoId" uuid NOT NULL REFERENCES "Sentidos");
CREATE TABLE "PadroesVersoes" ("Id" uuid PRIMARY KEY, "PadraoOperacionalId" uuid NOT NULL REFERENCES "PadroesOperacionais",
  "Topologia" text NOT NULL CHECK ("Topologia" IN ('LINEAR','CIRCULAR')), "Geometria" geometry(LineString,4326) NOT NULL);
CREATE TABLE "Paradas" ("Id" uuid PRIMARY KEY);
CREATE TABLE "OcorrenciasParadasPadroes" ("Id" uuid PRIMARY KEY,
  "PadraoVersaoId" uuid NOT NULL REFERENCES "PadroesVersoes", "ParadaId" uuid NOT NULL REFERENCES "Paradas",
  "PosicaoTracado" double precision NOT NULL, "Ordem" integer NOT NULL,
  UNIQUE ("PadraoVersaoId", "Ordem"));
CREATE TABLE "EventosViagem" ("EventId" text PRIMARY KEY, "Tipo" text NOT NULL,
  "Payload" text NOT NULL, "TimestampEvento" timestamptz NOT NULL);
CREATE TABLE "TelemetriasVeiculoMl" ("Id" uuid PRIMARY KEY, "ObservacaoId" varchar(64) NOT NULL UNIQUE,
  "Modal" varchar(20) NOT NULL, "Provedor" varchar(40) NOT NULL, "OrdemVeiculo" varchar(80) NOT NULL, "CodigoLinha" varchar(40) NOT NULL,
  "OrigemPosicao" varchar(20) NOT NULL, "LatitudeRecebida" double precision NOT NULL, "LongitudeRecebida" double precision NOT NULL,
  "VelocidadeInstantanea" double precision NOT NULL, "TimestampGps" timestamptz NOT NULL,
  "ViagemId" uuid, "Volta" integer, "LinhaId" uuid, "SentidoId" uuid, "PadraoVersaoId" uuid,
  "OcorrenciaParadaPadraoId" uuid, "ProximaOcorrenciaParadaPadraoId" uuid, "PosicaoNaRota" double precision,
  "ComprimentoRotaMetros" double precision, "DistanciaProximaParadaMetros" double precision,
  "VelocidadeMediaCausal" double precision);
CREATE INDEX ON "TelemetriasVeiculoMl" ("CodigoLinha", "TimestampGps");
CREATE TABLE "HistoricoPassagens" ("Id" uuid PRIMARY KEY, "Ordem" text NOT NULL,
  "CodigoLinha" text NOT NULL, "ViagemId" uuid, "SentidoId" uuid, "PadraoVersaoId" uuid,
  "OcorrenciaParadaPadraoId" uuid, "ParadaId" uuid NOT NULL REFERENCES "Paradas", "Volta" integer,
  "TimestampPassagem" timestamptz, "TimestampGps" timestamptz NOT NULL, "PosicaoNaRota" double precision NOT NULL,
  UNIQUE ("ViagemId", "OcorrenciaParadaPadraoId", "Volta"));
CREATE ROLE eta_fixture_reader LOGIN;
GRANT CONNECT ON DATABASE eta_fixture TO eta_fixture_reader;
GRANT USAGE ON SCHEMA public TO eta_fixture_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO eta_fixture_reader;
