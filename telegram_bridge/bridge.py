"""Experimental loopback MTProxy-to-Telegram WSS transport bridge.

No account data, payloads, headers, keys, addresses or exception strings are logged.
Only the transport obfuscation layer is translated; MTProto account encryption is
unchanged. A 16-byte NEW local secret must be supplied by a launcher through stdin.
No secret generation/persistence, proxy-setting changes or background installation.
Preview port defaults to 1443; 19443 is reserved for isolated synthetic smoke tests.

Protocol references (independent implementation, reviewed 2026-09-29):
https://core.telegram.org/mtproto/mtproto-transports#transport-obfuscation
https://core.telegram.org/mtproto/transports#websocket
https://www.rfc-editor.org/rfc/rfc6455
DC2/4 endpoint mapping is an explicitly pinned experiment, not a stability promise.
"""
import argparse
import asyncio
import base64
import hashlib
import ipaddress
import json
import re
import secrets
import socket
import ssl
import struct
import sys
import time

try:
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
except ImportError:
    print('{"event":"fatal","code":"dependency_unavailable"}', flush=True)
    raise SystemExit(2) from None

MAX_FRAME = 1024 * 1024
CHUNK = 65536
HANDSHAKE_TIMEOUT = 8
IDLE_TIMEOUT = 300
GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
NAMES = ("pluto", "kws2", "aurora", "kws4", "flora")
MARKERS = (b"\xef" * 4, b"\xee" * 4, b"\xdd" * 4)
FORBIDDEN = {b"HEAD", b"POST", b"GET ", b"OPTI", b"PUT ", b"DELE",
             b"TRAC", b"CONN", b"PATC", b"\x16\x03\x01\x02", *MARKERS[1:]}


class Rejected(Exception):
    """Only fixed, non-sensitive error codes may be used."""


def event(code, dc=None, connected=None):
    names = {"listening": "listening", "transport_open": "upstream_connected",
             "client_header_rejected": "client_rejected", "connection_limit": "client_rejected",
             "connection_closed": "connection_closed", "stopped": "connection_closed",
             "startup_rejected": "fatal"}
    value = {"event": names.get(code, "connection_failed"), "code": code}
    if dc is not None:
        value["dc"] = abs(dc)
        value["media"] = dc < 0
    if connected is not None:
        value["connected"] = connected
    print(json.dumps(value), flush=True)


def parse_secret(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-fA-F0-9]{32}", value):
        raise Rejected("secret_invalid")
    return bytes.fromhex(value)


def ctr(key, iv):
    return Cipher(algorithms.AES(key), modes.CTR(iv)).encryptor()


def valid_seed(seed):
    return (len(seed) == 64 and seed[0] != 0xef and
            seed[:4] not in FORBIDDEN and seed[4:8] != bytes(4))


def accept_client(wire, secret):
    if len(secret) != 16 or not valid_seed(wire):
        raise Rejected("client_header_rejected")
    reverse = wire[::-1]
    incoming = ctr(hashlib.sha256(wire[8:40] + secret).digest(), wire[40:56])
    outgoing = ctr(hashlib.sha256(reverse[8:40] + secret).digest(), reverse[40:56])
    # Consume exactly 64 incoming CTR bytes, but only the last 8 are encrypted.
    tail = incoming.update(wire)[56:64]
    marker, dc = tail[:4], struct.unpack("<h", tail[4:6])[0]
    if marker not in MARKERS or not 1 <= abs(dc) <= 5:
        raise Rejected("client_header_rejected")
    return marker, dc, incoming, outgoing


def upstream_obfuscation(marker, seed=None):
    if marker not in MARKERS:
        raise Rejected("transport_rejected")
    if seed is None:
        seed = secrets.token_bytes(64)
        while not valid_seed(seed):
            seed = secrets.token_bytes(64)
    if not valid_seed(seed):
        raise Rejected("initialization_rejected")
    initial = seed[:56] + marker + seed[60:64]  # Random tail, NO proxy DC field.
    reverse = initial[::-1]
    outgoing = ctr(initial[8:40], initial[40:56])
    incoming = ctr(reverse[8:40], reverse[40:56])
    encrypted = outgoing.update(initial)
    return initial[:56] + encrypted[56:64], outgoing, incoming


