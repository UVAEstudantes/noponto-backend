"""Pontes locais do laboratório Android; não iniciam Metro/API e não registram tráfego."""
import asyncio

capacity = asyncio.Semaphore(32)


async def bridge(reader, writer, target, port):
    peer = None
    try:
        if capacity.locked():
            return
        async with capacity:
            upstream, peer = await asyncio.wait_for(asyncio.open_connection(target, port), 5)

            async def copy(source, destination):
                while data := await source.read(16384):
                    destination.write(data)
                    await destination.drain()
                if destination.can_write_eof():
                    destination.write_eof()

            tasks = [asyncio.create_task(copy(reader, peer)), asyncio.create_task(copy(upstream, writer))]
            try:
                await asyncio.gather(*tasks)
            finally:
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
    except (OSError, TimeoutError):
        pass
    finally:
        writer.close()
        if peer is not None:
            peer.close()


async def main():
    # IPv4 do QR/ADB -> Metro IPv6 existente; IPv6 -> API IPv4 existente.
    metro = await asyncio.start_server(lambda r, w: bridge(r, w, "::1", 8084), "127.0.0.1", 8084, backlog=16)
    api = await asyncio.start_server(lambda r, w: bridge(r, w, "127.0.0.1", 8083), "::1", 8083, backlog=16)
    async with metro, api:
        print("Pontes loopback Android ativas; nenhum tráfego é registrado.", flush=True)
        await asyncio.gather(metro.serve_forever(), api.serve_forever())


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
