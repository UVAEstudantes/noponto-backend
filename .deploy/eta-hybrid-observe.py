#!/usr/bin/env python3
"""Finite, read-only observation of production API/dependencies; no GPS acquisition."""
import argparse, collections, datetime, hashlib, json, pathlib, re, subprocess, time, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent
NAMES = ['noponto_api', 'transporte_postgres', 'transporte_redis']

def command(args, timeout=12, input=None):
    return subprocess.check_output(args, input=input, text=True, stderr=subprocess.DEVNULL, timeout=timeout).strip()

def sql(query):
    return command(['docker','exec','-i','transporte_postgres','sh','-c',
        'PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=5000" psql -X -q -A -t -U "$POSTGRES_USER" -d "$POSTGRES_DB"'], input=query+';\n')

def get(path):
    start=time.monotonic()
    try:
        with urllib.request.urlopen('http://127.0.0.1:8080'+path, timeout=8) as response:
            data=response.read()
            try: payload=json.loads(data) if data else None
            except (ValueError,UnicodeDecodeError): payload=None
            return response.status,(time.monotonic()-start)*1000,payload
    except Exception as error: return 0,(time.monotonic()-start)*1000,type(error).__name__

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--seconds',type=int,default=900);parser.add_argument('--name',default='monitor')
    parser.add_argument('--lines',default='838,855,884')
    args=parser.parse_args();assert 30<=args.seconds<=900
    codes=args.lines.split(',');assert 1<=len(codes)<=3 and all(re.fullmatch(r'[A-Z0-9]{1,12}',code) for code in codes)
    # Current published geometry: one directed route per requested line, three future stop bands.
    query='''WITH selected AS (SELECT DISTINCT ON (l."Codigo") l."Codigo" code,v."Id" version
      FROM "Linhas" l JOIN "Sentidos" s ON s."LinhaId"=l."Id"
      JOIN "PadroesOperacionais" p ON p."SentidoId"=s."Id" JOIN "PadroesVersoes" v ON v."Id"=p."VersaoAtualId"
      WHERE l."Codigo" IN ('838','855','884') ORDER BY l."Codigo",v."Id"),
      stops AS (SELECT x.code,o."ParadaId" id, o."Ordem",row_number() OVER (PARTITION BY x.code ORDER BY o."Ordem") n,
      count(*) OVER(PARTITION BY x.code) total FROM selected x JOIN "OcorrenciasParadasPadroes" o ON o."PadraoVersaoId"=x.version)
      SELECT code||'|'||id::text FROM stops WHERE n IN (greatest(2,total/4),greatest(3,total/2),greatest(4,total*3/4))'''
    query=query.replace("'838','855','884'",','.join("'"+code+"'" for code in codes))
    targets=[line.split('|') for line in sql(query).splitlines() if '|' in line]
    assert len(targets)>=3, 'No production stop sample'
    start=time.monotonic(); wall=time.time(); iteration=0; failures=0; previous={}
    path=ROOT/(args.name+'.jsonl')
    with path.open('w') as output:
        while True:
            elapsed=time.monotonic()-start
            row={'at':datetime.datetime.now(datetime.timezone.utc).isoformat(),'elapsed':elapsed,'events':[],'requests':[]}
            containers=json.loads(command(['docker','inspect']+NAMES))
            row['containers']=[{'name':c['Name'],'image':c['Image'],'running':c['State']['Running'],
                'oom':c['State']['OOMKilled'],'restarts':c['RestartCount'],'health':c['State'].get('Health',{}).get('Status')} for c in containers]
            row['stats']=command(['docker','stats','--no-stream','--format','{{json .}}']+NAMES,20).splitlines()
            mem={k:int(v.strip().split()[0]) for k,v in (x.split(':',1) for x in pathlib.Path('/proc/meminfo').read_text().splitlines())}
            row['host']={'memAvailableKiB':mem['MemAvailable'],'memTotalKiB':mem['MemTotal'],
                'load':pathlib.Path('/proc/loadavg').read_text().strip(), 'cpu':pathlib.Path('/proc/stat').read_text().splitlines()[0]}
            try: row['postgres']=sql("SELECT row_to_json(x) FROM (SELECT numbackends,xact_commit,xact_rollback,deadlocks,temp_bytes FROM pg_stat_database WHERE datname=current_database()) x")
            except Exception: row['postgres']='unavailable'
            try:
                row['redis']=command(['docker','exec','transporte_redis','redis-cli','PING'])
                row['redisInfo']=command(['docker','exec','transporte_redis','redis-cli','INFO','stats'])
            except Exception: row['redis']='unavailable'
            status,ms,_=get('/');row['requests'].append({'path':'/','status':status,'ms':ms})
            for line,stop in targets:
                status,ms,events=get('/paradas/'+stop+'/eventos');row['requests'].append({'line':line,'status':status,'ms':ms})
                if status!=200 or not isinstance(events,list): continue
                for event in events:
                    key=hashlib.sha256(event['eventId'].encode()).hexdigest()[:16]
                    record={k:event.get(k) for k in ['codigoLinha','source','quality','secondsUntilEvent','estimatedAt','lastRealtimeEvidenceUtc','distanciaRestanteMetros']}
                    record['key']=key
                    prior=previous.get(key)
                    if event.get('estimatedAt'):
                        at=datetime.datetime.fromisoformat(event['estimatedAt']).timestamp()
                        if prior and prior[0]!=event.get('lastRealtimeEvidenceUtc'): record['revisionSeconds']=at-prior[1]
                        previous[key]=(event.get('lastRealtimeEvidenceUtc'),at)
                    row['events'].append(record)
            # Bounded series memory, metrics include unavailable events and repeated queries explicitly.
            if len(previous)>10000: previous.clear()
            output.write(json.dumps(row)+'\n');output.flush();iteration+=1
            grave=not row['containers'][0]['running'] or row['containers'][0]['oom'] or row['containers'][0]['restarts']>0
            failures=failures+1 if row['requests'][0]['status']!=200 else 0
            print(json.dumps({'sample':iteration,'elapsed':round(elapsed,1),'httpErrors':sum(r['status']!=200 for r in row['requests']),
                'sources':dict(collections.Counter(e['source'] for e in row['events'])),'grave':grave}),flush=True)
            if args.name=='monitor' and (grave or failures>=3):
                command(['python3',str(ROOT/'eta-hybrid-release.py'),'rollback-flag'],180)
                print('Severe health regression: hybrid flag rolled back',flush=True);break
            if elapsed>=args.seconds: break
            time.sleep(max(0,min(30,args.seconds-(time.monotonic()-start))))
    (ROOT/(args.name+'-finished.json')).write_text(json.dumps({'seconds':time.monotonic()-start,'samples':iteration,'started':wall,'finished':time.time()}))

if __name__=='__main__':
    try: main()
    except Exception as error:
        print('Observation failed:',type(error).__name__,flush=True);raise SystemExit(1)