def endpoint_specs(dc):
    if not 1 <= abs(dc) <= 5:
        raise Rejected("dc_rejected")
    name = NAMES[abs(dc) - 1]
    address = "149.154.167.220" if abs(dc) in (2, 4) else None
    names = (name + "-1", name) if dc < 0 else (name,)
    return tuple((n + ".web.telegram.org", address) for n in names)


def verify_upgrade(raw, key):
    if len(raw) > 8192 or not raw.endswith(b"\r\n\r\n"):
        raise Rejected("wss_upgrade_rejected")
    lines = raw.decode("iso-8859-1").split("\r\n")
    if not re.fullmatch(r"HTTP/1\.1 101(?: [\x20-\x7e]*)?", lines[0]):
        raise Rejected("wss_upgrade_rejected")
    headers = {}
    for line in lines[1:-2]:
        name, separator, value = line.partition(":")
        if not separator or not re.fullmatch(r"[!#$%&'*+.^_`|~0-9A-Za-z-]+", name):
            raise Rejected("wss_upgrade_rejected")
        name = name.lower()
        if name in headers or any(ord(c) < 32 and c != "\t" for c in value):
            raise Rejected("wss_upgrade_rejected")
        headers[name] = value.strip()
    expected = base64.b64encode(hashlib.sha1((key + GUID).encode("ascii")).digest()).decode("ascii")
    if (headers.get("sec-websocket-accept") != expected or
            headers.get("upgrade", "").lower() != "websocket" or
            "upgrade" not in headers.get("connection", "").lower().replace(" ", "").split(",") or
            headers.get("sec-websocket-protocol") != "binary" or
            "sec-websocket-extensions" in headers or
            "transfer-encoding" in headers or
            headers.get("content-length", "0") != "0"):
        raise Rejected("wss_upgrade_rejected")


def masked_frame(payload, opcode=2, mask=None):
    length = len(payload)
    if opcode not in (2, 8, 10) or length > MAX_FRAME or (opcode >= 8 and length > 125):
        raise Rejected("frame_rejected")
    mask = secrets.token_bytes(4) if mask is None else mask
    if len(mask) != 4:
        raise Rejected("frame_rejected")
    header = bytes([0x80 | opcode])
    header += (bytes([length | 0x80]) if length < 126 else
               b"\xfe" + struct.pack("!H", length) if length <= 65535 else
               b"\xff" + struct.pack("!Q", length))
    return header + mask + bytes(value ^ mask[i % 4] for i, value in enumerate(payload))


async def frame_header(reader):
    first, second = await reader.readexactly(2)
    opcode, final, length = first & 15, bool(first & 128), second & 127
    if first & 0x70 or second & 128 or opcode not in (0, 2, 8, 9, 10):
        raise Rejected("upstream_frame_rejected")
    if opcode >= 8 and (not final or length > 125):
        raise Rejected("upstream_frame_rejected")
    if length in (126, 127):
        size = 2 if length == 126 else 8
        expanded = int.from_bytes(await reader.readexactly(size), "big")
        if expanded < (126 if size == 2 else 65536):
            raise Rejected("upstream_frame_rejected")
        length = expanded
    if length > MAX_FRAME:
        raise Rejected("upstream_frame_rejected")
    return opcode, final, length


async def close_writer(writer):
    if writer is not None:
        writer.close()
        try:
            await asyncio.wait_for(writer.wait_closed(), 2)
        except (OSError, TimeoutError):
            pass


