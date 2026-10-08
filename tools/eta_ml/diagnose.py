"""Exploration of a verified local snapshot; never connects or publishes a dataset."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
from uuid import UUID
if __package__:
    from . import snapshot as s
else:
    import snapshot as s

ROOT = Path(__file__).resolve().parents[2]
SQL = ROOT / 'NoPonto/ETA_ML_CANDIDATOS_3A.sql'
VALIDATOR = ROOT / 'NoPonto/2-Application/Services/GPS/EtaDataset.cs'
# Pin normalized sources: fail closed on contract evolution, never silently drift.
def source_hash(path):
    return hashlib.sha256(path.read_text(encoding='utf-8-sig').replace('\r\n', '\n').encode()).hexdigest()


def copy_query(text, parameters):
    """One physical psql line; tokenize comments/quotes before substituting.

    Support the pinned 3A SQL, ordinary quoted strings/identifiers and nested
    comments. Fail closed on constructs whose newline/escape semantics need
    a full PostgreSQL lexer, rather than silently changing their meaning.
    """
    result, i, previous_string, newline_gap = [], 0, False, False
    while i < len(text):
        c = text[i]
        if c.isspace():
            newline_gap |= c in '\r\n'
            result.append(' '); i += 1
        elif text.startswith('--', i):
            end = text.find('\n', i)
            i = len(text) if end < 0 else end
            result.append(' ')
        elif text.startswith('/*', i):
            i += 2; depth = 1
            while i < len(text) and depth:
                if text.startswith('/*', i): depth += 1; i += 2
                elif text.startswith('*/', i): depth -= 1; i += 2
                else: newline_gap |= text[i] in '\r\n'; i += 1
            if depth: raise ValueError('Unterminated SQL comment')
            result.append(' ')
        elif c in "'\"":
            if c == "'" and previous_string and newline_gap:
                raise ValueError('Newline-concatenated SQL strings require explicit review')
            if c == "'" and i and (text[i-1] in 'eEbBxX' or text[max(0,i-2):i].upper() == 'U&'):
                raise ValueError('Prefixed SQL literals require explicit review')
            start = i; i += 1
            while i < len(text):
                if text[i] in '\r\n\\':
                    raise ValueError('Multiline/escaped SQL quotes require explicit review')
                if text[i] == c:
                    i += 1
                    if i < len(text) and text[i] == c: i += 1; continue
                    break
                i += 1
            else: raise ValueError('Unterminated SQL quote')
            result.append(text[start:i]); previous_string = c == "'"; newline_gap = False
        elif c == '$':
            raise ValueError('Dollar-quoted SQL requires explicit review')
        elif c == '@':
            match = re.match(r'@([a-z_]+)\b', text[i:])
            if not match or match[1] not in parameters:
                raise ValueError('Unknown SQL parameter')
            value = parameters[match[1]]
            if '\n' in value or '\r' in value:
                raise ValueError('Multiline parameter cannot be used in psql copy')
            result.append(value); i += len(match[0]); previous_string = False; newline_gap = False
        else:
            result.append(c); i += 1; previous_string = False; newline_gap = False
    # Do not split/join the completed text: whitespace INSIDE literals is data.
    return ''.join(result).strip().removesuffix(';').rstrip()


def valid_id(value):
    try:
        return UUID(value).int != 0
    except (ValueError, TypeError, AttributeError):
        return False


def finite(value):
    return isinstance(value, (int, float)) and math.isfinite(value)


def fraction(value):
    return finite(value) and 0 <= value <= 1


def identity(g):
    return (all(valid_id(g.get(k)) for k in ('ViagemId', 'SentidoId', 'LinhaId', 'PadraoVersaoId',
                                           'ProximaOcorrenciaParadaPadraoId'))
            and isinstance(g.get('Volta'), int) and g['Volta'] >= 0
            and g.get('OrigemPosicao') == 'REAL' and g.get('Modal') in ('ONIBUS', 'BRT'))


def journal_ok(h, j, d):
    if not h or not j or not d:
        return False
    pairs = {'viagem_id': 'ViagemId', 'ordem_veiculo': 'Ordem', 'codigo_linha': 'CodigoLinha',
             'sentido_id': 'SentidoId', 'padrao_versao_id': 'PadraoVersaoId',
             'ocorrencia_parada_padrao_id': 'OcorrenciaParadaPadraoId', 'parada_id': 'ParadaId',
             'volta': 'Volta', 'posicao_linha': 'PosicaoNaRota'}
    return (j.get('tipo') == 'PassagemParada' and j.get('schema_version') == 2
            and j.get('event_id') == f'passagem:{h["ViagemId"]}:{h["OcorrenciaParadaPadraoId"]}:{h["Volta"]}'
            and all(j.get(a) == h.get(b) for a, b in pairs.items())
            and j.get('linha_id') == d['linha_id'] and j.get('padrao_operacional_id') == d['padrao_id']
            and all(j.get(a) and h.get(b) and s.same_pg_time(j[a], h[b])
                    for a, b in (('timestamp_passagem', 'TimestampPassagem'), ('timestamp_gps', 'TimestampGps'))))


def reason(g, h, j, d, trip, distance, geometry_checked):
    """Component diagnosis of 3A technical rules; no quality or split certification."""
    if h is None:
        return 'LabelAusente'
    if d is None:
        return 'EstruturaAusente'
    if not journal_ok(h, j, d):
        return 'PassagemSemJournalConferido'
    if (not identity(g) or not all(valid_id(h.get(k)) for k in
            ('ViagemId', 'SentidoId', 'PadraoVersaoId', 'OcorrenciaParadaPadraoId'))
            or not all(valid_id(d.get(k)) for k in ('ocorrencia_id', 'parada_id', 'padrao_id'))
            or not h.get('TimestampPassagem') or h.get('Volta') is None
            or s.utc(g['TimestampGps']).timestamp() <= 0
            or any(not str(g.get(k) or '').strip() for k in
                   ('ObservacaoId', 'OrdemVeiculo', 'CodigoLinha', 'Provedor'))):
        return 'IdentidadeIncompletaOuOrigemModal'
    if g['ViagemId'] != trip['viagem_id'] or h['ViagemId'] != trip['viagem_id'] or g['OrdemVeiculo'] != h['Ordem']:
        return 'ExecucaoDiferente'
    if g['Volta'] != h['Volta']:
        return 'VoltaDiferente'
    comparisons = [('SentidoId', 'sentido_id'), ('LinhaId', 'linha_id'), ('CodigoLinha', 'codigo_linha'),
                   ('PadraoVersaoId', 'versao_id'), ('ProximaOcorrenciaParadaPadraoId', 'ocorrencia_id'),
                   ('OcorrenciaParadaPadraoId', 'ocorrencia_id')]
    if (any(g.get(a) != d.get(b) for a, b in comparisons)
            or any(g.get(k) != h.get(k) for k in ('SentidoId', 'CodigoLinha', 'PadraoVersaoId'))
            or h['OcorrenciaParadaPadraoId'] != d['ocorrencia_id'] or h['ParadaId'] != d['parada_id']):
        return 'IdentidadeEstruturalIncompativel'
    if d['topologia'] not in ('LINEAR', 'CIRCULAR'):
        return 'TopologiaInvalida'
    gps, passage = s.utc(g['TimestampGps']), s.utc(h['TimestampPassagem'])
    if not s.utc(trip['inicio']) <= gps <= s.utc(trip['fim']) or passage > s.utc(trip['fim']):
        return 'FronteirasExecucaoInvalidas'
    label = (passage - gps).total_seconds()
    if label <= 0 or label > 3600 or not h.get('TimestampGps') or passage > s.utc(h['TimestampGps']):
        return 'TempoInvalido'
    length = g.get('ComprimentoRotaMetros')
    if (not all(fraction(x) for x in (g.get('PosicaoNaRota'), h.get('PosicaoNaRota'), d.get('posicao_destino')))
            or abs(h['PosicaoNaRota'] - d['posicao_destino']) > 1e-8
            or d['posicao_destino'] <= g['PosicaoNaRota'] or not finite(length) or length <= 0):
        return 'DestinoNaoAdianteOuDistanciaInvalida'
    if not geometry_checked:
        return 'PendenteGeographySQL3A'
    if not finite(distance) or distance <= 0 or distance > length + 10:
        return 'DistanciaNaoConferida'
    # Direct distance belongs to matching diagnostics, not route validity.
    return 'TecnicamenteElegivelPreliminar'


def diagnose(bundle, output, candidates=None, database='eta_snapshot_3b2b_real02'):
    s.local_args(database, 1)  # Reuse dedicated-local-DB identifier guard; no connection.
    bundle, output = Path(bundle).resolve(), Path(output).resolve()
    if output == bundle or bundle in output.parents or output.exists():
        raise ValueError('Use a NEW report directory OUTSIDE the sealed bundle')
    policy = json.loads(Path(__file__).with_name('diagnose.sources.json').read_text())
    hashes = {'sql_3a': source_hash(SQL), 'validator_3a': source_hash(VALIDATOR)}
    if hashes != policy:
        raise ValueError('3A source changed: review diagnostic before continuing')
    manifest = s.verify(bundle)
    if manifest['snapshot_version'] != s.VERSION:
        raise ValueError('Requires sealed v2')
    gps = list(s.rows(bundle / 'TelemetriasVeiculoMl.csv'))
    passages = defaultdict(list)
    for h in s.rows(bundle / 'HistoricoPassagens.csv'):
        passages[h['ViagemId'], h['OcorrenciaParadaPadraoId'], h['Volta']].append(h)
    journals = {e['EventId']: e['Payload'] for e in s.rows(bundle / 'EventosViagem.csv')}
    occurrence = {r['Id']: r for r in s.rows(bundle / 'OcorrenciasParadasPadroes.csv')}
    versions = {r['Id']: r for r in s.rows(bundle / 'PadroesVersoes.csv')}
    patterns = {r['Id']: r for r in s.rows(bundle / 'PadroesOperacionais.csv')}
    directions = {r['Id']: r for r in s.rows(bundle / 'Sentidos.csv')}
    lines = {r['Id']: r for r in s.rows(bundle / 'Linhas.csv')}
    checked = None
    if candidates:
        checked = defaultdict(list)
        for row in s.rows(Path(candidates)):
            checked[row['gps']['Id']].append(row)
    totals, trips, distribution = Counter(), [], defaultdict(Counter)
    sql_text = SQL.read_text(encoding='utf-8-sig')
    queries = []
    for trip in manifest['trips']:
        counts = Counter({key: 0 for key in ('LabelAusente', 'EstruturaAusente', 'PassagemSemJournalConferido',
            'IdentidadeIncompletaOuOrigemModal', 'ExecucaoDiferente', 'VoltaDiferente',
            'IdentidadeEstruturalIncompativel', 'TopologiaInvalida', 'FronteirasExecucaoInvalidas',
            'TempoInvalido', 'DestinoNaoAdianteOuDistanciaInvalida', 'DistanciaNaoConferida',
            'DistanciasDivergentes', 'AssociacaoAmbigua', 'PendenteGeographySQL3A', 'TecnicamenteElegivelPreliminar')})
        selected = [g for g in gps if g.get('ViagemId') == trip['viagem_id']]
        # SQL3A paginates GPS BEFORE joins. Use the complete local window/line
        # from the sealed bundle to compute deterministic cursor pages.
        window = sorted([g for g in gps if g['CodigoLinha'] == trip['codigo_linha']
                         and s.utc(trip['inicio']) <= s.utc(g['TimestampGps']) < s.utc(trip['fim'])],
                        key=lambda g: (s.utc(g['TimestampGps']), g['Id']))
        cursor_ts, cursor_id = trip['inicio'], str(UUID(int=0))
        for start in range(0, max(len(window), 1), 1000):
            values = {'codigo': s.literal(trip['codigo_linha']), 'inicio': s.literal(trip['inicio'])+'::timestamptz',
                      'fim': s.literal(trip['fim'])+'::timestamptz', 'cursor_ts': s.literal(cursor_ts)+'::timestamptz',
                      'cursor_id': s.literal(cursor_id)+'::uuid', 'tamanho': '1000'}
            query = copy_query(sql_text, values)
            queries.append(f"SELECT to_jsonb(q) AS row FROM ({query}) q WHERE q.gps->>'ViagemId'={s.literal(trip['viagem_id'])}")
            if window:
                last = window[min(start+999, len(window)-1)]
                cursor_ts, cursor_id = last['TimestampGps'], last['Id']
        for g in selected:
            counts['gps'] += 1
            counts['identidade_operacional_valida'] += identity(g)
            counts['identidade_operacional_invalida'] += not identity(g)
            hs = passages[g['ViagemId'], g.get('ProximaOcorrenciaParadaPadraoId'), g.get('Volta')]
            counts['gps_associado_passagem'] += bool(hs)
            counts['associacao_ambigua'] += len(hs) > 1
            o = occurrence.get(g.get('ProximaOcorrenciaParadaPadraoId'))
            v = versions.get(o['PadraoVersaoId']) if o else None
            p = patterns.get(v['PadraoOperacionalId']) if v else None
            direction = directions.get(p['SentidoId']) if p else None
            line = lines.get(direction['LinhaId']) if direction else None
            d = (dict(ocorrencia_id=o['Id'], parada_id=o['ParadaId'], posicao_destino=o['PosicaoTracado'],
                      versao_id=v['Id'], padrao_id=p['Id'], topologia=v['Topologia'], sentido_id=direction['Id'],
                      linha_id=line['Id'], codigo_linha=line['Codigo']) if line else None)
            h = hs[0] if len(hs) == 1 else None
            j = journals.get(f'passagem:{h["ViagemId"]}:{h["OcorrenciaParadaPadraoId"]}:{h["Volta"]}') if h else None
            counts['journal_conferido'] += journal_ok(h, j, d)
            distance = None
            if checked is not None:
                records = checked.pop(g['Id'], [])
                if not (s.utc(trip['inicio']) <= s.utc(g['TimestampGps']) < s.utc(trip['fim'])):
                    if records:
                        raise ValueError('Unexpected outside-window SQL row')
                elif len(records) != max(1, len(hs)) or any(r['gps'] != g for r in records):
                    raise ValueError('SQL output differs from sealed GPS/incomplete pagination')
                if len(records) == 1:
                    r = records[0]
                    if r['passagem'] != h or r['journal'] != j or (d and any(r[k] != value for k, value in d.items())):
                        raise ValueError('SQL structure/passage/journal differs from sealed sources')
                    distance = r['distancia_rota_conferida_metros']
            result = ('AssociacaoAmbigua' if len(hs) > 1 else reason(g, h, j, d, trip, distance, checked is not None))
            counts[result] += 1
            band_value = distance if checked is not None else g.get('DistanciaProximaParadaMetros')
            band = 'invalida/ausente' if not finite(band_value) or band_value <= 0 else '<200m' if band_value < 200 else '200-999m' if band_value < 1000 else '>=1000m'
            for key in ('linha:'+g['CodigoLinha'], 'modal:'+str(g.get('Modal')), 'distancia:'+band):
                distribution[key]['gps'] += 1
                distribution[key][result] += 1
        trips.append(dict(viagem_id=trip['viagem_id'], codigo_linha=trip['codigo_linha'], counts=dict(counts),
                          qualidade='NaoVerificada', certificacao='nao_atribuida'))
        totals.update(counts)
    if checked:
        raise ValueError('Unselected/duplicate GPS in SQL output')
    output.mkdir(parents=True, exist_ok=False)
    sql = ("\\set ON_ERROR_STOP on\n\\encoding UTF8\nBEGIN READ ONLY;\nSET TRANSACTION ISOLATION LEVEL REPEATABLE READ;\n"
           "SET LOCAL search_path=public,pg_catalog;\nSET LOCAL statement_timeout='60s';\nSET LOCAL lock_timeout='2s';\n"
           "SET LOCAL timezone='UTC';\nSELECT 1/CASE WHEN current_database()=" + s.literal(database)
           + " THEN 1 ELSE 0 END AS local_database_guard;\n"
           "\\copy (" + ' UNION ALL '.join('('+q+')' for q in queries)
           + ") TO 'diagnostic-candidates.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')\nCOMMIT;\n")
    s.write(output / 'diagnostic.sql', sql)
    report = dict(report_version='eta-exploration-3g2-v2', validation_policy='eta-route-versioned-3g2-v2', training_dataset=False,
                  persisted_distance_semantics='direct_to_matching_stop_not_operational_route_length',
                  collection_profile='official-post-fix', cutoff=manifest['cutoff'], snapshot_end=manifest['snapshot_end'],
                  snapshot_manifest_sha256=s.sha(bundle/'snapshot.manifest.json'), sources=hashes,
                  script_sha256=s.sha(Path(__file__)), sql_sha256=s.sha(output/'diagnostic.sql'),
                  candidate_file_sha256=s.sha(Path(candidates)) if candidates else None,
                  database=database, geometry_checked=bool(candidates), total=dict(totals), trips=trips, distribution=dict(distribution),
                  limits=['Component diagnosis, not EtaDataset export/certification.',
                          'Quality remains NaoVerificada; no AuditadaSemProtecao granted.',
                          'No temporal train/validation/test split; no duplicate-observation export deduplication.',
                          'Technical eligibility is preliminary; horizon=3600s, first rejection per GPS.',
                          'Without SQL result, distance bands use reported distance and geography is pending.',
                          'Local restored content/isolation are attested by operator, not verified via connection.'])
    s.write(output/'report.json', s.canonical(report))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle'); parser.add_argument('output'); parser.add_argument('--candidates')
    parser.add_argument('--database', default='eta_snapshot_3b2b_real02')
    args = parser.parse_args()
    report = diagnose(args.bundle, args.output, args.candidates, args.database)
    print(s.canonical({'geometry_checked': report['geometry_checked'], 'total': report['total']}), end='')
