# Chromecast Emulator

A fake Chromecast receiver (CASTV2 protocol) for testing Cast senders. .NET 10, C#, single executable. Senders connect over TLS on port 8009; the emulator answers device auth, heartbeat, receiver, and media namespaces, and advertises itself over mDNS.

## Layout

```
Chromecast-Emulator.slnx          solution (slnx format)
src/ChromecastEmulator/
  Program.cs                      composition root — wires logging, handlers, server
  EmulatorOptions.cs              CLI parsing; --help text lives here
  CastLogging.cs                  Serilog pipeline (console + JSON Lines journal)
  CastFrameLog.cs                 per-frame logging (console line + journal properties)
  Device/                         VirtualDevice, AppSession, MediaSession, Volume — no I/O
  Transport/                      TCP/TLS server, CASTV2 framing, persisted identity
  Protocol/                       one handler per Cast namespace + MessageRouter
  Discovery/                      mDNS advertiser (Makaretu.Dns)
  Protos/cast_channel.proto       generates ChromecastEmulator.Proto.CastMessage
tests/
  ChromecastEmulator.UnitTests/          pure in-process tests, mirrors src/ layout
  ChromecastEmulator.IntegrationTests/   real sockets/TLS/filesystem/RSA, mirrors src/ layout
  ChromecastEmulator.TestSupport/        shared helpers — must stay free of xunit/NSubstitute
tools/test-sender.py              end-to-end sender script (real TLS client)
```

## Commands

```bash
dotnet build                                      # whole solution
dotnet test                                       # all tests (~200, <2s)
dotnet test tests/ChromecastEmulator.UnitTests    # fast inner loop, no I/O
dotnet run --project src/ChromecastEmulator -- --help
```

Manual run for a live check: `dotnet run --project src/ChromecastEmulator -- --no-advertise --port 18009 --state-dir /tmp/cast-state`, then drive it with `tools/test-sender.py` or a raw TLS client. Heartbeat frames only appear on the console with `-v`.

## Constraints that are easy to violate

- **The `--log-file` JSON Lines field names are a contract** (`ts, dir, peer, source, destination, namespace, short, type, payload` — defined in `CastLogging.cs`). Sender test harnesses parse them; do not rename or reorder. `--quiet` silences the console only — the journal must still record every frame, including heartbeats.
- **Logging levels**: handlers take `ILogger<T>` via constructor. Serilog pipeline stays at Verbose; sinks filter. Heartbeat frames log at Trace so the default console isn't drowned in PINGs.
- **Single owners — extend, don't fork**: volume wire-parsing lives in `Volume.Apply`; RECEIVER_STATUS/MEDIA_STATUS envelopes in `StatusBroadcaster`; reply addressing in `CastConnection.ReplyAsync`/`ReplyBinaryAsync`. Duplicating any of these has caused drift before.
- **Every reply echoes the request's `requestId`** — senders correlate by it and hang otherwise. `GET_APP_AVAILABILITY` answers with `responseType`, not `type` (real-device quirk; `CastFrameLog` special-cases it).
- **`HeartbeatHandler` must never throw** — no PONG means senders disconnect after ~10s. Parse defensively.
- **`CastConnection` cannot be faked** — it's sealed around `SslStream`, which refuses to write before a handshake. Handler tests use `TestSupport/LoopbackChannel` (a real TLS loopback) and assert on actual frames off the wire.
- **`CastConnection`'s write semaphore is deliberately not disposed**; disposing it races in-flight sends.
- **mDNS TXT records don't include volume** — volume changes must not call `NotifyStatusChanged()` (it triggers a full multicast republish for nothing). `StatusChanged` exists solely to refresh TXT `st`/`rs` when the app list changes.
- **`MediaSession` owns a timer** — anything that creates one must dispose it (`using var` in tests; `AppSession`/`VirtualDevice` own lifecycle in src).

## Conventions

- Tests: xUnit v2 + NSubstitute, names `Method_StateUnderTest_ExpectedBehaviour`. New test files mirror the src/ folder of the type under test. Real-I/O tests go in IntegrationTests, pure ones in UnitTests. Handler tests that only assert direct replies use an empty `IConnectionRegistry` substitute so broadcasts can't add stray frames to the stream.
- Serilog assertions go through `TestSupport/CollectingSink` (captures real pipeline `LogEvent`s), not a mocked `ILogger`.
- Comments: brief, WHY-only — the non-obvious constraint, present tense. No narration of changes.
- Log messages are structured templates (`"launched {AppId}"`, PascalCase placeholders), never interpolated strings. Routine network faults log `ex.Message` as `{Reason}`; genuine faults pass the exception object.