async def open_wss(host, fixed_address, binding):
    # Host and fixed_address can only originate in endpoint_specs, never the client.
    loop = asyncio.get_running_loop()
    if fixed_address is None:
        answers = await loop.getaddrinfo(host, 443, family=socket.AF_INET, type=socket.SOCK_STREAM)
        addresses = [x[4][0] for x in answers if ipaddress.ip_address(x[4][0]).is_global]
        if not addresses:
            raise Rejected("dns_rejected")
        fixed_address = addresses[0]
    raw = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    writer = None
    try:
        raw.setblocking(False)
        if binding is not None:
            index, source = binding
            raw.setsockopt(socket.IPPROTO_IP, 31, struct.pack("!I", index))
            raw.bind((source, 0))
            if raw.getsockopt(socket.IPPROTO_IP, 31) != index or raw.getsockname()[0] != source:
                raise Rejected("binding_rejected")
        await loop.sock_connect(raw, (fixed_address, 443))
        # Explicit context does not honor SSLKEYLOGFILE inherited from the host.
        # Never enable TLS session-key logging, even for this local module.
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.load_default_certs()
        context.set_alpn_protocols(["http/1.1"])
        reader, writer = await asyncio.open_connection(sock=raw, ssl=context, server_hostname=host,
                                                       ssl_handshake_timeout=HANDSHAKE_TIMEOUT, limit=CHUNK)
        raw = None  # Stream now owns socket.
        key = base64.b64encode(secrets.token_bytes(16)).decode("ascii")
        request = ("GET /apiws HTTP/1.1\r\nHost: " + host +
                   "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13"
                   "\r\nSec-WebSocket-Protocol: binary\r\nSec-WebSocket-Key: " + key +
                   "\r\nOrigin: https://web.telegram.org\r\n\r\n")
        writer.write(request.encode("ascii"))
        await writer.drain()
        # Bounded one-byte header reader retains already-buffered WS payload bytes.
        header = bytearray()
        while not header.endswith(b"\r\n\r\n"):
            if len(header) >= 8192:
                raise Rejected("wss_upgrade_rejected")
            header.extend(await reader.readexactly(1))
        verify_upgrade(bytes(header), key)
        return reader, writer
    except BaseException:
        await close_writer(writer)
        raise
    finally:
        if raw is not None:
            raw.close()


async def connect_upstream(dc, binding):
    specs = endpoint_specs(dc)
    for host, address in specs:
        try:
            return await asyncio.wait_for(open_wss(host, address, binding), HANDSHAKE_TIMEOUT / len(specs))
        except (OSError, Rejected, asyncio.IncompleteReadError, TimeoutError):
            continue  # Only media -1 to same-DC base, before sending any stream bytes.
    raise Rejected("upstream_unavailable")


async def relay(client_reader, client_writer, up_reader, up_writer, incoming, outgoing, up_tx, up_rx):
    last_activity = time.monotonic()
    lock = asyncio.Lock()

    async def send(payload, opcode=2):
        async with lock:
            up_writer.write(masked_frame(payload, opcode))
            await up_writer.drain()

    async def client_to_upstream():
        nonlocal last_activity
        while True:
            data = await client_reader.read(CHUNK)
            if not data:
                return
            last_activity = time.monotonic()
            await send(up_tx.update(incoming.update(data)))

    async def upstream_to_client():
        nonlocal last_activity
        fragmented, controls = False, 0
        while True:
            opcode, final, length = await frame_header(up_reader)
            last_activity = time.monotonic()
            if opcode >= 8:
                controls += 1
                if controls > 64:
                    raise Rejected("control_limit")
                payload = await up_reader.readexactly(length)
                if opcode == 8:
                    if len(payload) == 1:
                        raise Rejected("upstream_frame_rejected")
                    if len(payload) >= 2:
                        code = int.from_bytes(payload[:2], "big")
                        if code not in (1000, 1001, 1002, 1003, 1007, 1008, 1009, 1010, 1011, 1012, 1013, 1014) and not 3000 <= code <= 4999:
                            raise Rejected("upstream_frame_rejected")
                        try:
                            payload[2:].decode("utf-8", "strict")
                        except UnicodeError:
                            raise Rejected("upstream_frame_rejected") from None
                    await send(payload, 8)
                    return
                if opcode == 9:
                    await send(payload, 10)
                continue
            if (opcode == 0 and not fragmented) or (opcode == 2 and fragmented):
                raise Rejected("upstream_frame_rejected")
            fragmented = not final
            controls = 0
            while length:
                payload = await up_reader.readexactly(min(length, CHUNK))
                length -= len(payload)
                last_activity = time.monotonic()
                client_writer.write(outgoing.update(up_rx.update(payload)))
                await client_writer.drain()

    async def watchdog():
        while True:
            await asyncio.sleep(max(1, IDLE_TIMEOUT - (time.monotonic() - last_activity)))
            if time.monotonic() - last_activity >= IDLE_TIMEOUT:
                raise Rejected("idle_timeout")

    tasks = [asyncio.create_task(fn()) for fn in (client_to_upstream, upstream_to_client, watchdog)]
    try:
        completed, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in completed:
            task.result()
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


