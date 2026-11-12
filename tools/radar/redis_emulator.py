"""Redis efêmero emulado para testes N4-A; requer fakeredis[lua]==2.40.0.

Não substitui homologação com Redis real. Somente RESP2/loopback, sem persistência,
sem configuração de produção e sem logs de comandos ou dados. Ctrl+C descarta tudo.
"""

import argparse
import socketserver

import fakeredis
from redis.exceptions import ResponseError


class Handler(socketserver.StreamRequestHandler):
    def write_response(self, value):
        if value is None:
            self.wfile.write(b"$-1\r\n")
        elif isinstance(value, (bytes, str, float)):
            data = value if isinstance(value, bytes) else str(value).encode()
            self.wfile.write(b"$" + str(len(data)).encode() + b"\r\n" + data + b"\r\n")
        elif isinstance(value, int):
            self.wfile.write(b":" + str(value).encode() + b"\r\n")
        elif isinstance(value, (list, tuple)):
            self.wfile.write(b"*" + str(len(value)).encode() + b"\r\n")
            for item in value:
                self.write_response(item)
        else:
            raise TypeError("Resposta não suportada no emulador de teste")

    def handle(self):
        client = fakeredis.FakeRedis(server=self.server.fake)
        connection = client.connection_pool.get_connection()
        try:
            while line := self.rfile.readline():
                if not line.startswith(b"*"):
                    return
                count = int(line[1:])
                if not 0 < count <= 128:
                    return
                args = []
                for _ in range(count):
                    size = int(self.rfile.readline()[1:])
                    if not 0 <= size <= 1_048_576:
                        return
                    args.append(self.rfile.read(size))
                    self.rfile.read(2)
                try:
                    name = args[0].upper()
                    if name == b"PING":
                        self.wfile.write(b"+PONG\r\n")
                    else:
                        connection.send_command(*args)
                        result = connection.read_response()
                        if name in (b"SET", b"SETEX", b"PSETEX", b"SELECT", b"CLIENT") and result is not None:
                            self.wfile.write(b"+OK\r\n")
                        else:
                            self.write_response(result)
                except ResponseError:
                    # A mensagem pode incorporar argumentos; não registrar nem retransmitir.
                    self.wfile.write(b"-ERR test emulator command failed\r\n")
                self.wfile.flush()
        finally:
            connection.disconnect()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=6397)
    args = parser.parse_args()
    with Server(("127.0.0.1", args.port), Handler) as server:
        server.fake = fakeredis.FakeServer()
        print(f"Redis EMULADO descartável em 127.0.0.1:{args.port}", flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
