"""Manual controlled-window supervisor. Only --tick reads an explicitly prepared state.
No raw positions, identities, configuration or provider exceptions are written to output.
"""
import base64, copy, datetime as dt, hashlib, json, math, os, pathlib, re, signal, socket, stat, statistics, struct, subprocess, sys, threading, time, urllib.request
from probe_http import get, ProbeError

ROOT = pathlib.Path(__file__).resolve().parent
STATE = pathlib.Path('/home/guilherme/.noponto-deploy')
LINES = ('838', '898', '006', '457', '483', '355', '805')
CONTROL_LINES = ('35', '60', '67')
stop = threading.Event()
guard = threading.Lock()
state_guard = threading.Lock()
event_guard = threading.Lock()
wire = {'messages':0, 'duplicates':0, 'regressions':0, 'invalid':0, 'connections':0, 'errors':0, 'last':0, 'lines':{}, 'connected':False, 'last_error_kind':None}
last_gps = {}
activation_epoch = None
APPROVED_IMAGE = 'ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8'
capture_process = None
capture_contract = None
PROC_ROOT = pathlib.Path('/proc')
CAPTURE_FILES = ('its_window.py','its_log_capture.py','probe_http.py','its.primary.override.yml','gtfs.rollback.override.yml')
CAPTURE_KEYS = ('session','source_sha256','pid','pid_start','container_id',
                'directory_device','directory_inode','archive_device','archive_inode')

class CaptureFailure(RuntimeError):
    def evidence(self):
        return {'type':'CaptureFailure','classification':'evidence_capture','reason':'capture_evidence_failed'}


def capture_files_valid(s):
    if s.get('image')!=APPROVED_IMAGE:raise CaptureFailure('capture_image_mismatch')
    expected=s.get('operational_sha256',{})
    for name in CAPTURE_FILES:
        if expected.get(name)!=hashlib.sha256((ROOT/name).read_bytes()).hexdigest():
            raise CaptureFailure('operational_hash_mismatch')
    return expected


def process_identity(pid, birth, session):
    """Match the live process, not just a reusable PID or a plausible health file."""
    try:
        proc=PROC_ROOT/str(pid)
        fields=(proc/'stat').read_text().split()
        if fields[21]!=str(birth) or fields[2]=='Z':return False
        args=[part.decode() for part in (proc/'cmdline').read_bytes().split(b'\0') if part]
        expected=['-B',str(ROOT/'its_log_capture.py'),'--directory',str(ROOT/'capture'),
                  '--session',session,'--seconds','1000']
        return len(args)>=2 and args[1:]==expected
    except (OSError,UnicodeError,IndexError,ValueError):return False


def process_birth(pid):
    return (PROC_ROOT/str(pid)/'stat').read_text().split()[21]


def capture_descriptors(pid):
    """A proof reader must have no SSH stdio or inherited deploy lock."""
    if os.name!='posix':return True
    try:
        if os.getsid(pid)!=pid or os.getpgid(pid)!=pid:return False
        descriptors=PROC_ROOT/str(pid)/'fd'
        for fd in descriptors.iterdir():
            try:
                if 'deploy.lock' in os.readlink(fd):return False
            except FileNotFoundError:continue  # A transient Docker FD can close during inspection.
        return all(os.readlink(descriptors/str(fd))=='/dev/null' for fd in (0,1,2))
    except OSError:return False


def capture_file_identity():
    directory=ROOT/'capture';archive=directory/'aggregates.jsonl';health=directory/'health.json'
    if any(p.is_symlink() for p in (ROOT,directory,archive,health)):raise CaptureFailure('capture_symlink')
    r,d,a,h=ROOT.stat(),directory.stat(),archive.stat(),health.stat()
    if not stat.S_ISDIR(d.st_mode) or not stat.S_ISREG(a.st_mode) or not stat.S_ISREG(h.st_mode):
        raise CaptureFailure('capture_file_type')
    if os.name=='posix' and (r.st_mode&0o777!=0o700 or d.st_mode&0o777!=0o700 or a.st_mode&0o777!=0o600 or h.st_mode&0o777!=0o600):
        raise CaptureFailure('capture_permissions')
    return {'directory_device':d.st_dev,'directory_inode':d.st_ino,
            'archive_device':a.st_dev,'archive_inode':a.st_ino}


def read_capture_health():
    path=ROOT/'capture'/'health.json'
    if path.stat().st_size>4096:raise CaptureFailure('capture_health_limit')
    value=json.loads(path.read_text())
    if not isinstance(value,dict):raise CaptureFailure('capture_health_schema')
    return value


def capture_matches(contract, health, container=None, initial_container=True):
    if not isinstance(contract.get('pid'),int) or contract['pid']<=0 or not re.fullmatch(r'[0-9]+',str(contract.get('pid_start',''))):
        raise CaptureFailure('capture_pid_contract')
    if not re.fullmatch(r'[a-f0-9]{32}',str(contract.get('session',''))) or not re.fullmatch(r'[a-f0-9]{64}',str(contract.get('source_sha256',''))):
        raise CaptureFailure('capture_session_contract')
    if not re.fullmatch(r'[a-f0-9]{64}',str(contract.get('container_id',''))):
        raise CaptureFailure('capture_container_contract')
    if any(health.get(k)!=contract[k] for k in CAPTURE_KEYS if k in health and k in contract and (initial_container or k!='container_id')):
        raise CaptureFailure('capture_identity_mismatch')
    required=('session','source_sha256','pid','pid_start','container_id') if initial_container or container is None else ('session','source_sha256','pid','pid_start')
    if any(health.get(k)!=contract[k] for k in required):
        raise CaptureFailure('capture_health_identity')
    if health.get('image')!=APPROVED_IMAGE or (container and health.get('container_id')!=container) or not re.fullmatch(r'[a-f0-9]{64}',str(health.get('container_id',''))):
        raise CaptureFailure('capture_container')
    age=time.time()-health['heartbeat']
    if not math.isfinite(age) or not 0<=age<=10:raise CaptureFailure('capture_stale')
    if health.get('state')!='running' or health.get('ready') is not True or health.get('failure') or health.get('gap_count')!=0 or health.get('truncated') is not False:
        raise CaptureFailure('capture_unhealthy')
    if any(type(health.get(k)) is not int for k in ('records','bytes','input_bytes')) or health['records']<2 or health['bytes']<=0 or health['input_bytes']<0:
        raise CaptureFailure('capture_counts')
    if not process_identity(contract['pid'],contract['pid_start'],contract['session']):
        raise CaptureFailure('capture_process_identity')
    if capture_file_identity()!={k:contract[k] for k in ('directory_device','directory_inode','archive_device','archive_inode')}:
        raise CaptureFailure('capture_file_identity')


