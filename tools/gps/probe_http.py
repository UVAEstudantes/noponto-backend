"""Bounded loopback observer HTTP. Only the controller's exact empty-line 404 is empty."""
import json
import re
import time
import urllib.error
import urllib.request


class ProbeError(RuntimeError):
    def __init__(self, path, status, reason):
        self.path, self.status, self.reason = path, status, reason
        super().__init__(f"probe:{reason}:HTTP={status}:path={path}")

    def evidence(self):
        return {"type": type(self).__name__, "path": self.path,
                "http_status": self.status, "reason": self.reason}


def get(path):
    if path != "/" and not re.fullmatch(r"/veiculos/linha/[A-Za-z0-9_-]{1,32}", path):
        raise ProbeError("<invalid>", None, "path_not_allowed")
    started = time.monotonic()
    status = None
    try:
        try:
            response = urllib.request.urlopen("http://127.0.0.1:8080" + path, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status = response.code
            body = response.read(1_000_001)
        if len(body) > 1_000_000:
            raise ProbeError(path, status, "body_limit")
        line = path.removeprefix("/veiculos/linha/") if path != "/" else None
        payload = None
        if line:
            try:
                payload = json.loads(body)
            except (ValueError, UnicodeError):
                raise ProbeError(path, status, "invalid_json") from None
        if status == 404 and line and payload == {"mensagem": f"Nenhum veículo ativo para a linha {line}."}:
            body = json.dumps({"posicoes": [], "probe": {"http_status": 404, "empty_line": True}}).encode()
        elif status != 200:
            raise ProbeError(path, status, "http_failure")
        elif line and (not isinstance(payload, dict) or not isinstance(payload.get("posicoes"), list)):
            raise ProbeError(path, status, "invalid_positions_contract")
        return body, time.monotonic() - started
    except ProbeError:
        raise
    except (OSError, TimeoutError) as error:
        cause = error.reason if isinstance(error, urllib.error.URLError) else error
        raise ProbeError(path, status, "timeout" if isinstance(cause, TimeoutError) else "transport_failure") from None