async def serve(secret, port, binding):
    active = set()
    connected = 0

    async def connection(reader, writer):
        nonlocal connected
        task = asyncio.current_task()
        if len(active) >= 8:
            event("connection_limit", connected=connected)
            await close_writer(writer)
            return
        active.add(task)
        up_writer, dc, transport_opened = None, None, False
        try:
            wire = await asyncio.wait_for(reader.readexactly(64), HANDSHAKE_TIMEOUT)
            marker, dc, incoming, outgoing = accept_client(wire, secret)
            up_reader, up_writer = await asyncio.wait_for(connect_upstream(dc, binding), HANDSHAKE_TIMEOUT)
            initial, up_tx, up_rx = upstream_obfuscation(marker)
            up_writer.write(masked_frame(initial))
            await asyncio.wait_for(up_writer.drain(), HANDSHAKE_TIMEOUT)
            transport_opened = True
            connected += 1
            event("transport_open", dc, connected)
            await relay(reader, writer, up_reader, up_writer, incoming, outgoing, up_tx, up_rx)
        except Rejected as exc:
            event(str(exc), dc, connected)
        except TimeoutError:
            event("handshake_timeout", dc, connected)
        except (OSError, asyncio.IncompleteReadError):
            if not transport_opened:
                event("connection_closed", dc, connected)
        except Exception:
            event("internal_failure", dc, connected)
        finally:
            try:
                await close_writer(up_writer)
            finally:
                try:
                    await close_writer(writer)
                finally:
                    active.discard(task)
                    if transport_opened:
                        connected -= 1
                        # Every opened stream reports its end, including normal
                        # client EOF and a WebSocket close with no exception.
                        # Only the aggregate is exposed; no connection IDs.
                        event("connection_closed", dc, connected)

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if sys.platform == "win32":
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind(("127.0.0.1", port))
        listener.listen(8)
        listener.setblocking(False)
        server = await asyncio.start_server(connection, sock=listener, limit=CHUNK, backlog=8)
    except BaseException:
        listener.close()
        raise
    event("listening", connected=connected)
    try:
        async with server:
            await server.serve_forever()
    finally:
        for task in tuple(active):
            task.cancel()
        await asyncio.gather(*tuple(active), return_exceptions=True)


def main():
    parser = argparse.ArgumentParser(description="Experimental loopback MTProxy to strict Telegram WSS")
    parser.add_argument("--secret-stdin", action="store_true", required=True)
    parser.add_argument("--port", type=int, choices=(1443, 19443), default=1443)
    parser.add_argument("--ifindex", type=int)
    parser.add_argument("--source")
    args = parser.parse_args()
    binding = None
    try:
        if sys.stdin.isatty():
            raise Rejected("secret_pipe_required")
        secret = parse_secret(sys.stdin.readline(66).rstrip("\r\n"))
        if args.ifindex is not None or args.source is not None:
            source = ipaddress.ip_address(args.source)
            if (sys.platform != "win32" or args.ifindex is None or not 1 <= args.ifindex <= 65535 or
                    source.version != 4 or source.is_loopback or source.is_unspecified or source.is_multicast):
                raise Rejected("binding_rejected")
            binding = (args.ifindex, str(source))
        asyncio.run(serve(secret, args.port, binding))
    except (Rejected, ValueError, TypeError, OSError):
        event("startup_rejected")
        return 2
    except KeyboardInterrupt:
        event("stopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
