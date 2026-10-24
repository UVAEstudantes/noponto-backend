"""Run locally or bundle with its_window/probe_http over SSH stdin. Never arms a window."""
import datetime
import json
import statistics
import threading
import time
import its_window as window

window.LINES = ('838', '898', '006', '457', '483', '355', '805')
window.CONTROL_LINES = ('35', '60', '67')
original_fresh = window.fresh


def fresh(positions):
    result = original_fresh(positions)
    now = time.time()
    ages = sorted(now - datetime.datetime.fromisoformat(p['timestampGps'].replace('Z', '+00:00')).timestamp() for p in positions)
    result.update(median=statistics.median(ages) if ages else None,
                  p90=ages[max(0, window.math.ceil(len(ages) * .9) - 1)] if ages else None)
    return result


window.fresh = fresh
api = window.inspect()[0]
settings = window.source(api)
assert settings.get('GpsSources__BusPrimarySource') == 'GTFSRT_BUS'
assert settings.get('GpsSources__BrtPrimarySource') == 'GTFSRT_BRT'
thread = threading.Thread(target=window.socket_worker, daemon=True)
thread.start()
try:
    for index in range(6):
        started = time.monotonic()
        result = window.sample()
        assert result['source'] == 'GTFSRT_BUS'
        print(json.dumps({'round': index, 'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), **result}), flush=True)
        if index < 5:
            window.stop.wait(max(1, 45 - (time.monotonic() - started)))
finally:
    window.stop.set()
    thread.join(6)