def archive_prefix_valid(session, health):
    """Check the live archive's session and complete durable prefix at handoff."""
    path=ROOT/'capture'/'aggregates.jsonl'
    if path.stat().st_size>8*1024*1024:raise CaptureFailure('capture_archive_limit')
    # The writer may append another line concurrently. Health names the fsynced prefix.
    size=health['bytes']
    if not isinstance(size,int) or not 0<size<=8*1024*1024:raise CaptureFailure('capture_archive_size')
    with path.open('rb') as file:prefix=file.read(size)
    rows=prefix.splitlines(keepends=True)
    if len(prefix)!=size or len(rows)!=health['records'] or len(rows)>12000 or len(rows)<2 or any(not line.endswith(b'\n') or len(line)>16384 for line in rows):
        raise CaptureFailure('capture_archive_prefix')
    try:events=[json.loads(line) for line in rows]
    except (ValueError,UnicodeError):raise CaptureFailure('capture_archive_schema') from None
    if not isinstance(events[0],dict) or events[0].get('kind')!='begin' or events[0].get('schema')!=1 or events[0].get('session')!=session:
        raise CaptureFailure('capture_archive_session')
    if any(not isinstance(event,dict) or event.get('kind') in ('gap','end') for event in events[1:]):
        raise CaptureFailure('capture_archive_gap_or_closed')


class AdoptedCapture:
    def __init__(self, contract):self.pid=contract['pid'];self.birth=contract['pid_start'];self.session=contract['session']
    def poll(self):return None if process_identity(self.pid,self.birth,self.session) else 1
    def terminate(self):
        if self.poll() is None:os.kill(self.pid,signal.SIGTERM)


def adopt_capture(s):
    global capture_process,capture_contract
    expected=capture_files_valid(s)
    contract=s.get('capture')
    if not isinstance(contract,dict) or any(k not in contract for k in CAPTURE_KEYS):
        raise CaptureFailure('capture_contract_incomplete')
    if contract['source_sha256']!=expected['its_log_capture.py']:
        raise CaptureFailure('capture_script_hash')
    health=read_capture_health()
    capture_matches(contract,health,inspect()[0]['Id'])
    if not capture_descriptors(contract['pid']):raise CaptureFailure('capture_inherited_descriptors')
    archive_prefix_valid(contract['session'],health)
    capture_process=AdoptedCapture(contract)
    capture_contract=dict(contract)


def start_capture(s):
    global capture_process,capture_contract
    expected=capture_files_valid(s)
    if s.get('capture') or (ROOT/'capture').exists() or capture_process is not None:
        raise CaptureFailure('capture_already_exists')
    session=os.urandom(16).hex()
    # New session, no inherited lock descriptor/stdout/SSH. Never wait for this child on rollback.
    process=subprocess.Popen([sys.executable,'-B',str(ROOT/'its_log_capture.py'),
        '--directory',str(ROOT/'capture'),'--session',session,'--seconds','1000'],
        stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
        close_fds=True,start_new_session=True)
    capture_process=process
    s['capture']={'session':session,'source_sha256':expected['its_log_capture.py'],'pid':process.pid}
    save(s)
    try:
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            if process.poll() is not None:raise CaptureFailure('capture_exited_during_start')
            try:
                health=read_capture_health()
                if health.get('ready') and health.get('session')==session:break
            except (OSError,ValueError,CaptureFailure):pass
            time.sleep(.1)
        else:raise CaptureFailure('capture_start_timeout')
        birth=health.get('pid_start');container=health.get('container_id')
        contract={**s['capture'],'pid_start':birth,'container_id':container,**capture_file_identity()}
        capture_matches(contract,health,inspect()[0]['Id'])
        if not capture_descriptors(process.pid):raise CaptureFailure('capture_inherited_descriptors')
        archive_prefix_valid(session,health)
        capture_contract=contract
        s['capture']=dict(contract);save(s)
    except Exception:
        if process.poll() is None:
            try:process.terminate()
            except ProcessLookupError:pass
        raise CaptureFailure('capture_start_invalid') from None


def ensure_capture(s):
    if capture_process is not None:
        if s.get('capture')!=capture_contract or not capture_health(inspect()[0]['Id'])['healthy']:
            raise CaptureFailure('capture_controller_mismatch')
        return
    if s.get('capture') is not None:
        adopt_capture(s)
    elif (ROOT/'capture').exists():
        raise CaptureFailure('capture_unclaimed_archive')
    else:
        start_capture(s)


def capture_health(container=None):
    try:
        if capture_process is None or capture_process.poll() is not None:raise CaptureFailure()
        value=read_capture_health()
        capture_matches(capture_contract,value,container,initial_container=False)
        return {'healthy':True,'session':value['session'],'records':value['records'],'bytes':value['bytes'],'families':value.get('families',{})}
    except (OSError,ValueError,TypeError,KeyError,CaptureFailure):
        return {'healthy':False,'reason':'capture_evidence_failed'}


def stop_capture():
    # Called only after rollback attempt. No wait/join/fsync or deploy lock in the collector.
    if capture_process is not None and capture_process.poll() is None:
        try:capture_process.terminate()
        except ProcessLookupError:pass


def stop_orphan_capture(s):
    """Watchdog cleanup after its own successful GTFS rollback."""
    contract=s.get('capture')
    if not isinstance(contract,dict) or any(k not in contract for k in CAPTURE_KEYS):
        raise CaptureFailure('orphan_contract_incomplete')
    capture_files_valid(s)
    health=read_capture_health()
    if any(health.get(k)!=contract[k] for k in ('session','source_sha256','pid','pid_start')):
        raise CaptureFailure('orphan_identity_mismatch')
    if capture_file_identity()!={k:contract[k] for k in ('directory_device','directory_inode','archive_device','archive_inode')}:
        raise CaptureFailure('orphan_file_identity')
    process=AdoptedCapture(contract)
    if process.poll() is None:
        if not capture_descriptors(process.pid):raise CaptureFailure('orphan_descriptors')
        process.terminate()


def finalize_capture(s):
    """Verify once after the API is safe and the deploy lock has been released."""
    if 'capture_verification' in s:return s['capture_verification']
    result={'integrity_valid':False,'archive_complete':False,'evidence_complete':False}
    if s.get('capture'):
        deadline=time.monotonic()+8
        while time.monotonic()<deadline:
            try:
                health=read_capture_health()
                if health.get('state')=='closed' and health.get('session')==s['capture']['session']:
                    result=json.loads(run([sys.executable,'-B',str(ROOT/'its_log_capture.py'),
                                            '--directory',str(ROOT/'capture'),'--verify'],timeout=5))
                    break
            except (OSError,ValueError,KeyError,CommandFailure):break
            time.sleep(.2)
    # Closed schema: do not persist arbitrary output from a failed helper.
    safe={key:result.get(key) for key in ('integrity_valid','archive_complete','evidence_complete',
                                          'file_sha256','gap_count','truncated','reason')}
    s['capture_verification']=safe;save(s);record('capture_archive',safe)
    return safe


