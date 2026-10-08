"""Focused offline distance evidence; never changes tolerance/labels/snapshot."""
import argparse
import json
import math
from pathlib import Path
import struct
if __package__:
    from . import snapshot as s
else:
    import snapshot as s


def point(hex_wkb):
    data = bytes.fromhex(hex_wkb)
    endian = '<' if data[0] == 1 else '>'
    kind = struct.unpack_from(endian+'I', data, 1)[0]
    if kind & 0xFFFF != 1: raise ValueError('Expected EWKB point')
    offset = 9 if kind & 0x20000000 else 5
    return struct.unpack_from(endian+'dd', data, offset)


def direct_meters(lat, lon, dst_lat, dst_lon):
    lat, lon, dst_lat, dst_lon = map(math.radians, (lat, lon, dst_lat, dst_lon))
    a = math.sin((dst_lat-lat)/2)**2 + math.cos(lat)*math.cos(dst_lat)*math.sin((dst_lon-lon)/2)**2
    return 6371008.8*2*math.asin(math.sqrt(min(1, max(0, a))))


def sample(bundle, candidates, report, output):
    bundle, output = Path(bundle).resolve(), Path(output).resolve()
    if output.exists() or output == bundle or bundle in output.parents:
        raise ValueError('New output outside sealed bundle required')
    s.verify(bundle)
    metadata = json.loads(Path(report).read_text())
    if (metadata['snapshot_manifest_sha256'] != s.sha(bundle/'snapshot.manifest.json')
            or metadata['candidate_file_sha256'] != s.sha(candidates) or not metadata['geometry_checked']):
        raise ValueError('Evidence hashes mismatch')
    stops = {p['Id']: p for p in s.rows(bundle/'Paradas.csv')}
    rows = []
    for c in s.rows(candidates):
        g = c['gps']; route = c['distancia_rota_conferida_metros']; reported = g.get('DistanciaProximaParadaMetros')
        if route is None or reported is None or route <= 0 or reported <= 0:
            continue
        if abs(reported-route) <= max(10, route*0.1): continue
        stop = stops[c['parada_id']]
        lon, lat = point(stop['Localizacao'])
        direct = direct_meters(g['LatitudeRecebida'], g['LongitudeRecebida'], lat, lon)
        rows.append(dict(gps_id=g['Id'], viagem_id=g['ViagemId'], linha=g['CodigoLinha'],
                         reported_meters=reported, route_meters=route, absolute_difference=abs(reported-route),
                         reported_route_ratio=reported/route, spherical_direct_to_operational_stop=direct,
                         direct_reported_difference=abs(direct-reported), posicao_gps=g['PosicaoNaRota'],
                         posicao_destino=c['posicao_destino'], ocorrencia_operacional=c['ocorrencia_id']))
    if len(rows) != metadata['total']['DistanciasDivergentes']:
        raise ValueError('Divergence count differs from diagnostic')
    selected = []
    for line in sorted({r['linha'] for r in rows}):
        selected += sorted([r for r in rows if r['linha']==line], key=lambda r: (-r['absolute_difference'],r['gps_id']))[:2]
    if rows:
        outlier = max(rows, key=lambda r:r['direct_reported_difference'])
        if outlier not in selected: selected.append(outlier)
    result = dict(version='eta-distance-evidence-3c-v1', count=len(rows),
                  smaller_than_route=sum(r['reported_meters'] < r['route_meters'] for r in rows),
                  direct_within_10m=sum(r['direct_reported_difference']<=10 for r in rows),
                  candidates_sha256=s.sha(candidates), report_sha256=s.sha(report),
                  snapshot_manifest_sha256=metadata['snapshot_manifest_sha256'], sample=selected,
                  conclusion='Code compares direct distance to matched stop against along-route distance to operational target.',
                  limitations=['Spherical direct distance is exploratory, not PostGIS spheroid equality.',
                               'Original matching target is overwritten by operational identity in telemetry.',
                               'No label/tolerance change or certification; causes of individual cases remain unproven.'])
    output.mkdir(parents=True)
    s.write(output/'distance-sample.json',s.canonical(result))
    print(s.canonical({k:result[k] for k in ('count','smaller_than_route','direct_within_10m')}),end='')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('bundle','candidates','report','output'): p.add_argument(name)
    a=p.parse_args(); sample(a.bundle,a.candidates,a.report,a.output)
