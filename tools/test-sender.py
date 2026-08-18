#!/usr/bin/env python3
"""Minimal CASTV2 sender used as an end-to-end smoke test for the emulator.

Speaks the protocol directly with a hand-rolled protobuf codec so it has no
dependencies, and skips device authentication the way a custom sender or an SDK
developer mode would.

    python3 tools/test-sender.py [host] [port]
"""

import json
import socket
import ssl
import struct
import sys
import time

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8009

NS_CONNECTION = "urn:x-cast:com.google.cast.tp.connection"
NS_HEARTBEAT = "urn:x-cast:com.google.cast.tp.heartbeat"
NS_AUTH = "urn:x-cast:com.google.cast.tp.deviceauth"
NS_RECEIVER = "urn:x-cast:com.google.cast.receiver"
NS_MEDIA = "urn:x-cast:com.google.cast.media"

SENDER = "sender-0"
PLATFORM = "receiver-0"

failures = []


def varint(value):
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | (0x80 if value else 0))
        if not value:
            return bytes(out)


def field(number, wire, payload):
    tag = varint((number << 3) | wire)
    return tag + (varint(len(payload)) + payload if wire == 2 else payload)


def encode(source, destination, namespace, payload, binary=False):
    msg = b""
    msg += field(1, 0, varint(0))  # protocol_version CASTV2_1_0
    msg += field(2, 2, source.encode())
    msg += field(3, 2, destination.encode())
    msg += field(4, 2, namespace.encode())
    msg += field(5, 0, varint(1 if binary else 0))  # payload_type
    msg += field(7 if binary else 6, 2, payload if binary else payload.encode())
    return struct.pack(">I", len(msg)) + msg


def read_varint(data, index):
    result = shift = 0
    while True:
        byte = data[index]
        index += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, index
        shift += 7


def decode(data):
    """Return the CastMessage fields we care about."""
    out = {"source": "", "destination": "", "namespace": "", "payload": "", "binary": False}
    index = 0
    while index < len(data):
        tag, index = read_varint(data, index)
        number, wire = tag >> 3, tag & 7
        if wire == 0:
            value, index = read_varint(data, index)
            if number == 5 and value == 1:
                out["binary"] = True
        elif wire == 2:
            length, index = read_varint(data, index)
            chunk = data[index:index + length]
            index += length
            if number == 2:
                out["source"] = chunk.decode()
            elif number == 3:
                out["destination"] = chunk.decode()
            elif number == 4:
                out["namespace"] = chunk.decode()
            elif number == 6:
                out["payload"] = chunk.decode()
            elif number == 7:
                out["payload"] = chunk
        else:
            raise ValueError(f"unsupported wire type {wire}")
    return out


