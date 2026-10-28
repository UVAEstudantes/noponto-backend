import subprocess,pathlib,re,json,argparse
parser=argparse.ArgumentParser(description="Compare matching SQL only in a disposable local PostGIS container.")
parser.add_argument("--container", default="noponto-pipeline-latency-20261028")
parser.add_argument("--before-revision", default="6babc1036d7467af399899a0d1596144a708e43d")
parser.add_argument("--output", required=True)
args=parser.parse_args()
ROOT=pathlib.Path(args.output).resolve();ROOT.mkdir(parents=True,exist_ok=True)
CONTAINER=args.container
if not CONTAINER.startswith('noponto-pipeline-latency-'):raise SystemExit('Disposable container name required')
context=json.loads(subprocess.check_output(['docker','context','inspect']))[0]
endpoint=context['Endpoints']['docker']['Host']
if not endpoint.startswith(('npipe://','unix://','tcp://127.0.0.1:','tcp://localhost:')):raise SystemExit('Only a local Docker context is allowed')
container=json.loads(subprocess.check_output(['docker','inspect',CONTAINER]))[0]
if container['Config']['Image']!='postgis/postgis:16-3.4':raise SystemExit('Expected disposable PostGIS16 image')
ports=container['HostConfig']['PortBindings'].get('5432/tcp',[])
if not ports or any(x['HostIp']!='127.0.0.1' or x['HostPort']!='55436' for x in ports):raise SystemExit('Expected loopback-only disposable port55436')
def sql(q):
 r=subprocess.run(['docker','exec','-i',CONTAINER,'psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','pipeline_test','-d','pipeline_test'],input=q.encode(),capture_output=True,check=True)
 return r.stdout.decode()
old=re.search(r'SqlMatchingCombinadoLoteSetBased = """(.*?)""";',subprocess.check_output(['git','show',args.before_revision+':NoPonto/4-Data/Repositories/GpsItinerarioRepository.CombinedSetBased.cs']).decode(),re.S)[1]
# Compare the actual repository SQL. Only synthetic input/structural data are used.
new=re.search(r'SqlMatchingCombinadoLoteSetBased = \"\"\"(.*?)\"\"\";',pathlib.Path('NoPonto/4-Data/Repositories/GpsItinerarioRepository.CombinedSetBased.cs').read_text(encoding='utf8'),re.S)[1]
if old==new:raise SystemExit('Before and candidate SQL are identical')
schema='pipeline_latency_bench'
setup='''CREATE SCHEMA IF NOT EXISTS pipeline_latency_bench; SET search_path=pipeline_latency_bench,public;
CREATE TABLE IF NOT EXISTS "Linhas" ("Id" uuid PRIMARY KEY,"Codigo" text);
CREATE TABLE IF NOT EXISTS "Sentidos" ("Id" uuid PRIMARY KEY,"LinhaId" uuid);
CREATE TABLE IF NOT EXISTS "PadroesOperacionais" ("Id" uuid PRIMARY KEY,"SentidoId" uuid,"VersaoAtualId" uuid);
CREATE TABLE IF NOT EXISTS "PadroesVersoes" ("Id" uuid PRIMARY KEY,"PadraoOperacionalId" uuid,"Geometria" geometry(LineString,4326),"Topologia" text);
CREATE TABLE IF NOT EXISTS "Paradas" ("Id" uuid PRIMARY KEY,"Nome" text,"Localizacao" geometry(Point,4326));
CREATE TABLE IF NOT EXISTS "OcorrenciasParadasPadroes" ("Id" uuid PRIMARY KEY,"ParadaId" uuid,"PadraoVersaoId" uuid,"Ordem" int,"PosicaoTracado" float8,"DistanciaAcumuladaMetros" float8,"DistanciaDaLinhaMetros" float8);
'''
ids=[f'00000000-0000-0000-0000-{i:012}' for i in range(1,7)]
l,s,r1,r2,stop,occ=ids
setup+=f'''INSERT INTO "Linhas" VALUES ('{l}','SYNTHETIC') ON CONFLICT DO NOTHING;
INSERT INTO "Sentidos" VALUES ('{s}','{l}') ON CONFLICT DO NOTHING;'''
for n,r in enumerate((r1,r2)):
 setup+=f'''INSERT INTO "PadroesOperacionais" VALUES ('{r}','{s}','{r}') ON CONFLICT DO NOTHING;
 INSERT INTO "PadroesVersoes" SELECT '{r}','{r}',ST_MakeLine(array(SELECT ST_SetSRID(ST_MakePoint(-43.21+i*0.000005,-22.9+{n}*0.0002),4326) FROM generate_series(0,4000)i)),'LINEAR' ON CONFLICT DO NOTHING;'''
setup+=f'''INSERT INTO "Paradas" VALUES ('{stop}','Synthetic stop',ST_SetSRID(ST_MakePoint(-43.195,-22.9),4326)) ON CONFLICT DO NOTHING;
INSERT INTO "OcorrenciasParadasPadroes" VALUES ('{occ}','{stop}','{r1}',1,0.75,1500,0) ON CONFLICT DO NOTHING;'''
sql(setup)
inputs=[dict(input_id=f'input-{i}',codigo='SYNTHETIC',lat=-22.9,lon=-43.205+i*0.00001,bearing=90,dist_max=250,usar_anterior=True,padrao_versao_id=r1,fracao_min=.1,fracao_max=.9,usar_operacional=False) for i in range(100)]
values="'"+json.dumps(inputs).replace("'","''")+"'"
results=[]
for iteration in range(5):
 for label,query in [('before',old),('candidate',new)] if iteration%2==0 else [('candidate',new),('before',old)]:
  q=query.replace('@inputs',values)
  plan=json.loads(sql(f'SET search_path={schema},public; EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) '+q))[0]
  (ROOT/f'explain-b-{label}.json').write_text(json.dumps(plan,indent=2))
  results.append(dict(iteration=iteration,label=label,execution_ms=plan['Execution Time'],planning_ms=plan['Planning Time'],jit=plan.get('JIT')))
  print(json.dumps(results[-1]),flush=True)
# Exact output comparison, all columns including projection and stop distance.
before=json.loads(sql(f'SET search_path={schema},public; SELECT json_agg(row_to_json(x) ORDER BY input_id,ramo) FROM ('+old.replace('@inputs',values)+') x;'))
after=json.loads(sql(f'SET search_path={schema},public; SELECT json_agg(row_to_json(x) ORDER BY input_id,ramo) FROM ('+new.replace('@inputs',values)+') x;'))
assert before==after
(ROOT/'sql-candidate-b.json').write_text(json.dumps({'before_revision':args.before_revision,'queries':results,'equal_rows':len(before)},indent=2))
(ROOT/'candidate-b-validated.sql').write_text(new)
print('exact_rows_equal',len(before))
