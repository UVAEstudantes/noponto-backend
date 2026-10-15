"""Synthetic SQL integration, ONLY a new private Docker container with tmpfs.
No existing database/ports/volumes; no application startup or migrations.
Run: python tools/eta_ml/test_selected_stop_postgis.py
"""
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid


def docker(*args, stdin=None):
    result = subprocess.run(['docker', *args], input=stdin, text=True, capture_output=True, timeout=45)
    if result.returncode:
        raise RuntimeError(result.stderr.strip())
    return result.stdout.strip()


def main():
    endpoint = docker('context', 'inspect', '--format', '{{.Endpoints.docker.Host}}')
    assert endpoint == 'npipe:////./pipe/dockerDesktopLinuxEngine', 'Require local Docker Desktop'
    image = 'postgis/postgis:16-3.4'
    docker('image', 'inspect', image, '--format', '{{.Id}}')  # never pull/build
    name = 'noponto-eta-stop-test-' + uuid.uuid4().hex[:12]
    cid = None
    try:
        if len(sys.argv) == 2:
            assert re.fullmatch('[0-9a-f]{64}', sys.argv[1]), 'Require full ID of this test container'
            cid = sys.argv[1]  # resume only a test-owned container after CLI timeout
        else:
            cid = docker('create', '--pull', 'never', '--rm', '--name', name,
                         '--label', 'noponto.test=selected-stop', '--network', 'none',
                         '--memory', '384m', '--cpus', '1', '--pids-limit', '128',
                         '--tmpfs', '/var/lib/postgresql/data:rw,size=268435456',
                         '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', image)
        state = json.loads(docker('inspect', cid))[0]
        assert state['Config']['Labels']['noponto.test'] == 'selected-stop'
        assert state['Name'].startswith('/noponto-eta-stop-test-')
        assert state['HostConfig']['NetworkMode'] == 'none'
        assert not state['HostConfig']['PortBindings']
        assert '/var/lib/postgresql/data' in state['HostConfig']['Tmpfs']
        assert all(m['Type'] == 'tmpfs' for m in state['Mounts'])
        name = state['Name'].lstrip('/')
        if not state['State']['Running']:
            docker('start', cid)
        print('Private tmpfs container isolation verified', flush=True)
        for _ in range(60):
            # pg_isready alone can accept the entrypoint's TEMPORARY init server.
            # Wait for PID 1 to be the final postgres before creating fixtures.
            main_process = docker('exec',cid,'cat','/proc/1/comm')
            ready = subprocess.run(['docker','exec',cid,'pg_isready','-U','postgres'], capture_output=True, timeout=10)
            if main_process == 'postgres' and ready.returncode == 0:
                break
            time.sleep(.5)
        else:
            raise RuntimeError('Private fixture DB not ready')

        def sql(value):
            return docker('exec',cid,'psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','postgres','-c',value)

        def guid(n):
            return f"'00000000-0000-0000-0000-{n:012d}'"

        sql(f'''
          CREATE EXTENSION IF NOT EXISTS postgis;
          CREATE TABLE "Linhas" ("Id" uuid PRIMARY KEY,"Codigo" text);
          CREATE TABLE "Sentidos" ("Id" uuid PRIMARY KEY,"LinhaId" uuid);
          CREATE TABLE "PadroesOperacionais" ("Id" uuid PRIMARY KEY,"SentidoId" uuid);
          CREATE TABLE "PadroesVersoes" ("Id" uuid PRIMARY KEY,"PadraoOperacionalId" uuid,"Topologia" text,"Geometria" geometry);
          CREATE TABLE "OcorrenciasParadasPadroes" ("Id" uuid PRIMARY KEY,"PadraoVersaoId" uuid,"ParadaId" uuid,"Ordem" int,"PosicaoTracado" double precision);
          CREATE UNIQUE INDEX ON "OcorrenciasParadasPadroes" ("PadraoVersaoId","Ordem");
          INSERT INTO "Linhas" VALUES ({guid(1)},'FIXTURE');
          INSERT INTO "Sentidos" VALUES ({guid(2)},{guid(1)});
          INSERT INTO "PadroesOperacionais" VALUES ({guid(3)},{guid(2)});
          INSERT INTO "PadroesVersoes" VALUES ({guid(4)},{guid(3)},'LINEAR',ST_GeomFromText('LINESTRING(-43 -23,-43 -22,-42 -22)',4326));
          INSERT INTO "OcorrenciasParadasPadroes" VALUES
            ({guid(10)},{guid(4)},{guid(20)},1,.1),
            ({guid(11)},{guid(4)},{guid(21)},2,.3),
            ({guid(12)},{guid(4)},{guid(22)},3,.6),
            ({guid(13)},{guid(4)},{guid(23)},4,.9);
        ''')
        print('Synthetic schema ready', flush=True)
        source = Path(__file__).resolve().parents[2] / 'NoPonto/2-Application/Services/EventosParada/RoadEtaDistances.cs'
        query = re.search(r'const string Sql = """(.*?)""";', source.read_text(encoding='utf-8'), re.S)[1].strip()
        def arr(values):
            return 'ARRAY[' + ','.join(values) + ']'
        def repeat(value):
            return arr([value]*3)
        anchor = '(SELECT ST_Length(ST_LineSubstring("Geometria",0,.3)::geography)-ST_Length(ST_LineSubstring("Geometria",0,.2)::geography) FROM "PadroesVersoes")'
        params = dict(targets=arr([guid(11),guid(12),guid(13)]), stops=arr([guid(21),guid(22),guid(23)]),
                      orders='ARRAY[2,3,4]', versions=repeat(guid(4)), nexts=repeat(guid(11)),
                      patterns=repeat(guid(3)), directions=repeat(guid(2)), lines=repeat(guid(1)),
                      codes=repeat("'FIXTURE'"), positions=repeat('.2'), anchors=repeat(anchor), topologies=repeat("'LINEAR'"))
        def run(**changes):
            values = params | changes
            rendered = re.sub(r'@(\w+)', lambda m: values[m[1]], query)
            return json.loads(sql('BEGIN READ ONLY; SELECT coalesce(json_agg(r),\'[]\') FROM (' + rendered + ') r; COMMIT;'))
        valid = run()
        assert len(valid) == 3 and valid[0]['distance'] < valid[1]['distance'] < valid[2]['distance']
        approximate = float(sql('SELECT ST_Length("Geometria"::geography)*.4 FROM "PadroesVersoes";'))
        assert abs(valid[1]['distance']-approximate) > 1, 'Fixture must distinguish geometry from fraction * length'
        for changes in [dict(positions=repeat('.95')), dict(directions=repeat(guid(99))),
                        dict(versions=repeat(guid(99))), dict(nexts=repeat(guid(12))),
                        dict(orders='ARRAY[9,9,9]'), dict(anchors=repeat('1'))]:
            assert run(**changes) == [], changes.keys()
        print(json.dumps(dict(private_container=name, isolated=True, sql_cases=7,
                              targets=valid, second_target_fraction_times_length=approximate)))
    finally:
        # Only the newly created container, identified by its full returned ID.
        if cid:
            owned = json.loads(docker('inspect', cid))[0]
            if owned['Config']['Labels'].get('noponto.test') == 'selected-stop' and owned['Name'].startswith('/noponto-eta-stop-test-'):
                docker('stop','--time','5',cid)


if __name__ == '__main__':
    main()