class Sender:
    def __init__(self, host, port):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        raw = socket.create_connection((host, port), timeout=10)
        self.sock = context.wrap_socket(raw, server_hostname=host)
        self.request_id = 0

    def send(self, destination, namespace, payload, binary=False):
        body = payload if binary else json.dumps(payload)
        self.sock.sendall(encode(SENDER, destination, namespace, body, binary))

    def request(self, destination, namespace, payload, expect_type, timeout=6):
        self.request_id += 1
        payload = dict(payload, requestId=self.request_id)
        self.send(destination, namespace, payload)
        deadline = time.time() + timeout

        while time.time() < deadline:
            message = self.recv(deadline - time.time())
            if message is None:
                break
            if message["namespace"] == NS_HEARTBEAT:
                continue
            if message["binary"]:
                continue
            body = json.loads(message["payload"])
            if body.get("requestId") == payload["requestId"]:
                got = body.get("type") or body.get("responseType")
                if got != expect_type:
                    fail(f"expected {expect_type}, got {got}: {body}")
                return body
        fail(f"timed out waiting for {expect_type}")
        return {}

    def wait_for(self, namespace, predicate, timeout):
        """Await a receiver-initiated message; these carry requestId 0."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            message = self.recv(deadline - time.time())
            if message is None or message["binary"] or message["namespace"] != namespace:
                continue
            body = json.loads(message["payload"])
            try:
                if predicate(body):
                    return body
            except (IndexError, KeyError, TypeError):
                continue
        return None

    def recv(self, timeout):
        self.sock.settimeout(max(0.1, timeout))
        try:
            header = self.readn(4)
            length = struct.unpack(">I", header)[0]
            return decode(self.readn(length))
        except (socket.timeout, ssl.SSLError, ConnectionError):
            return None

    def readn(self, count):
        buffer = b""
        while len(buffer) < count:
            chunk = self.sock.recv(count - len(buffer))
            if not chunk:
                raise ConnectionError("closed")
            buffer += chunk
        return buffer

    def close(self):
        self.sock.close()


def check(label, condition, detail=""):
    if condition:
        print(f"  PASS  {label}")
    else:
        fail(f"{label} {detail}")


def fail(message):
    failures.append(message)
    print(f"  FAIL  {message}")


def main():
    print(f"connecting to {HOST}:{PORT}")
    sender = Sender(HOST, PORT)

    # Device auth: challenge with an empty AuthChallenge submessage.
    print("\ndevice auth")
    sender.send(PLATFORM, NS_AUTH, b"\x0a\x00", binary=True)
    reply = sender.recv(5)
    check("auth challenge answered", reply is not None and reply["binary"],
          f"got {reply}")

    print("\nconnection + receiver status")
    sender.send(PLATFORM, NS_CONNECTION, {"type": "CONNECT"})
    status = sender.request(PLATFORM, NS_RECEIVER, {"type": "GET_STATUS"}, "RECEIVER_STATUS")
    apps = status.get("status", {}).get("applications", [])
    check("idle device reports backdrop", apps and apps[0].get("isIdleScreen") is True, f"got {apps}")
    check("volume present", "volume" in status.get("status", {}))

    print("\napp availability")
    availability = sender.request(
        PLATFORM, NS_RECEIVER,
        {"type": "GET_APP_AVAILABILITY", "appId": ["CC1AD845"]}, "GET_APP_AVAILABILITY")
    check("CC1AD845 available", availability.get("availability", {}).get("CC1AD845") == "APP_AVAILABLE",
          str(availability))

    print("\nlaunch")
    launched = sender.request(PLATFORM, NS_RECEIVER, {"type": "LAUNCH", "appId": "CC1AD845"}, "RECEIVER_STATUS")
    apps = launched.get("status", {}).get("applications", [])
    check("app launched", apps and apps[0].get("appId") == "CC1AD845", str(apps))
    if not apps:
        return report(sender)

    transport = apps[0]["transportId"]
    session_id = apps[0]["sessionId"]
    check("transport id issued", transport.startswith("web-"), transport)
    check("media namespace declared",
          any(n["name"] == NS_MEDIA for n in apps[0].get("namespaces", [])), str(apps[0].get("namespaces")))

    print("\nmedia load")
    sender.send(transport, NS_CONNECTION, {"type": "CONNECT"})
    loaded = sender.request(transport, NS_MEDIA, {
        "type": "LOAD",
        "autoplay": True,
        "currentTime": 0,
        "media": {
            "contentId": "http://example.com/video.mp4",
            "contentType": "video/mp4",
            "streamType": "BUFFERED",
            "duration": 30.0,
        },
    }, "MEDIA_STATUS")

    entries = loaded.get("status", [])
    check("media session created", entries and entries[0].get("mediaSessionId") == 1, str(entries))
    check("autoplay -> PLAYING", entries and entries[0].get("playerState") == "PLAYING", str(entries))
    check("media echoed back", entries and entries[0].get("media", {}).get("contentType") == "video/mp4", str(entries))
    if not entries:
        return report(sender)

    media_session_id = entries[0]["mediaSessionId"]

    print("\nplayback clock")
    time.sleep(1.5)
    ticked = sender.request(transport, NS_MEDIA, {"type": "GET_STATUS"}, "MEDIA_STATUS")
    current = ticked.get("status", [{}])[0].get("currentTime", 0)
    check("currentTime advances", current >= 1.0, f"currentTime={current}")

    print("\ntransport controls")
    paused = sender.request(
        transport, NS_MEDIA, {"type": "PAUSE", "mediaSessionId": media_session_id}, "MEDIA_STATUS")
    check("PAUSE honoured", paused.get("status", [{}])[0].get("playerState") == "PAUSED", str(paused))

    held = paused.get("status", [{}])[0].get("currentTime", 0)
    time.sleep(1.0)
    still = sender.request(transport, NS_MEDIA, {"type": "GET_STATUS"}, "MEDIA_STATUS")
    check("clock frozen while paused",
          abs(still.get("status", [{}])[0].get("currentTime", 0) - held) < 0.2,
          f"{held} -> {still.get('status', [{}])[0].get('currentTime')}")

    sought = sender.request(
        transport, NS_MEDIA,
        {"type": "SEEK", "mediaSessionId": media_session_id, "currentTime": 10.0, "resumeState": "PLAYBACK_START"},
        "MEDIA_STATUS")
    check("SEEK honoured", abs(sought.get("status", [{}])[0].get("currentTime", 0) - 10.0) < 0.5, str(sought))
    check("resumeState resumes", sought.get("status", [{}])[0].get("playerState") == "PLAYING", str(sought))

    bad = sender.request(
        transport, NS_MEDIA, {"type": "PLAY", "mediaSessionId": 999}, "INVALID_REQUEST")
    check("stale mediaSessionId rejected", bad.get("reason") == "INVALID_MEDIA_SESSION_ID", str(bad))

    print("\nunsolicited end-of-media broadcast")
    sender.request(transport, NS_MEDIA, {
        "type": "LOAD",
        "autoplay": True,
        "media": {"contentId": "short.mp4", "contentType": "video/mp4", "duration": 2.0},
    }, "MEDIA_STATUS")
    finished = sender.wait_for(NS_MEDIA, lambda body: body.get("status", [{}])[0].get("idleReason") == "FINISHED", 8)
    check("FINISHED pushed without polling", finished is not None, "no unsolicited MEDIA_STATUS arrived")
    if finished:
        check("finished state is IDLE", finished["status"][0].get("playerState") == "IDLE", str(finished))

    print("\nvolume")
    volumed = sender.request(
        PLATFORM, NS_RECEIVER, {"type": "SET_VOLUME", "volume": {"level": 0.25}}, "RECEIVER_STATUS")
    check("volume applied", abs(volumed.get("status", {}).get("volume", {}).get("level", 1) - 0.25) < 0.001,
          str(volumed.get("status", {}).get("volume")))

    print("\nstop")
    stopped = sender.request(PLATFORM, NS_RECEIVER, {"type": "STOP", "sessionId": session_id}, "RECEIVER_STATUS")
    apps = stopped.get("status", {}).get("applications", [])
    check("session torn down", apps and apps[0].get("isIdleScreen") is True, str(apps))

    return report(sender)


def report(sender):
    sender.close()
    print()
    if failures:
        print(f"{len(failures)} FAILURE(S)")
        for message in failures:
            print(f"  - {message}")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