class CommandFailure(RuntimeError):
    def __init__(self,args,code=None,timeout=False):
        self.tool=pathlib.Path(args[0]).name
        self.operation=args[1] if self.tool=='docker' and len(args)>1 and args[1] in ('logs','inspect','stats','ps','compose') else 'command'
        self.code=code;self.reason='command_timeout' if timeout else 'command_interrupted' if code is not None and code<0 else 'command_failed'
        super().__init__(self.reason+':'+self.tool+':'+self.operation)
    def evidence(self):
        return {'type':'CommandFailure','reason':self.reason,'tool':self.tool,'operation':self.operation,'exit_code':self.code}

def failure_evidence(error):
    if isinstance(error,ProbeError):
        return {**error.evidence(),'classification':'backend_transport' if error.reason in ('transport_failure','timeout') else 'backend_http' if error.reason=='http_failure' else 'backend_contract'}
    return error.evidence() if isinstance(error,(ProbeError,CommandFailure,CaptureFailure,SafetyFailure)) else {'type':type(error).__name__,'reason':'unclassified_failure'}

def stop_reason():
    try:
        request=json.loads((ROOT/'stop-request.json').read_text())
        if request.get('reason') in ('operator_rollback','gate_898_unproven','safety_degradation'):
            return request['reason']
    except (OSError,ValueError):pass
    return 'supervisor_interrupted'

def request_stop(reason):
    if reason not in ('operator_rollback','gate_898_unproven','safety_degradation'):raise ValueError('invalid_stop_reason')
    s=load()
    if s['phase'] not in ('baseline','its'):raise RuntimeError('not_running')
    pid=s['pid']
    if pathlib.Path('/proc/'+str(pid)+'/stat').read_text().split()[21]!=s['pid_start']:raise RuntimeError('pid_changed')
    if str(pathlib.Path(__file__).resolve()).encode() not in pathlib.Path('/proc/'+str(pid)+'/cmdline').read_bytes():raise RuntimeError('command_changed')
    target=ROOT/'stop-request.tmp';target.write_text(json.dumps({'reason':reason,'time':time.time()}));os.chmod(target,0o600);os.replace(target,ROOT/'stop-request.json')
    # Notify only the controller. Killing its process group also interrupts docker/logs.
    os.kill(pid,signal.SIGTERM)

def run(args, timeout=15, data=None, env=None):
    try:r = subprocess.run(args, input=data, capture_output=True, text=True, timeout=timeout, env=env)
    except subprocess.TimeoutExpired:raise CommandFailure(args,timeout=True) from None
    if r.returncode: raise CommandFailure(args,r.returncode)
    return r.stdout+(r.stderr if args[:2]==['docker','logs'] else '')

def load(): return json.loads((ROOT/'state.json').read_text())
def save(value):
    with state_guard:
        target=ROOT/'state.tmp'; target.write_text(json.dumps(value)); os.chmod(target,0o600); os.replace(target,ROOT/'state.json')
def record(kind,value):
    with event_guard:
        with open(ROOT/'events.jsonl','a') as f: f.write(json.dumps({'utc':dt.datetime.now(dt.timezone.utc).isoformat(),'kind':kind,'value':value})+'\n')

def inspect():
    names=['noponto_api','transporte_postgres','transporte_redis','auth_api','auth_postgres','auth_redis','portainer','dozzle']
    return json.loads(run(['docker','inspect',*names]))

def source(api): return dict(x.split('=',1) for x in api['Config']['Env'] if '=' in x)
def compose(image, override=None):
    args=['docker','compose','--project-name','noponto','--project-directory','/opt/stacks/noponto','--env-file','/opt/stacks/noponto/.env']
    for p in ['/opt/stacks/noponto/docker-compose.yml','/home/guilherme/noponto-release-artifacts/docker-compose.gtfsrt-production.proposed.yml',str(STATE/'physical-eta.override.yml'),str(STATE/'historical-eta-shadow.override.yml')]: args+=['-f',p]
    if override: args+=['-f',str(ROOT/override)]
    return args,dict(os.environ,GTFSRT_API_IMAGE=image)

def fresh(positions):
    now=time.time(); ages=[]
    for p in positions:
        ts=p.get('timestampGps') or p.get('TimestampGps')
        ages.append(now-dt.datetime.fromisoformat(ts.replace('Z','+00:00')).timestamp())
    ordered=sorted(ages)
    return {'vehicles':len(ages),'until40':sum(0<=a<=40 for a in ages),'until60':sum(0<=a<=60 for a in ages),'until180':sum(0<=a<=180 for a in ages),'older180':sum(a>180 for a in ages),'future':sum(a<0 for a in ages),'max_age':max(ages,default=None),
            'median':statistics.median(ages) if ages else None,'p90':ordered[max(0,math.ceil(len(ordered)*.9)-1)] if ages else None,
            'new_after_activation':sum(now-a>=activation_epoch for a in ages) if activation_epoch is not None else 0}

def data_state(stats):
    if not stats['vehicles']:return 'valid_empty'
    if stats.get('future',0):return 'invalid_physical_time'
    if stats['older180']==stats['vehicles']:return 'present_stale'
    return 'present_recent' if stats['until60'] else 'present_aging'

def parse_flow_logs(logs):
    frames=[]
    for line in logs.splitlines():
        if 'GPS flow: ' not in line:continue
        raw=line.split('GPS flow: ',1)[1]
        if len(raw)>4096:continue
        try:
            value=json.loads(raw)
            if isinstance(value,dict) and value.get('line') in LINES and isinstance(value.get('generation'),int):frames.append(value)
        except ValueError:continue
    return frames

def correlation_state(frames,wire):
    server=any(f.get('line')=='898' and f.get('source')=='GTFSRT_BUS' and f.get('linked_complete_new',0)>0 for f in frames)
    client=wire.get('lines',{}).get('898',{}).get('new_after_activation',0)>0
    return 'proven' if server and client else 'server_only' if server else 'client_only' if client else 'unproven'

def send_frame(sock,payload,opcode=1):
    b=payload if isinstance(payload,bytes) else payload.encode(); mask=os.urandom(4); n=len(b)
    header=bytes([0x80|opcode,0x80|n]) if n<126 else bytes([0x80|opcode,0xfe])+struct.pack('!H',n)
    sock.sendall(header+mask+bytes(v^mask[i%4] for i,v in enumerate(b)))

def read_exact(sock,n):
    parts=bytearray()
    while len(parts)<n:
        b=sock.recv(n-len(parts))
        if not b: raise EOFError()
        parts.extend(b)
    return bytes(parts)

