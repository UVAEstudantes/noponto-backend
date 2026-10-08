"""Offline comparison against immutable, previously checked PostGIS results. No certification."""
import argparse
import json
from pathlib import Path
if __package__:
    from . import diagnose as d, snapshot as s
else:
    import diagnose as d
    import snapshot as s


def compare(bundle, candidates, previous_report, output):
    bundle, output = Path(bundle).resolve(), Path(output).resolve()
    if output.exists() or output == bundle or bundle in output.parents:
        raise ValueError('New output outside sealed bundle required')
    previous = json.loads(Path(previous_report).read_text())
    if (previous.get('sources',{}).get('validator_3a') != 'baaf0233be4d3b615ce9c5e9b47eb3cfc4168f3c319dca6475ad9c6e1858e094'
            or previous.get('sources',{}).get('sql_3a') != d.source_hash(d.SQL)
            or previous['candidate_file_sha256'] != s.sha(candidates)
            or previous['snapshot_manifest_sha256'] != s.sha(bundle/'snapshot.manifest.json')
            or previous.get('training_dataset') is not False or not previous['geometry_checked']):
        raise ValueError('Previous evidence/policy/hash mismatch')
    # Diagnose itself verifies all sealed sources and SQL result associations.
    current = d.diagnose(bundle, output, candidates)
    before, after = previous['total'], current['total']
    keys = sorted(set(before)|set(after))
    result = dict(version='eta-distance-policy-comparison-3g2-v1',
                  previous_report_sha256=s.sha(previous_report), current_report_sha256=s.sha(output/'report.json'),
                  matrix={key:dict(before=before.get(key,0),after=after.get(key,0)) for key in keys},
                  removed_semantic_rejections=before['DistanciasDivergentes']-after.get('DistanciasDivergentes',0),
                  technically_preliminary=after['TecnicamenteElegivelPreliminar'],certified_for_training=0,
                  protection_evidence_unverified=after['gps'],
                  limitations=['Component diagnosis with previously observed PostGIS results; no new database connection.',
                               'Outlier matching origin still unknown; component eligibility does not establish its physical validity.',
                               'No split/certification/real training; all whole-trip protection evidence remains unverified.'])
    s.write(output/'comparison.json',s.canonical(result))
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('bundle','candidates','previous-report','output'):parser.add_argument('--'+key,required=True)
    a=parser.parse_args();print(json.dumps(compare(a.bundle,a.candidates,a.previous_report,a.output),ensure_ascii=False))
