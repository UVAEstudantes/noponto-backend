"""Ensaio 3B.1: cria/remove SOMENTE seu container Docker efêmero, sem banco externo.

Compilar EtaMl.Export primeiro. Requer imagem local postgis/postgis:16-3.4.
Não é incluído no discover dos testes unitários; execução explícita.
"""
import argparse
import copy
import csv
import hashlib
import json
import os
import socket
import struct
import subprocess
import sys
import time
import uuid
from collections import Counter
from datetime import timedelta
from pathlib import Path

from pipeline import CUTOFF, FEATURES, HEADER, load, matrix, sha, utc, write_json

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
PROJECT = ROOT / "tools/EtaMl.Export/EtaMl.Export.csproj"
SQL = ROOT / "NoPonto/ETA_ML_CANDIDATOS_3A.sql"


def uid(n):
    return str(uuid.UUID(int=n))


def literal(value):
    if value is None:
        return "NULL"
    return "'" + str(value).replace("'", "''") + "'"


def insert(table, values):
    return f'INSERT INTO "{table}" (' + ",".join(f'"{k}"' for k in values) + ") VALUES (" + ",".join(literal(v) for v in values.values()) + ");"


def run(command, **kwargs):
    return subprocess.run(command, cwd=ROOT, encoding="utf-8", errors="replace", capture_output=True,
                          timeout=120, **kwargs)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