def observe_snapshot(values):
    ids=[p['ordem'].upper() for p in values]
    with guard:
        wire['messages']+=1; wire['last']=time.time(); wire['duplicates']+=len(ids)-len(set(ids))
        for p in values:
            key=p['ordem'].upper(); gps=dt.datetime.fromisoformat(p['timestampGps'].replace('Z','+00:00')).timestamp()
            if gps<last_gps.get(key,gps): wire['regressions']+=1
            last_gps[key]=max(gps,last_gps.get(key,gps))
            lat,lon=p['latitude'],p['longitude']
            if not math.isfinite(lat) or not math.isfinite(lon) or not -90<=lat<=90 or not -180<=lon<=180: wire['invalid']+=1
        for line in set(p['codigoLinha'] for p in values): wire['lines'][line]=[p for p in values if p['codigoLinha']==line]

def socket_worker():
    while not stop.is_set():
        sock=None
        try:
            req=urllib.request.Request('http://127.0.0.1:8080/hub/gps/negotiate?negotiateVersion=1',data=b'',method='POST')
            with urllib.request.urlopen(req,timeout=5) as response: token=json.loads(response.read(65536))['connectionToken']
            sock=socket.create_connection(('127.0.0.1',8080),5); sock.settimeout(2)
            key=base64.b64encode(os.urandom(16)).decode()
            sock.sendall(('GET /hub/gps?id='+token+' HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: '+key+'\r\nSec-WebSocket-Version: 13\r\n\r\n').encode())
            header=bytearray()
            while not header.endswith(b'\r\n\r\n'):
                header.extend(read_exact(sock,1))
                if len(header)>16384: raise RuntimeError('upgrade_limit')
            accept=base64.b64encode(hashlib.sha1((key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest())
            if b' 101 ' not in header or accept not in header: raise RuntimeError('upgrade_failed')
            send_frame(sock,'{"protocol":"json","version":1}\x1e')
            for i,line in enumerate(LINES+CONTROL_LINES): send_frame(sock,json.dumps({'type':1,'invocationId':str(i),'target':'InscreverseLinha','arguments':[line]})+'\x1e')
            with guard: wire['connections']+=1; wire['connected']=True
            pending=''; fragments=bytearray(); last_ping=time.monotonic()
            while not stop.is_set():
                if time.monotonic()-last_ping>10: send_frame(sock,'{"type":6}\x1e'); last_ping=time.monotonic()
                try: h=read_exact(sock,2)
                except socket.timeout: continue
                opcode=h[0]&15; n=h[1]&127
                if n==126: n=struct.unpack('!H',read_exact(sock,2))[0]
                if n==127: n=struct.unpack('!Q',read_exact(sock,8))[0]
                if n>1_000_000: raise RuntimeError('frame_limit')
                mask=read_exact(sock,4) if h[1]&128 else None; body=read_exact(sock,n)
                if mask: body=bytes(v^mask[i%4] for i,v in enumerate(body))
                if opcode==8: break
                if opcode==9: send_frame(sock,body,10); continue
                if opcode not in (0,1): continue
                fragments.extend(body)
                if not h[0]&128: continue
                pending+=fragments.decode(); fragments.clear()
                while '\x1e' in pending:
                    item,pending=pending.split('\x1e',1)
                    if not item: continue
                    obj=json.loads(item)
                    if obj.get('error'): raise RuntimeError('hub_error')
                    if obj.get('target')=='PosicaoAtualizada': observe_snapshot(obj['arguments'][0])
                    if obj.get('target')=='LinhaEsvaziada':
                        with guard: wire['last']=time.time(); wire['lines'][obj['arguments'][0]['codigoLinha']]=[]
        except Exception as error:
            with guard:
                wire['errors']+=1
                wire['last_error_kind']='contract_failure' if isinstance(error,(ValueError,KeyError)) else 'connection_failure'
        finally:
            with guard:wire['connected']=False
            if sock:
                try: send_frame(sock,b'',8)
                except Exception: pass
                sock.close()
        stop.wait(1)

def wire_summary():
    with guard:
        return {**{k:v for k,v in wire.items() if k!='lines'},'connection_state':'connected' if wire['connected'] else 'reconnecting' if wire['connections'] else 'not_connected','lines':{k:fresh(v) for k,v in wire['lines'].items()}}

def parse_logs(text):
    cycles=[]; acquisitions=[]; selection=[]
    for line in text.splitlines():
        if 'Performance GPS:' in line:
            values={k:float(v) for k,v in re.findall(r'([A-Za-z0-9_]+)=([0-9]+(?:[.][0-9]+)?)(?=[,;\s]|$)',line)}
            stamp=re.search(r'inicio=(.*?) fim=',line)
            values.update({k:1 if v.lower()=='true' else 0 for k,v in re.findall(r'(snapshot_sppo_consumido|snapshot_sppo_pendente)=(True|False|true|false)',line)})
            if 'total_ms' in values: cycles.append({'stamp':stamp[1] if stamp else hashlib.sha256(line.encode()).hexdigest(),**values})
        if 'ITS acquisition:' in line or 'ITS_CURRENT acquisition:' in line or ('records=' in line and 'vehicles=' in line):
            acquisitions.append({'stamp':line.split(' ',1)[0],**{k:v for k,v in re.findall(r'([A-Za-z0-9_]+)=([^\s,;]+)',line) if k in ('status','reason','pages','windows','records','rejected','duration_ms','vehicles','recent40','recent60','age_p50','age_p90','zirix_selected','conecta_selected')}})
        if 'BUS fallback' in line: selection.append({'stamp':line.split(' ',1)[0],**{k:v for k,v in re.findall(r'([A-Za-z0-9_]+)=([^\s,;]+)',line) if k in ('primary','status','active_source','healthy_probes','count')}})
        if 'BUS hybrid:' in line:
            selection.append({'stamp':line.split(' ',1)[0],'mode':'Hybrid',**dict(re.findall(r'([A-Za-z0-9_]+)=([^\s,;]+)',line))})
    return cycles,acquisitions,selection

def hybrid_proof(logs):
    return any(x.get('mode')=='Hybrid' and x.get('primary_status')=='Sucesso' and x.get('support_status')=='Sucesso'
               and int(x.get('selected_its','0'))>0 and int(x.get('selected_gtfs','0'))>0 for x in parse_logs(logs)[2])

def sample():
    state=inspect(); api=state[0]
    http,latency=get('/')
    lines={}; controls={}; line_http={}
    for line in LINES+CONTROL_LINES:
        path='/veiculos/linha/'+line
        body,seconds=get(path); payload=json.loads(body)
        (lines if line in LINES else controls)[line]=fresh(payload['posicoes'])
        line_http[line]={'path':path,'seconds':seconds,**payload.get('probe',{'http_status':200,'empty_line':False})}
        line_http[line]['data_state']=data_state((lines if line in LINES else controls)[line])
    stat=[json.loads(x) for x in run(['docker','stats','--no-stream','--format','{{json .}}',*[x['Name'].lstrip('/') for x in state]],timeout=12).splitlines()]
    logs=run(['docker','logs','--timestamps','--since','100s','--tail','6000','noponto_api'],timeout=10)
    cycles,acquisitions,selection=parse_logs(logs)
    memory=dict((x.split(':')[0],int(x.split()[1])) for x in open('/proc/meminfo') if x.startswith(('MemAvailable:','SwapFree:')))
    writers=len(run(['docker','ps','-q','--filter','label=com.docker.compose.project=noponto','--filter','label=com.docker.compose.service=api']).split())
    return {'writer_count':writers,'source':source(api).get('GpsSources__BusPrimarySource'),'fallback':source(api).get('GpsSources__BusFallbackSource',''),
            'support_mode':source(api).get('GpsSources__BusSupportMode','FullFallback'),
            'running':api['State']['Running'],'oom':api['State']['OOMKilled'],'restarts':api['RestartCount'],
            'http_seconds':latency,'lines':lines,'controls':controls,'line_http':line_http,'wire':wire_summary(),'memory_kib':memory,'load':os.getloadavg(),
            'resources':[{k:x[k] for k in ('Name','CPUPerc','MemUsage','MemPerc','PIDs')} for x in stat],
            'cycles':cycles,'acquisitions':acquisitions,'selection':selection,'flow':parse_flow_logs(logs),
            'capture':capture_health(api['Id']) if capture_contract is not None else None,
            'containers':{x['Name'].lstrip('/'):{'started':x['State']['StartedAt'],'running':x['State']['Running'],'health':x['State'].get('Health',{}).get('Status'),'restarts':x['RestartCount']} for x in state}}

def validate(image):
    assert re.fullmatch(r'ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}',image)
    api=inspect()[0]; assert api['Config']['Image']==image
    assert api['Config']['Labels']['com.docker.compose.project']=='noponto'
    assert source(api).get('GpsSources__BusPrimarySource')=='GTFSRT_BUS'
    assert source(api).get('GpsSources__BrtPrimarySource')=='GTFSRT_BRT'
    assert api['HostConfig']['Memory']==805306368 and api['HostConfig']['NanoCpus']==1000000000
    cmd,env=compose(image); original=json.loads(run(cmd+['config','--format','json'],env=env))
    for filename,primary,fallback,mode,trace in [('its.primary.override.yml','ITS_CURRENT','GTFSRT_BUS','Hybrid','true'),('gtfs.rollback.override.yml','GTFSRT_BUS','','FullFallback','false')]:
        cmd,env=compose(image,filename); desired=json.loads(run(cmd+['config','--format','json'],env=env)); normalized=copy.deepcopy(desired)
        e=desired['services']['api']['environment']; assert e['GpsSources__BusPrimarySource']==primary and e['GpsSources__BusFallbackSource']==fallback
        assert e['GpsSources__BusSupportMode']==mode
        assert e['GpsPipelineTrace__Enabled']==trace
        for k in ('GpsSources__BusPrimarySource','GpsSources__BusFallbackSource','GpsSources__BusSupportMode','GpsPipelineTrace__Enabled'):
            old=original['services']['api']['environment']
            if k in old: normalized['services']['api']['environment'][k]=old[k]
            else: normalized['services']['api']['environment'].pop(k,None)
        assert normalized==original
    for k in ('GtfsRealtimeGps__BusEnabled','GtfsRealtimeGps__CrosswalkValidated'): assert source(api).get(k)=='true'

def switch(image,filename):
    cmd,env=compose(image,filename)
    run(cmd+['stop','--timeout','30','api'],timeout=45,env=env)
    remaining=run(['docker','ps','-q','--filter','label=com.docker.compose.project=noponto','--filter','label=com.docker.compose.service=api'])
    if remaining.strip(): raise RuntimeError('old_writer_still_running')
    run(cmd+['up','-d','--no-deps','--no-build','--pull','never','api'],timeout=60,env=env)

def healthy(image,primary):
    end=time.monotonic()+60; good=0
    while time.monotonic()<end:
        try:
            api=inspect()[0]; get('/')
            assert api['Config']['Image']==image and source(api).get('GpsSources__BusPrimarySource')==primary
            assert source(api).get('GpsSources__BusSupportMode','FullFallback')==('Hybrid' if primary=='ITS_CURRENT' else 'FullFallback')
            assert source(api).get('GpsSources__BrtPrimarySource')=='GTFSRT_BRT'
            if primary=='ITS_CURRENT':
                assert hybrid_proof(run(['docker','logs','--timestamps','--since','90s','--tail','4000','noponto_api']))
            assert api['State']['Running'] and not api['State']['OOMKilled'] and api['RestartCount']==0
            good+=1
            if good==3:return
        except Exception:good=0
        time.sleep(2)
    raise RuntimeError('health_not_confirmed')

def take_lock():
    import fcntl
    fd=open(STATE/'deploy.lock','a'); fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB); return fd

def rollback(s,reason):
    s.update(phase='rollback',reason=reason,heartbeat=time.time()); save(s)
    switch(s['image'],'gtfs.rollback.override.yml'); healthy(s['image'],'GTFSRT_BUS')
    s.update(phase='complete',final_source='GTFSRT_BUS',finished=time.time(),heartbeat=time.time()); save(s)
    record('rollback_confirmed',{'reason':reason,'source':'GTFSRT_BUS'})

def decision(current,baseline,seen,long_streak,poor_streak,low_since,watch=None):
    watch=watch if watch is not None else {}
    beta=watch.get('beta',False)
    now=time.time()
    if current.get('writer_count',1)>1:return 'multiple_gps_writers',long_streak,poor_streak,low_since
    if watch.get('capture_required') and not beta:
        captured=current.get('capture') or {}
        families=captured.get('families',{})
        # No normalized page is expected when ITS times out before any response.
        required=('http_page','normalized_page','timing','acquisition','hybrid') if current.get('hybrid_state','healthy')=='healthy' else ()
        if not captured.get('healthy') or any(families.get(k,0)<1 for k in required):
            return 'capture_evidence_failed',long_streak,poor_streak,low_since
    if current['oom'] or current['restarts'] or (not current['running'] and not watch.get('starting')): return 'api_restart_or_oom',long_streak,poor_streak,low_since
    w=current['wire']
    if w['regressions'] or w['duplicates'] or w['invalid']: return 'signalr_gps_integrity',long_streak,poor_streak,low_since
    if baseline['lines']['898']['vehicles']>=5 and watch.get('activated') is not None:
        elapsed=watch.get('activation_elapsed',now-watch['activated'])
        if elapsed<90 and correlation_state(current.get('flow',[]),current['wire'])=='proven':watch['898_proven']=True
        if elapsed>=90 and not watch.get('898_proven') and not beta:return 'gate_898_unproven',long_streak,poor_streak,low_since
    for c in current['cycles']:
        if c['stamp'] in seen:continue
        seen.add(c['stamp']); long_streak=long_streak+1 if c['total_ms']>24000 else 0
        if (c['total_ms']>60000 or long_streak>=3) and not beta:return 'slow_polling',long_streak,poor_streak,low_since
        if c.get('commits_infra',0)>0:return 'cas_infrastructure_failure',long_streak,poor_streak,low_since
        if c.get('snapshot_sppo_pendente',0):watch.setdefault('pending_since',now)
        else:watch.pop('pending_since',None)
    if watch.get('pending_since') and now-watch['pending_since']>90 and not beta:return 'ack_missing',long_streak,poor_streak,low_since
    for selected in current['selection']:
        key='selection:'+selected['stamp']
        if key in seen:continue
        seen.add(key)
        if selected.get('mode')=='Hybrid':
            support_bad=selected.get('support_status') not in ('Sucesso','Vazio')
            watch['support_fail']=watch.get('support_fail',0)+1 if support_bad else 0
            if watch['support_fail']>=3 and not beta:return 'hybrid_support_failed',long_streak,poor_streak,low_since
            both_bad=support_bad and selected.get('primary_status') not in ('Sucesso','Vazio')
            if beta and selected.get('selected') is not None and int(selected.get('recent180',selected['selected']))==0:both_bad=True
            watch['hybrid_both_fail']=watch.get('hybrid_both_fail',0)+1 if both_bad else 0
            if watch['hybrid_both_fail']>=2:return 'both_sources_failed',long_streak,poor_streak,low_since
        if selected.get('active_source')=='ITS_CURRENT':watch.pop('fallback_since',None)
        if selected.get('active_source')=='GTFSRT_BUS' and 'count' in selected:
            watch.setdefault('fallback_since',now)
            watch['both_fail']=watch.get('both_fail',0)+1 if selected.get('status') not in ('Sucesso','Vazio') else 0
            if watch['both_fail']>=2:return 'both_sources_failed',long_streak,poor_streak,low_since
    if watch.get('fallback_since') and now-watch['fallback_since']>300 and not beta:return 'continuous_fallback',long_streak,poor_streak,low_since
    for name,old in baseline['containers'].items():
        if name=='noponto_api':continue
        new=current['containers'].get(name,{})
        if not new.get('running') or new.get('started')!=old['started'] or new.get('restarts')!=old['restarts'] or new.get('health') not in (None,'healthy'):return 'other_service_degradation',long_streak,poor_streak,low_since
    api=next(x for x in current['resources'] if x['Name']=='noponto_api')
    low=float(api['MemPerc'].strip('%'))>90 or current['memory_kib']['MemAvailable']<512*1024
    low_since=(low_since or now) if low else None
    if low_since and now-low_since>60:return 'memory_pressure',long_streak,poor_streak,low_since
    poor=False
    for line in LINES:
        b=baseline['lines'][line]; c=current['lines'][line]
        if b['vehicles']>=5 and c['vehicles']==0:poor=True
        if b['vehicles']>=5 and b['until60']>0 and c['vehicles']>0 and c['older180']==c['vehicles']:poor=True
        if b['vehicles']>=5 and c['vehicles']<b['vehicles']*.7 and c['until60']/max(1,c['vehicles'])<b['until60']/b['vehicles']-.2:poor=True
    if sum(c['older180'] for c in current['lines'].values())>sum(c['vehicles'] for c in current['lines'].values())/2:poor=True
    poor_streak=poor_streak+1 if poor else 0
    if poor_streak>=3 and not beta:return 'coverage_or_freshness_degradation',long_streak,poor_streak,low_since
    return None,long_streak,poor_streak,low_since


class SafetyFailure(RuntimeError):
    def __init__(self,reason):self.reason=reason;super().__init__(reason)
    def evidence(self):return {'type':'SafetyFailure','classification':'safety_gate','reason':self.reason}


def beta_mode(state):
    mode=state.get('experiment_mode','strict')
    if mode not in ('strict','beta_diagnostic'):raise SafetyFailure('experiment_mode_invalid')
    if mode=='beta_diagnostic' and state.get('beta_authorized') is not True:raise SafetyFailure('beta_not_authorized')
    return mode=='beta_diagnostic'


class ActivationDeadline:
    """Main-thread POSIX interrupt. Observer and heartbeat cannot postpone either gate."""
    def __init__(self,require898,beta=False):
        if not all(hasattr(signal,key) for key in ('SIGALRM','ITIMER_REAL','setitimer','getitimer')):
            raise SafetyFailure('deadline_interrupt_unavailable')
        if threading.current_thread() is not threading.main_thread() or signal.getitimer(signal.ITIMER_REAL)!=(0.,0.):
            raise SafetyFailure('deadline_interrupt_unavailable')
        self.require898=require898;self.beta=beta;self.proven=False;self.first_complete_elapsed=None;self.failure=None;self.http_bad_since=None
        self.observation_bad_since=None
        self.startup_deadline=None;self.healthy_seen=False
        self.on_proof=lambda elapsed:None

    def elapsed(self):return time.monotonic()-self.started
    def check(self):
        elapsed=self.elapsed()
        if elapsed>=600:raise SafetyFailure('ten_minutes_completed')
        if self.require898 and not self.proven and elapsed>=90 and not self.beta:raise SafetyFailure('gate_898_unproven')
        if self.failure:raise SafetyFailure(self.failure)
        if self.http_bad_since is not None and time.monotonic()-self.http_bad_since>=30:raise SafetyFailure('backend_unavailable')
        if self.observation_bad_since is not None and time.monotonic()-self.observation_bad_since>=30:raise SafetyFailure('observation_unavailable')
        if self.startup_deadline is not None and not self.healthy_seen and time.monotonic()>=self.startup_deadline and not self.beta:
            raise SafetyFailure('hybrid_startup_unconfirmed')
        if stop.is_set():raise SafetyFailure(stop_reason())

    def trip(self,reason):
        if self.failure is None:self.failure=reason

    def admit(self,current):
        # Called only after a complete REST/trace/client observation. Never accept late proof.
        elapsed=self.elapsed()
        if correlation_state(current.get('flow',[]),current['wire'])=='proven' and self.first_complete_elapsed is None:
            self.first_complete_elapsed=elapsed
        if not self.proven and elapsed<90 and correlation_state(current.get('flow',[]),current['wire'])=='proven':
            self.on_proof(elapsed)
            # Recheck the clock before admission; a delayed callback is not timely proof.
            if self.elapsed()<90:self.proven=True

    def arm(self):
        remaining=min(600-self.elapsed(),90-self.elapsed() if self.require898 and not self.proven and not self.beta else 600)
        if self.http_bad_since is not None:remaining=min(remaining,self.http_bad_since+30-time.monotonic())
        if self.observation_bad_since is not None:remaining=min(remaining,self.observation_bad_since+30-time.monotonic())
        if self.startup_deadline is not None and not self.healthy_seen and not self.beta:remaining=min(remaining,self.startup_deadline-time.monotonic())
        signal.setitimer(signal.ITIMER_REAL,max(.000001,min(.25,remaining)))

    def interrupt(self,*_):
        self.check();self.arm()

    def cancel(self,*_):
        stop.set();raise SafetyFailure(stop_reason())

    def __enter__(self):
        self.previous={key:signal.getsignal(key) for key in (signal.SIGALRM,signal.SIGTERM,signal.SIGINT)}
        self.started=time.monotonic();self.epoch=time.time()
        try:
            signal.signal(signal.SIGALRM,self.interrupt)
            signal.signal(signal.SIGTERM,self.cancel);signal.signal(signal.SIGINT,self.cancel)
            self.arm()
        except Exception:
            for key,handler in self.previous.items():signal.signal(key,handler)
            raise SafetyFailure('deadline_interrupt_unavailable') from None
        return self

    def __exit__(self,*_):
        signal.setitimer(signal.ITIMER_REAL,0)
        for key,handler in self.previous.items():signal.signal(key,handler)


def hybrid_state(current):
    if current.get('source')!='ITS_CURRENT':return 'starting'
    selected=next((x for x in reversed(current['selection']) if x.get('mode')=='Hybrid'),None)
    if selected is None:return 'starting'
    if (selected.get('primary_status')=='Sucesso' and selected.get('support_status')=='Sucesso'
            and int(selected.get('selected_its','0'))>0 and int(selected.get('selected_gtfs','0'))>0):return 'healthy'
    if selected.get('support_status') in ('Sucesso','Vazio') or selected.get('primary_status') in ('Sucesso','Vazio'):return 'degraded'
    return 'failure'


class ActivationObserver:
    """One passive bounded sampler. It never controls Docker or publishes GPS."""
    def __init__(self,safety,baseline):
        self.safety=safety;self.baseline=baseline;self.finished=threading.Event()
        self.thread=threading.Thread(target=self.observe,daemon=True)
        self.seen={c['stamp'] for c in baseline['cycles']};self.long=0;self.poor=0;self.low=None
        self.watch={'activated':safety.epoch,'capture_required':True,'beta':safety.beta}

    def start(self):self.thread.start()
    def stop(self):self.finished.set()  # Never join before rollback.
    def join(self):self.thread.join(1);return not self.thread.is_alive()

    def publish(self,kind,value):
        try:record(kind,value)
        except Exception:self.safety.trip('observation_failed')

    def observe(self):
        while not self.finished.is_set() and not stop.is_set():
            self.observe_once()
            recovery=self.safety.beta and (self.safety.http_bad_since is not None or self.safety.observation_bad_since is not None)
            self.finished.wait(2 if recovery else 15)

    def observe_once(self):
        began=time.monotonic()
        try:
            if self.safety.beta and self.safety.http_bad_since is not None:
                # Verify root recovery before expensive multi-line/stats/log sampling.
                get('/');self.safety.http_bad_since=None
            current=sample()
            if self.finished.is_set():return
            self.safety.http_bad_since=None
            self.safety.observation_bad_since=None
            current['hybrid_state']=hybrid_state(current)
            current['activation_elapsed']=self.safety.elapsed()
            if current['hybrid_state']=='healthy':self.safety.healthy_seen=True
            self.safety.admit(current)
            if self.safety.beta:
                current['beta_diagnostics']={'gate898_within90':self.safety.proven,'first_complete_elapsed':self.safety.first_complete_elapsed,
                    'correlation898':correlation_state(current.get('flow',[]),current['wire']),
                    'capture_healthy':bool((current.get('capture') or {}).get('healthy')),
                    'slow_cycles':sum(c['total_ms']>24000 for c in current['cycles']),
                    'pending_snapshot':any(c.get('snapshot_sppo_pendente',0) for c in current['cycles']),
                    'stale_publications':sum(x['older180'] for x in current['lines'].values())}
            self.watch.update(activation_elapsed=self.safety.elapsed(),starting=current['hybrid_state']=='starting',
                              **{'898_proven':self.safety.proven})
            reason,self.long,self.poor,self.low=decision(current,self.baseline,self.seen,self.long,self.poor,self.low,self.watch)
            if reason:self.safety.trip(reason)
            self.publish('its_sample',current)
        except ProbeError as error:
            field='observation_bad_since' if self.safety.beta and error.path!='/' else 'http_bad_since'
            if getattr(self.safety,field) is None:setattr(self.safety,field,began)
            if error.reason in ('invalid_json','invalid_positions_contract','body_limit'):self.safety.trip('observation_failed')
            self.publish('its_observation_error',{'hybrid_state':'starting' if not self.safety.healthy_seen else 'failure',
                   'activation_elapsed':self.safety.elapsed(),**failure_evidence(error)})
        except Exception as error:
            if self.safety.beta:
                if self.safety.observation_bad_since is None:self.safety.observation_bad_since=began
            else:self.safety.trip('observation_failed')
            self.publish('its_observation_error',{'hybrid_state':'failure',**failure_evidence(error)})


def run_activation(s,baseline,observers):
    if s.get('phase')!='baseline':raise SafetyFailure('activation_phase_invalid')
    if s.get('activated') is not None or 'gate898_proven_elapsed' in s:raise SafetyFailure('activation_state_reused')
    safety=ActivationDeadline(baseline['lines']['898']['vehicles']>=5,beta=beta_mode(s))
    observer=None
    try:
        with safety:
            s.update(phase='its',activated=safety.epoch,deadline=safety.epoch+600,gate898_required=safety.require898);save(s)
            global activation_epoch
            activation_epoch=safety.epoch
            def persist_proof(elapsed):
                # Heartbeat persists this scalar with the state. If the controller dies
                # first, cron sees no proof and rolls back conservatively.
                s['gate898_proven_elapsed']=elapsed
            safety.on_proof=persist_proof
            observer=ActivationObserver(safety,baseline);observers.append(observer);observer.start()
            switch(s['image'],'its.primary.override.yml')
            api=inspect()[0];env=source(api)
            if (api['Config']['Image']!=s['image'] or env.get('GpsSources__BusPrimarySource')!='ITS_CURRENT'
                    or env.get('GpsSources__BusSupportMode')!='Hybrid' or env.get('GpsSources__BrtPrimarySource')!='GTFSRT_BRT'):
                raise SafetyFailure('activation_configuration_mismatch')
            safety.startup_deadline=time.monotonic()+60
            while True:
                safety.check();stop.wait(.25)
    except SafetyFailure as error:
        if error.reason=='ten_minutes_completed':return error.reason
        raise
    finally:
        if observer is not None:
            observer.stop()


def supervise(s):
    global activation_epoch
    if os.getpgrp()!=os.getpid():os.setsid()
    lock=take_lock(); s.update(pid=os.getpid(),pid_start=process_birth(os.getpid()),phase='baseline',heartbeat=time.time(),deadline=time.time()+960); save(s)
    thread=threading.Thread(target=socket_worker,daemon=True); thread.start()
    def beat():
        while not stop.wait(3):
            with guard:
                s['heartbeat']=time.time(); save(s)
    beating=threading.Thread(target=beat,daemon=True); beating.start()
    signal.signal(signal.SIGTERM,lambda *_:stop.set()); signal.signal(signal.SIGINT,lambda *_:stop.set())
    activated=False;observers=[];rollback_attempted=False
    try:
        validate(s['image']); ensure_capture(s)
        ready_deadline=time.monotonic()+5
        while not capture_health(inspect()[0]['Id'])['healthy']:
            if stop.is_set() or time.monotonic()>=ready_deadline:raise CaptureFailure()
            stop.wait(.25)
        baseline_samples=[]; end=time.monotonic()+120
        while time.monotonic()<end and not stop.is_set():
            x=sample(); record('baseline_sample',x)
            if not x['capture']['healthy']:raise CaptureFailure()
            baseline_samples.append(x); stop.wait(15)
        if stop.is_set():raise RuntimeError('stopped_before_activation')
        baseline=baseline_samples[-1]
        if baseline['wire']['messages']==0 or any(line not in baseline['wire']['lines'] for line in LINES):raise RuntimeError('signalr_baseline_not_confirmed')
        if baseline['wire']['regressions'] or baseline['wire']['duplicates'] or baseline['wire']['invalid']:raise RuntimeError('signalr_baseline_integrity')
        validate(s['image'])
        if not capture_health(inspect()[0]['Id'])['healthy']:raise CaptureFailure()
        reason=run_activation(s,baseline,observers)
        rollback_attempted=True;rollback(s,reason)
        for _ in range(3):
            x=sample(); record('final_gtfs_sample',x); time.sleep(5)
    except Exception as error:
        activated=s.get('activated') is not None
        cause=failure_evidence(error)
        reason=error.reason if isinstance(error,SafetyFailure) else stop_reason() if stop.is_set() else 'capture_evidence_failed' if isinstance(error,CaptureFailure) else 'exception_'+type(error).__name__
        if activated and s.get('phase')!='complete':
            try:
                if rollback_attempted:raise error
                rollback_attempted=True;rollback(s,reason)
            except Exception as failure:s.update(phase='rollback_failed',failure=type(failure).__name__,heartbeat=time.time());save(s)
        elif not activated:s.update(phase='precondition_failed',failure=type(error).__name__,heartbeat=time.time());save(s)
        record('supervisor_error',{**cause,'activated':activated,'operational_reason':reason})
    finally:
        safe=s.get('phase') in ('complete','precondition_failed')
        try:
            if safe:stop_capture()
        finally:
            stop.set();lock.close();thread.join(6)
            for observer in observers:
                closed=observer.join()
                record('activation_observer_closed',{'closed':closed,'inflight':not closed})
            if safe:finalize_capture(s)

def tick():
    s=load()
    if s['phase']=='proof':
        chain=[]; p=os.getpid()
        for _ in range(6):
            try:
                chain.append(pathlib.Path('/proc/'+str(p)+'/comm').read_text().strip())
                p=int(next(x.split()[1] for x in pathlib.Path('/proc/'+str(p)+'/status').read_text().splitlines() if x.startswith('PPid:')))
            except Exception:break
        (ROOT/'cron-proof.json').write_text(json.dumps({'time':time.time(),'ancestor_processes':chain,'independent_cron':'cron' in chain}))
        return
    if s['phase']=='scheduled':
        try:supervise(s)
        except BlockingIOError:
            s.update(phase='precondition_failed',failure='deploy_lock_busy');save(s);record('lock_busy',{})
        return
    expired=s['phase'] in ('baseline','its') and time.time()>s['deadline']+5
    proof=s.get('gate898_proven_elapsed')
    explicit_beta=s.get('experiment_mode')=='beta_diagnostic' and s.get('beta_authorized') is True
    gate_expired=(not explicit_beta and s['phase']=='its' and s.get('gate898_required',True) and s.get('activated') is not None
                  and time.time()>=s['activated']+90 and not (type(proof) in (int,float) and 0<=proof<90))
    if s['phase'] in ('baseline','its','rollback','rollback_failed') and (gate_expired or expired or time.time()-s['heartbeat']>45):
        pid=s.get('pid')
        if pid and pathlib.Path('/proc/'+str(pid)+'/stat').exists():
            birth=pathlib.Path('/proc/'+str(pid)+'/stat').read_text().split()[21]
            command=pathlib.Path('/proc/'+str(pid)+'/cmdline').read_bytes()
            if birth==s.get('pid_start') and str(pathlib.Path(__file__).resolve()).encode() in command:
                os.killpg(pid,signal.SIGTERM); time.sleep(10)
                if load()['phase']=='complete':return
                try:os.killpg(pid,signal.SIGKILL)
                except ProcessLookupError:pass
        safe=False
        try:
            lock=take_lock()
            try:rollback(s,'gate_898_unproven' if gate_expired else 'independent_cron_watchdog');safe=True
            finally:lock.close()
        except Exception as error:record('watchdog_retry_required',{'type':type(error).__name__})
        if safe:
            try:stop_orphan_capture(s);finalize_capture(s)
            except Exception as error:record('capture_cleanup_failed',{'type':type(error).__name__})

if __name__=='__main__':
    os.umask(0o077)
    if sys.argv[1:]==['--tick']:tick()
    elif len(sys.argv)==3 and sys.argv[1]=='--request-stop':request_stop(sys.argv[2])
    elif sys.argv[1:]==['--probe-signalr']:
        t=threading.Thread(target=socket_worker,daemon=True);t.start();time.sleep(40);print(json.dumps(wire_summary()));stop.set();t.join(5)
    elif sys.argv[1:]==['--validate']:validate(load()['image']);print('preflight_ok')
    else:raise SystemExit(2)
