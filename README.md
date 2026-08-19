# Chromecast Emulator

A fake Google Cast receiver in C#. It advertises itself over mDNS exactly like a real
Chromecast, accepts TLS cast channel connections on port 8009, and speaks enough of the
CASTV2 protocol that a sender can discover it, launch an app, load media, and drive
playback — without any hardware.

Built for testing senders. It simulates receiver state; it does not render anything.

## The authentication caveat

A real Chromecast answers the `tp.deviceauth` challenge with a certificate chaining to
Google's Eureka CA. This emulator signs with its own self-signed key, because only
Google's factory-provisioned devices hold that chain.

- **Custom senders, or SDK senders in a developer mode that skips verification** — works.
- **Stock Chrome cast menu / unmodified Cast SDK senders** — they will discover the
  emulator and then drop the connection when the certificate fails to verify. That is not
  fixable from this side.

`--auth error` and `--auth ignore` let you exercise how your sender handles a receiver
that rejects or stalls on the challenge.

## Quickstart

```bash
cd src/ChromecastEmulator
dotnet run -- --name "Test Cast"
```

Then point a sender at it, or run the bundled smoke test against it:

```bash
python3 tools/test-sender.py            # defaults to 127.0.0.1:8009
python3 tools/test-sender.py 192.168.1.50
```

`tools/test-sender.py` is a dependency-free CASTV2 client (hand-rolled protobuf codec) that
walks the full flow and asserts on the responses. It doubles as a worked example of how to
talk to the emulator, and as a regression test after changing handlers.

Ports below 1024 are not involved, so no elevated privileges are needed.

## Options

```
DEVICE IDENTITY
  -n, --name <text>          Friendly name shown to senders
      --model <text>         Model name in the mDNS md= record
      --device-id <hex>      32-char device id; persisted in the state dir if omitted
      --state-dir <path>     Identity + TLS key location (default: ~/.chromecast-emulator)

NETWORK
  -p, --port <n>             Cast channel port (default: 8009)
      --bind <ip>            Listen address (default: 0.0.0.0)
      --no-advertise         Skip mDNS; senders must connect by IP

PROTOCOL
      --auth <mode>          respond | error | ignore (default: respond)
      --strict-apps          Only launch known app ids; otherwise LAUNCH_ERROR
      --namespace <urn>      Extra namespace to declare on launched apps (repeatable)
      --default-duration <s> Duration applied to LOAD requests that omit one
      --ping-interval <s>    Heartbeat PING interval, 0 disables (default: 5)
      --echo                 Echo custom-namespace messages back to the sender

RENDER
      --render               Open a window that plays loaded media (needs ffmpeg)
      --render-size <WxH>    Window size (default: 1280x720)
      --render-port <n>      Loopback port for the player page (default: an unused one)
      --ffmpeg <path>        ffmpeg executable (default: ffmpeg)
      --video-codec <name>   ffmpeg encoder (default: h264_videotoolbox on macOS, else libx264)
      --video-bitrate <rate> Target video bitrate (default: 4M)

OUTPUT
  -v, --verbose              Log full payloads including heartbeats and mDNS records
  -q, --quiet                Errors only
      --log-file <path>      Append every frame as JSON Lines
      --no-console           Disable the interactive command console
```

The device id, base-station id and TLS key persist in the state dir, so the emulator keeps
a stable identity across restarts — senders that cache devices keep recognising it.

## Interactive console

While running, the emulator reads commands on stdin. This is how you push
receiver-initiated events at a sender under test:

```
status                    device, volume and session state
conns                     connected senders and their virtual connections
launch <appId>            launch an app as if a sender asked
stop <sessionId>          tear a session down
volume <0-1>              set volume and broadcast RECEIVER_STATUS
mute | unmute             toggle mute
play | pause              drive the loaded media session
seek <seconds>            seek the loaded media session
stopmedia                 stop playback (IDLE/CANCELLED)
send <ns> <json>          push a message on any namespace to connected senders
quit                      shut down
```

`send` is the useful one for custom protocols — it fires an arbitrary payload on an
arbitrary namespace at every connected sender, so you can test how your sender reacts to
receiver-pushed messages without writing a receiver app.

## What it implements

**Discovery** — `_googlecast._tcp` on port 8009 with the TXT records senders filter on
(`id`, `cd`, `ve`, `md`, `ic`, `fn`, `ca`, `st`, `bs`, `nf`, `rs`). `st`/`rs` are
re-published when an app launches.

**Transport** — TLS 1.2/1.3, 4-byte big-endian length prefix, protobuf `CastMessage`,
virtual connections multiplexed over one socket.

| Namespace | Handled |
|---|---|
| `tp.connection` | CONNECT, CLOSE |
| `tp.heartbeat` | PING → PONG, plus outbound PING |
| `tp.deviceauth` | challenge → response / error / ignore |
| `receiver` | GET_STATUS, LAUNCH, STOP, SET_VOLUME, GET_APP_AVAILABILITY |
| `media` | LOAD, PLAY, PAUSE, SEEK, STOP, GET_STATUS, SET_PLAYBACK_RATE, VOLUME |
| `urn:x-cast:*` | logged, optionally echoed |

Media playback is simulated against a stopwatch: `currentTime` advances while PLAYING,
freezes on PAUSE, and the session flips to `IDLE`/`FINISHED` with an unsolicited
`MEDIA_STATUS` when it reaches the media duration.

**`--render`** additionally plays the media for real, so you can see what you cast: ffmpeg
transcodes the `contentId` to HLS, a loopback HTTP server serves it, and a Photino webview
plays it through hls.js. The stopwatch above stays authoritative for what senders are told
— the window mirrors it — so protocol behaviour is identical whether or not the window is
open. Closing the window shuts the emulator down.

Status changes are broadcast to every sender holding a virtual connection to the relevant
destination, matching how a real device pushes `RECEIVER_STATUS` and `MEDIA_STATUS`.

## What it does not implement

- Real certificate chains (see the authentication caveat above)
- Queues (`QUEUE_LOAD`, `QUEUE_INSERT`, …)
- Multizone / audio groups
- DIAL and the `:8008/setup/eureka_info` HTTP surface
- Cast Connect / Android TV receiver semantics
- Running a real receiver web app — `--render` plays the media, it does not host the
  CAF receiver the sender's app id would normally load

## Layout

```
src/ChromecastEmulator/
  Protos/cast_channel.proto   wire format, transcribed to proto3
  Transport/                  TLS listener, framing, device identity
  Protocol/                   namespace handlers, routing, broadcasts
  Device/                     virtual device, app sessions, media clock
  Discovery/                  mDNS advertisement
  Render/                     --render window: ffmpeg -> HLS -> Photino webview
tools/test-sender.py          dependency-free CASTV2 client and smoke test
```

## Notes

- The `namespace`, `protocol_version` and `payload_type` fields are declared `optional` in
  the proto3 file on purpose. Senders parse the message as proto2 where those fields are
  `required`, and proto3 omits default-valued fields from the wire unless they have
  explicit presence — without `optional` the sender's parse fails.
- On macOS the private key must make a PKCS#12 round-trip before `SslStream` will accept
  it for server authentication; `DeviceIdentity` does this.
- `--render` needs the process main thread for the window's event loop, so the Cast accept
  loop runs behind it on the thread pool. Without `--render` nothing changes.