class LocalFixture:
    def __init__(self):
        self.name = "noponto-eta-3b1-" + uuid.uuid4().hex[:12]
        self.created = False

    def start(self):
        result = run(["docker", "run", "--pull", "never", "--rm", "-d", "--name", self.name,
                      "--label", "noponto.fixture=eta-3b1", "-e", "POSTGRES_HOST_AUTH_METHOD=trust",
                      "-e", "POSTGRES_DB=eta_fixture", "-p", "127.0.0.1::5432", "postgis/postgis:16-3.4"])
        require(result.returncode == 0, result.stderr)
        self.created = True
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            result = run(["docker", "exec", self.name, "pg_isready", "-U", "postgres", "-d", "eta_fixture"])
            if result.returncode == 0:
                break
            time.sleep(.5)
        require(result.returncode == 0, "PostgreSQL não ficou pronto em 60s")
        binding = run(["docker", "port", self.name, "5432/tcp"])
        require(binding.returncode == 0 and binding.stdout.startswith("127.0.0.1:"), "Binding não loopback")
        self.port = int(binding.stdout.strip().split(":")[-1])
        # pg_isready interno pode preceder o proxy TCP publicado do Docker Desktop.
        # PostgreSQL responde N/S ao SSLRequest; não basta o socket aceitar conexão.
        deadline = time.monotonic() + 30
        response = b""
        while time.monotonic() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=2) as probe:
                    probe.settimeout(2)
                    probe.sendall(struct.pack("!II", 8, 80877103))
                    response = probe.recv(1)
                if response in (b"N", b"S"):
                    break
            except OSError:
                pass
            time.sleep(.5)
        require(response in (b"N", b"S"), "Porta Docker não respondeu protocolo PostgreSQL em 30s")
        self.sql(HERE.joinpath("postgis_fixture.sql").read_text(encoding="utf-8"))
        print("Container descartável preparado:", self.name, "loopback port", self.port, flush=True)

    def sql(self, text, success=True):
        result = run(["docker", "exec", "-i", self.name, "psql", "-X", "-q", "-A", "-t",
                      "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "eta_fixture"], input=text)
        if success:
            require(result.returncode == 0, result.stderr)
        return result

    def close(self):
        if self.created:
            # Nome criado nesta instância, sem glob, volumes nomeados ou containers existentes.
            result = run(["docker", "rm", "-f", "-v", self.name])
            require(result.returncode == 0, "Limpeza do container próprio falhou: " + result.stderr)
            print("Container próprio removido:", self.name, flush=True)


def seed(db, output):
    options = {"Inicio": CUTOFF, "FimTreino": "2026-10-11T00:00:00Z",
               "FimValidacao": "2026-10-12T00:00:00Z", "Fim": "2026-10-13T00:00:00Z",
               "ExecucoesPorPagina": 1, "MaxCandidatosPorExecucao": 10000, "MaxPaginas": 1000,
               "MaxLabelSegundos": 3600}
    audit = json.loads((ROOT / "tools/EtaMl.Export/audit.example.json").read_text(encoding="utf-8-sig"))
    audit.update(SnapshotReference="fixture-docker-3b1-known-full-lifecycle", DataKind="synthetic", Trips=[])
    statements = []
    for n, geometry in ((0, "LINESTRING(-43.21 -22.9,-43.19 -22.9)"),
                        (1, "LINESTRING(-43.21 -22.9,-43.20 -22.9,-43.20 -22.89,-43.21 -22.89,-43.21 -22.9)")):
        line, sense, pattern, version, stop, occurrence = [uid(100+n*10+i) for i in range(6)]
        statements += [insert("Linhas", {"Id": line, "Codigo": f"FIXTURE-{n}"}),
                       insert("Sentidos", {"Id": sense, "LinhaId": line}),
                       insert("PadroesOperacionais", {"Id": pattern, "SentidoId": sense}),
                       f'INSERT INTO "PadroesVersoes" VALUES ({literal(version)}, {literal(pattern)}, {literal("CIRCULAR" if n else "LINEAR")}, ST_GeomFromText({literal(geometry)},4326));',
                       insert("Paradas", {"Id": stop}),
                       insert("OcorrenciasParadasPadroes", {"Id": occurrence, "PadraoVersaoId": version, "ParadaId": stop, "PosicaoTracado": .4, "Ordem": 1})]
    statements.append(insert("OcorrenciasParadasPadroes", {"Id": uid(106), "PadraoVersaoId": uid(103), "ParadaId": uid(104), "PosicaoTracado": .6, "Ordem": 2}))
    starts = ["2026-10-08T15:00:00.225082Z", "2026-10-08T15:00:00.225082Z", "2026-10-11T15:00:00.225082Z",
              "2026-10-12T15:00:00.225082Z", "2026-10-07T14:53:59.225081Z", "2026-10-12T23:55:00Z",
              "2026-10-10T23:55:00Z", "2026-10-11T23:55:00Z"]
    for index, start_text in enumerate(starts, 1):
        start = utc(start_text)
        end = utc(options["Fim"]) if index == 6 else start + timedelta(minutes=10)
        circular = index == 4
        base = 110 if circular else 100
        line, sense, pattern, version, stop, occurrence = [uid(base+i) for i in range(6)]
        trip = {"ViagemId": uid(index), "Inicio": start.isoformat(), "Fim": end.isoformat(),
                "CodigoLinha": f"FIXTURE-{int(circular)}", "Qualidade": "AuditadaSemProtecao",
                "ReferenciaAuditoria": f"fixture-known-execution-{index}-no-protection"}
        audit["Trips"].append(trip)
        gps = start + timedelta(seconds=60)
        passage = gps + timedelta(seconds=60)
        confirm = passage + timedelta(seconds=10)
        event = {"viagem_id": uid(index), "ordem_veiculo": f"fixture-{index}", "codigo_linha": trip["CodigoLinha"],
                 "sentido_id": sense, "padrao_versao_id": version, "padrao_operacional_id": pattern,
                 "linha_id": line, "schema_version": 2, "volta": 0}
        for kind, prefix, timestamp in (("ViagemIniciada", "inicio", start), ("ViagemFinalizada", "fim", end)):
            payload = dict(event, event_id=f"{prefix}:{uid(index)}", tipo=kind, timestamp_evento=timestamp.isoformat())
            statements.append(insert("EventosViagem", {"EventId": payload["event_id"], "Tipo": kind,
                               "Payload": json.dumps(payload), "TimestampEvento": timestamp.isoformat()}))
        payload = dict(event, event_id=f"passagem:{uid(index)}:{occurrence}:0", tipo="PassagemParada",
                       timestamp_evento=passage.isoformat(), timestamp_passagem=passage.isoformat(),
                       timestamp_gps=confirm.isoformat(), ocorrencia_parada_padrao_id=occurrence,
                       parada_id=stop, ordem=1, posicao_linha=.4)
        statements.append(insert("EventosViagem", {"EventId": payload["event_id"], "Tipo": "PassagemParada",
                           "Payload": json.dumps(payload), "TimestampEvento": passage.isoformat()}))
        statements.append(insert("HistoricoPassagens", {"Id": uid(1000+index), "Ordem": f"fixture-{index}",
                           "CodigoLinha": trip["CodigoLinha"], "ViagemId": uid(index), "SentidoId": sense,
                           "PadraoVersaoId": version, "OcorrenciaParadaPadraoId": occurrence, "ParadaId": stop,
                           "Volta": 0, "TimestampPassagem": passage.isoformat(), "TimestampGps": confirm.isoformat(), "PosicaoNaRota": .4}))
        count = 1001 if index == 1 else 1
        for observation in range(count + (2 if index == 1 else 1 if circular else 0)):
            timestamp = gps + timedelta(milliseconds=999 if index == 2 else observation)
            missing = index == 1 and observation == 1001
            incompatible = index == 1 and observation == 1002
            wrap = circular and observation == 1
            pos = .95 if wrap else .1
            target = uid(106) if missing else occurrence
            values = {"Id": uid(100000+index*10000+observation), "ObservacaoId": hashlib.sha256(f"fixture-{index}/{timestamp.isoformat()}/{observation}".encode()).hexdigest(),
                      "Modal": "BRT" if circular else "ONIBUS", "Provedor": "FIXTURE", "OrdemVeiculo": f"fixture-{index}",
                      "CodigoLinha": trip["CodigoLinha"], "OrigemPosicao": "REAL", "LatitudeRecebida": -22.9,
                      "LongitudeRecebida": -43.208, "VelocidadeInstantanea": 20, "VelocidadeMediaCausal": 18,
                      "TimestampGps": timestamp.isoformat(), "ViagemId": uid(index), "Volta": 0, "LinhaId": line,
                      "SentidoId": uid(111) if incompatible else sense, "PadraoVersaoId": version,
                      "OcorrenciaParadaPadraoId": target, "ProximaOcorrenciaParadaPadraoId": target,
                      "PosicaoNaRota": pos, "ComprimentoRotaMetros": 5000, "DistanciaProximaParadaMetros": 100}
            statement = insert("TelemetriasVeiculoMl", values)
            if not wrap:
                expression = f'(SELECT ST_Length(ST_LineSubstring("Geometria",0.1,{.6 if missing else .4})::geography) FROM "PadroesVersoes" WHERE "Id"={literal(version)})'
                # Substitui somente o último valor (distância), não texto de outros campos.
                statement = statement.rsplit(",", 1)[0] + "," + expression + ");"
            statements.append(statement)
    statements.append('''UPDATE "TelemetriasVeiculoMl" g SET "ComprimentoRotaMetros"=ST_Length(v."Geometria"::geography)
        FROM "PadroesVersoes" v WHERE v."Id"=g."PadraoVersaoId";''')
    text = "BEGIN;\n" + "\n".join(statements) + "\nCOMMIT;\n"
    (output / "seed.sql").write_text(text, encoding="utf-8")
    db.sql(text)
    write_json(output / "audit.json", audit)
    write_json(output / "options.json", options)
    config = {"inicio": options["Inicio"], "fim_treino": options["FimTreino"],
              "fim_validacao": options["FimValidacao"], "fim": options["Fim"], "seed": 20261007, "minimum_breakdown_rows": 30}
    write_json(output / "config.json", config)
    return audit, options, config


def execute_adapter(db, output, audit, name):
    path = output / (name + ".audit.json")
    write_json(path, audit)
    env = dict(os.environ, ETA_ML_LOCAL_CONNECTION=f"Host=127.0.0.1;Port={db.port};Database=eta_fixture;Username=eta_fixture_reader;Pooling=false;SSL Mode=Disable")
    result = run(["dotnet", "run", "--project", str(PROJECT), "--no-build", "--",
                  str(path), str(output / "options.json"), str(SQL), str(output / name)], env=env)
    # Credenciais inexistentes neste ensaio; guardar somente stdout/stderr do console.
    (output / (name + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
    return result, path


def validate(db, output, audit, options, config):
    result, audit_path = execute_adapter(db, output, audit, "export-a")
    require(result.returncode == 0, result.stderr)
    folder = output / "export-a"
    manifest = json.loads((folder / "dataset.manifest.json").read_text(encoding="utf-8-sig"))
    # PostgreSQL é real; o conteúdo controlado continua sintético.
    require(manifest["data_kind"] == "synthetic", "Fixture PostgreSQL indevidamente marcada como data_kind=real")
    with (folder / "dataset.csv").open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        require(reader.fieldnames == HEADER, "Cabeçalho divergente")
        rows = list(reader)
    require(len(rows) == 1004, f"1004 observações esperadas, produzido {len(rows)}")
    require(Counter(r["viagem_id"] for r in rows) == {uid(1): 1001, uid(2): 1, uid(3): 1, uid(4): 1}, "Viagens/páginas incompletas ou exclusões vazaram")
    require(Counter(r["split"] for r in rows) == {"TRAIN": 1002, "VALIDATION": 1, "TEST": 1}, "Splits divergentes")
    require(manifest["features"] == FEATURES, "Allowlist divergente")
    for field, path in (("dataset_sha256", folder / "dataset.csv"), ("audit_sha256", audit_path),
                        ("options_sha256", output / "options.json"), ("sql_sha256", SQL)):
        require(manifest[field] == sha(path), "Hash divergente: " + field)
    require(manifest["discovery"]["trips_outside_window"] == 2, "Cutoff/fim não purgados")
    require(manifest["discovery"]["trips_crossing_split"] == 2, "Cortes de split não purgados")
    require(manifest["discovery"]["gps_trip_candidates"] == 1007, "Descoberta/keyset incompleto")
    require(manifest["discovery"]["missing_label_journal_structure"] == 1, "Candidato sem passagem não contabilizado")
    require(manifest["counts"]["Exportados"] == 1004, "Contagem CSV/manifest divergente")
    require(manifest["counts"]["Descartes"] == {"IdentidadeEstruturalIncompativel": 1, "DestinoNaoAdianteOuDistanciaInvalida": 1}, "Descartes divergentes")
    parsed, _, discarded = load(folder / "dataset.csv", folder / "dataset.manifest.json", config)
    require(not discarded, "Loader descartou CSV já certificado pela fixture")
    features_before = matrix(parsed)
    for row in parsed:
        for key in ("label_segundos", "timestamp_passagem", "viagem_id", "observacao_id", "veiculo", "provedor"):
            row[key] = "poison"
    require((features_before == matrix(parsed)).all(), "Proveniência/label vazou para matriz")
    reference = json.loads(db.sql('''SELECT json_agg(x) FROM (
        SELECT g."Id", g."ViagemId", EXTRACT(EPOCH FROM h."TimestampPassagem"-g."TimestampGps") AS label,
        ST_Length(ST_LineSubstring(v."Geometria",g."PosicaoNaRota",o."PosicaoTracado")::geography) AS distance
        FROM "TelemetriasVeiculoMl" g JOIN "HistoricoPassagens" h ON h."ViagemId"=g."ViagemId" AND h."Volta"=g."Volta" AND h."OcorrenciaParadaPadraoId"=g."ProximaOcorrenciaParadaPadraoId"
        JOIN "PadroesVersoes" v ON v."Id"=g."PadraoVersaoId" JOIN "OcorrenciasParadasPadroes" o ON o."Id"=g."ProximaOcorrenciaParadaPadraoId"
        WHERE g."Id" IN ('00000000-0000-0000-0000-00000001adb0','00000000-0000-0000-0000-0000000222e0')
        ORDER BY g."Id") x;''').stdout)
    require(len(reference) == 2, "Amostra SQL direta incompleta")
    samples = []
    for ref in reference:
        matching = next(r for r in rows if r["viagem_id"] == ref["ViagemId"])
        require(abs(float(matching["distancia_metros"])-ref["distance"]) < 1e-7, "Distância diferente do SQL direto")
        require(abs(float(matching["label_segundos"])-ref["label"]) < 1e-7, "Label diferente do SQL direto")
        samples.append({"trip": ref["ViagemId"], "label_seconds": ref["label"], "distance_meters": ref["distance"], "split": matching["split"]})
    second, _ = execute_adapter(db, output, audit, "export-b")
    require(second.returncode == 0, second.stderr)
    require(sha(folder / "dataset.csv") == sha(output / "export-b/dataset.csv"), "CSV não determinístico")
    # Não reduzir artificialmente tamanho da página: as 1001 linhas da viagem1
    # só podem sobreviver se o adapter consumiu a segunda página do SQL original.
    negative = []
    for name, change, expected in (
        ("duplicate-trip", lambda a: a["Trips"].append(a["Trips"][0].copy()), "auditoria inválido"),
        ("unaudited", lambda a: a["Trips"][0].update(Qualidade="NaoVerificada"), "auditoria inválido"),
        ("protected", lambda a: a["Trips"][0].update(Qualidade="ProtegidaOuAmbigua"), "auditoria inválido"),
        ("no-reference", lambda a: a["Trips"][0].update(ReferenciaAuditoria=""), "auditoria inválido"),
        ("wrong-build", lambda a: a.update(BackendCommit="wrong"), "coleta oficial"),
        ("wrong-cutoff", lambda a: a.update(CollectionStartedAtUtc="2026-10-07T14:53:59Z"), "auditoria inválido"),
        ("wrong-boundary", lambda a: a["Trips"][0].update(Inicio="2026-10-08T15:00:01Z"), "fronteiras fechadas")):
        modified = copy.deepcopy(audit)
        change(modified)
        failed, _ = execute_adapter(db, output, modified, name)
        require(failed.returncode != 0 and expected in failed.stderr, "Negativo não rejeitado: " + name + "\n" + failed.stderr)
        require(not (output / name / "dataset.manifest.json").exists(), "Negativo publicou manifesto")
        negative.append(name)
    # Conferência real do journal: remover um fim impede publicar o dataset.
    end_id = "fim:" + uid(1)
    saved_end = json.loads(db.sql(f'SELECT to_jsonb(e) FROM "EventosViagem" e WHERE "EventId"={literal(end_id)};').stdout)
    db.sql(f'DELETE FROM "EventosViagem" WHERE "EventId"={literal(end_id)};')
    try:
        failed, _ = execute_adapter(db, output, audit, "missing-end")
        require(failed.returncode != 0 and "fronteiras fechadas" in failed.stderr, "Fim ausente não rejeitado")
        require(not (output / "missing-end/dataset.manifest.json").exists(), "Fim ausente publicou manifesto")
        require(not (output / "missing-end/dataset.csv.partial").exists(), "CSV parcial ficou publicado")
        negative.append("missing-end")
    finally:
        db.sql(insert("EventosViagem", saved_end))
    # Payload de passagem incompatível: candidato descartado, sem inventar label.
    passage_id = f"passagem:{uid(3)}:{uid(105)}:0"
    saved_passage = json.loads(db.sql(f'SELECT to_jsonb(e) FROM "EventosViagem" e WHERE "EventId"={literal(passage_id)};').stdout)
    db.sql(f'''UPDATE "EventosViagem" SET "Payload"=jsonb_set("Payload"::jsonb,'{{posicao_linha}}','0.5'::jsonb)::text
        WHERE "EventId"={literal(passage_id)};''')
    try:
        result, _ = execute_adapter(db, output, audit, "journal-mismatch")
        require(result.returncode == 0, result.stderr)
        bad_manifest = json.loads((output / "journal-mismatch/dataset.manifest.json").read_text(encoding="utf-8-sig"))
        require(bad_manifest["counts"]["Exportados"] == 1003, "Journal incompatível exportou candidato")
        require(bad_manifest["counts"]["Descartes"]["PassagemSemJournalConferido"] == 1, "Descarte do journal não contabilizado")
        negative.append("journal-mismatch-candidate-discarded")
    finally:
        db.sql(f'UPDATE "EventosViagem" SET "Payload"={literal(saved_passage["Payload"])} WHERE "EventId"={literal(passage_id)};')
    # Constraint real: duplicata de observação não pode se tornar feature/linha extra.
    duplicated = db.sql('''INSERT INTO "TelemetriasVeiculoMl"
        SELECT (jsonb_populate_record(NULL::"TelemetriasVeiculoMl",to_jsonb(t)||jsonb_build_object('Id','ffffffff-ffff-ffff-ffff-ffffffffffff'))).*
        FROM "TelemetriasVeiculoMl" t LIMIT 1;''', success=False)
    require(duplicated.returncode != 0 and "ObservacaoId_key" in duplicated.stderr, "Unicidade de ObservacaoId não preservada")
    volume = run([sys.executable, str(HERE / "check_volume.py"), "--dataset", str(folder / "dataset.csv"),
                  "--manifest", str(folder / "dataset.manifest.json"), "--config", str(output / "config.json"),
                  "--output", str(folder / "volume.json")])
    require(volume.returncode == 2, "Gate de volume deveria ser insuficiente: " + volume.stderr)
    report = json.loads((folder / "volume.json").read_text(encoding="utf-8-sig"))
    require(report["ready"] is False and report["data_kind"] == "synthetic", "Gate/fixture incorreto")
    return {"status": "APROVADO", "data_kind": "synthetic", "observations": len(rows), "exported_trips": 4,
            "audit_trips": len(audit["Trips"]), "splits": {"TRAIN": 1002, "VALIDATION": 1, "TEST": 1},
            "discovery": manifest["discovery"], "counts": manifest["counts"], "samples_direct_sql": samples,
            "dataset_sha256": manifest["dataset_sha256"], "csv_deterministic": True, "negative_cases": negative,
            "volume": report, "sql_sha256": sha(SQL)}


def main(output):
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = LocalFixture()
    try:
        fixture.start()
        version = fixture.sql("SELECT json_build_object('postgres',current_setting('server_version'),'postgis',postgis_lib_version());").stdout
        metadata = json.loads(version)
        require(metadata["postgres"].startswith("16.") and metadata["postgis"].startswith("3.4."), "Versões diferentes do pedido")
        audit, options, config = seed(fixture, output)
        print("Fixture inserida; executando console real e validação independente", flush=True)
        report = validate(fixture, output, audit, options, config)
        report.update(environment=metadata, container=fixture.name,
                      fixture_schema_sha256=sha(HERE / "postgis_fixture.sql"), fixture_seed_sha256=sha(output / "seed.sql"))
        write_json(output / "integration.report.json", report)
        print(json.dumps(report, ensure_ascii=True), flush=True)
    except Exception as error:
        if fixture.created:
            logs = run(["docker", "logs", fixture.name])
            (output / "container.log").write_text(logs.stdout + logs.stderr, encoding="utf-8")
        write_json(output / "integration.report.json", {"status": "REPROVADO", "error": str(error), "container": fixture.name})
        raise
    finally:
        fixture.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    main(parser.parse_args().output)
