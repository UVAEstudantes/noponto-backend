"""Manual controlled-window supervisor. Only --tick reads an explicitly prepared state.
No raw positions, identities, configuration or provider exceptions are written to output.
"""
import base64, copy, datetime as dt, hashlib, json, math, os, pathlib, re, signal, socket, statistics, struct, subprocess, sys, threading, time, urllib.request
from probe_http import get, ProbeError

ROOT = pathlib.Path(__file__).resolve().parent
STATE = pathlib.Path('/home/guilherme/.noponto-deploy')
LINES = ('838', '898', '006', '457', '483', '355', '805')
CONTROL_LINES = ('35', '60', '67')
stop = threading.Event()
guard = threading.Lock()
state_guard = threading.Lock()
wire = {'messages':0, 'duplicates':0, 'regressions':0, 'invalid':0, 'connections':0, 'errors':0, 'last':0, 'lines':{}, 'connected':False, 'last_error_kind':None}
last_gps = {}
activation_epoch = None

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
    return error.evidence() if isinstance(error,(ProbeError,CommandFailure)) else {'type':type(error).__name__,'reason':'unclassified_failure'}

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
    return {'source':source(api).get('GpsSources__BusPrimarySource'),'fallback':source(api).get('GpsSources__BusFallbackSource',''),
            'support_mode':source(api).get('GpsSources__BusSupportMode','FullFallback'),
            'running':api['State']['Running'],'oom':api['State']['OOMKilled'],'restarts':api['RestartCount'],
            'http_seconds':latency,'lines':lines,'controls':controls,'line_http':line_http,'wire':wire_summary(),'memory_kib':memory,'load':os.getloadavg(),
            'resources':[{k:x[k] for k in ('Name','CPUPerc','MemUsage','MemPerc','PIDs')} for x in stat],
            'cycles':cycles,'acquisitions':acquisitions,'selection':selection,'flow':parse_flow_logs(logs),
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
    now=time.time()
    if current['oom'] or current['restarts'] or not current['running']: return 'api_restart_or_oom',long_streak,poor_streak,low_since
    w=current['wire']
    if w['regressions'] or w['duplicates'] or w['invalid']: return 'signalr_gps_integrity',long_streak,poor_streak,low_since
    if baseline['lines']['898']['vehicles']>=5 and watch.get('activated') is not None:
        if now-watch['activated']<=90 and correlation_state(current.get('flow',[]),current['wire'])=='proven':watch['898_proven']=True
        if now-watch['activated']>=90 and not watch.get('898_proven'):return 'gate_898_unproven',long_streak,poor_streak,low_since
    for c in current['cycles']:
        if c['stamp'] in seen:continue
        seen.add(c['stamp']); long_streak=long_streak+1 if c['total_ms']>24000 else 0
        if c['total_ms']>60000 or long_streak>=3:return 'slow_polling',long_streak,poor_streak,low_since
        if c.get('commits_infra',0)>0:return 'cas_infrastructure_failure',long_streak,poor_streak,low_since
        if c.get('snapshot_sppo_pendente',0):watch.setdefault('pending_since',now)
        else:watch.pop('pending_since',None)
    if watch.get('pending_since') and now-watch['pending_since']>90:return 'ack_missing',long_streak,poor_streak,low_since
    for selected in current['selection']:
        key='selection:'+selected['stamp']
        if key in seen:continue
        seen.add(key)
        if selected.get('mode')=='Hybrid':
            support_bad=selected.get('support_status') not in ('Sucesso','Vazio')
            watch['support_fail']=watch.get('support_fail',0)+1 if support_bad else 0
            if watch['support_fail']>=3:return 'hybrid_support_failed',long_streak,poor_streak,low_since
            both_bad=support_bad and selected.get('primary_status') not in ('Sucesso','Vazio')
            watch['hybrid_both_fail']=watch.get('hybrid_both_fail',0)+1 if both_bad else 0
            if watch['hybrid_both_fail']>=2:return 'both_sources_failed',long_streak,poor_streak,low_since
        if selected.get('active_source')=='ITS_CURRENT':watch.pop('fallback_since',None)
        if selected.get('active_source')=='GTFSRT_BUS' and 'count' in selected:
            watch.setdefault('fallback_since',now)
            watch['both_fail']=watch.get('both_fail',0)+1 if selected.get('status') not in ('Sucesso','Vazio') else 0
            if watch['both_fail']>=2:return 'both_sources_failed',long_streak,poor_streak,low_since
    if watch.get('fallback_since') and now-watch['fallback_since']>300:return 'continuous_fallback',long_streak,poor_streak,low_since
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
    if poor_streak>=3:return 'coverage_or_freshness_degradation',long_streak,poor_streak,low_since
    return None,long_streak,poor_streak,low_since

def supervise(s):
    global activation_epoch
    if os.getpgrp()!=os.getpid():os.setsid()
    lock=take_lock(); s.update(pid=os.getpid(),pid_start=pathlib.Path('/proc/self/stat').read_text().split()[21],phase='baseline',heartbeat=time.time(),deadline=time.time()+960); save(s)
    thread=threading.Thread(target=socket_worker,daemon=True); thread.start()
    def beat():
        while not stop.wait(3):
            with guard:
                s['heartbeat']=time.time(); save(s)
    beating=threading.Thread(target=beat,daemon=True); beating.start()
    signal.signal(signal.SIGTERM,lambda *_:stop.set()); signal.signal(signal.SIGINT,lambda *_:stop.set())
    activated=False
    try:
        validate(s['image']); baseline_samples=[]; end=time.monotonic()+120
        while time.monotonic()<end and not stop.is_set():
            x=sample(); record('baseline_sample',x); baseline_samples.append(x); stop.wait(15)
        if stop.is_set():raise RuntimeError('stopped_before_activation')
        baseline=baseline_samples[-1]
        if baseline['wire']['messages']==0 or any(line not in baseline['wire']['lines'] for line in LINES):raise RuntimeError('signalr_baseline_not_confirmed')
        if baseline['wire']['regressions'] or baseline['wire']['duplicates'] or baseline['wire']['invalid']:raise RuntimeError('signalr_baseline_integrity')
        validate(s['image']); s.update(phase='its',activated=time.time(),deadline=time.time()+600); save(s); activated=True
        activation_epoch=s['activated']
        switch(s['image'],'its.primary.override.yml'); healthy(s['image'],'ITS_CURRENT')
        seen={c['stamp'] for x in baseline_samples for c in x['cycles']}; long_streak=poor_streak=0; low_since=None; reason='ten_minutes_completed';watch={'activated':s['activated']}
        while time.time()<s['deadline'] and not stop.is_set():
            if baseline['lines']['898']['vehicles']>=5 and not watch.get('898_proven') and time.time()-s['activated']>=90:
                reason='gate_898_unproven';break
            x=sample(); record('its_sample',x)
            reason_now,long_streak,poor_streak,low_since=decision(x,baseline,seen,long_streak,poor_streak,low_since,watch)
            if reason_now:reason=reason_now;break
            delay=min(15,max(0,s['deadline']-time.time()))
            if baseline['lines']['898']['vehicles']>=5 and not watch.get('898_proven'):
                delay=min(delay,max(0,s['activated']+90-time.time()))
            stop.wait(delay)
        if stop.is_set():reason=stop_reason()
        rollback(s,reason)
        for _ in range(3):
            x=sample(); record('final_gtfs_sample',x); time.sleep(5)
    except Exception as error:
        cause=failure_evidence(error)
        reason=stop_reason() if stop.is_set() else 'exception_'+type(error).__name__
        record('supervisor_error',{**cause,'activated':activated,'operational_reason':reason})
        if activated:
            try:rollback(s,reason)
            except Exception as failure:s.update(phase='rollback_failed',failure=type(failure).__name__,heartbeat=time.time());save(s)
        else:s.update(phase='precondition_failed',failure=type(error).__name__,heartbeat=time.time());save(s)
    finally:stop.set();thread.join(6);lock.close()

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
    if s['phase'] in ('baseline','its','rollback','rollback_failed') and (expired or time.time()-s['heartbeat']>45):
        pid=s.get('pid')
        if pid and pathlib.Path('/proc/'+str(pid)+'/stat').exists():
            birth=pathlib.Path('/proc/'+str(pid)+'/stat').read_text().split()[21]
            command=pathlib.Path('/proc/'+str(pid)+'/cmdline').read_bytes()
            if birth==s.get('pid_start') and str(pathlib.Path(__file__).resolve()).encode() in command:
                os.killpg(pid,signal.SIGTERM); time.sleep(10)
                if load()['phase']=='complete':return
                try:os.killpg(pid,signal.SIGKILL)
                except ProcessLookupError:pass
        try:
            lock=take_lock()
            try:rollback(s,'independent_cron_watchdog')
            finally:lock.close()
        except Exception as error:record('watchdog_retry_required',{'type':type(error).__name__})

if __name__=='__main__':
    os.umask(0o077)
    if sys.argv[1:]==['--tick']:tick()
    elif len(sys.argv)==3 and sys.argv[1]=='--request-stop':request_stop(sys.argv[2])
    elif sys.argv[1:]==['--probe-signalr']:
        t=threading.Thread(target=socket_worker,daemon=True);t.start();time.sleep(40);print(json.dumps(wire_summary()));stop.set();t.join(5)
    elif sys.argv[1:]==['--validate']:validate(load()['image']);print('preflight_ok')
    else:raise SystemExit(2)
