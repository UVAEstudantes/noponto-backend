"""Fixture controlada, não representa volume, distribuição ou sampling produtivos."""
import argparse
import csv
import json
import random
from datetime import datetime, timedelta, timezone
from pathlib import Path
from pipeline import CUTOFF, FEATURES, HEADER, VERSION, sha, write_json


def generate(output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    rng = random.Random(20261007)
    rows, trips = [], []
    for day in range(5):
        for trip in range(48):
            start = datetime(2026, 10, 8+day, 3+trip//3, (trip%3)*20, tzinfo=timezone.utc)
            end = start + timedelta(minutes=15)
            trip_id = f"synthetic-trip-{day}-{trip}"
            trips.append({"viagem_id": trip_id, "inicio": start.isoformat(), "fim": end.isoformat()})
            line = str(trip % 4)
            for observation in range(8):
                gps = start + timedelta(seconds=30*observation)
                distance = rng.uniform(80, 1800)
                speed = 16 + int(line)*3 + rng.uniform(-3, 3)
                delay = 20 if gps.hour in (10, 11) else 5
                label = distance / (speed / 3.6) + delay + rng.uniform(-3, 3)
                passage = gps + timedelta(seconds=label)
                local = gps - timedelta(hours=3)
                row = dict.fromkeys(HEADER, "")
                row.update(observacao_id=f"{trip_id}-{observation}", viagem_id=trip_id, volta=0,
                           veiculo=f"fixture-{trip%10}", modal="BRT" if trip%2 else "ONIBUS", provedor="SYNTHETIC",
                           timestamp_gps=gps.isoformat(), linha_id=line, codigo_linha=line, sentido_id=f"s-{line}",
                           padrao_id=f"p-{line}", versao_id=f"v-{line}", ocorrencia_id=f"o-{line}", parada_id=f"stop-{line}",
                           topologia="LINEAR", posicao_gps=.1, posicao_destino=.6, distancia_metros=distance,
                           velocidade_kmh=speed if observation%4 else "", velocidade_media_causal_kmh=speed if observation%5 else "",
                           hora_dia=local.hour, dia_semana=(local.weekday()+1)%7,
                           split="TRAIN" if day<3 else "VALIDATION" if day==3 else "TEST",
                           timestamp_passagem=passage.isoformat(), label_segundos=(passage-gps).total_seconds())
                rows.append(row)
    path = output / "dataset.csv"
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, HEADER)
        writer.writeheader()
        writer.writerows(rows)
    write_json(output / "dataset.manifest.json", {"dataset_version": VERSION, "data_kind": "synthetic",
               "cutoff_utc": CUTOFF, "features": FEATURES, "trips": trips, "dataset_sha256": sha(path),
               "fixture_seed": 20261007, "warning": "Não é dado real nem simulação do sampling produtivo"})


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--output", required=True)
    generate(p.parse_args().output)
