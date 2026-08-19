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
  Render/                         optional --render window: ffmpeg -> HLS -> Photino webview
    Assets/                       player page, embedded resources (hls.js vendored — see THIRD-PARTY.md)
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

Add `--render` to open a window that actually plays what a sender loads: ffmpeg transcodes `contentId` to HLS in a temp directory, a loopback `HttpListener` serves it, and a Photino webview plays it through hls.js. Needs ffmpeg on PATH. The page reports what the video element did (`playing`/`pause`/`waiting`/`ended`, and hls.js errors) at Debug, which is the only view of real playback — the emulator's own clock is a stopwatch and advances regardless.

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
- **The render window must own the process main thread.** AppKit ties its run loop to thread 0, so `RenderWindow.Run` is the last thing `Program.cs` does and the Cast accept loop runs on the thread pool behind it. Creating the window anywhere else dies with `NSInternalInconsistencyException: setting the main menu on a non-main thread`. Never construct a `PhotinoWindow` in a test.
- **Never hand the player a playlist ffmpeg hasn't written yet.** hls.js treats a 404 manifest as a fatal error and does not recover, so `RenderController` answers LOAD immediately, shows `loading`, and only sends `load` once `HlsPipeline.WaitForFirstSegmentAsync` sees `#EXTINF`. Retry options named `manifestLoadingMaxRetry` are hls.js 0.x and are silently ignored by the vendored 1.x build — its retry config lives under `*LoadPolicy`.
- **The render window is a subscriber, not an owner** — `MediaSession` stays the single source of playback state and the window mirrors it. `IMediaRenderer` calls must be idempotent; they fire after every transport command, including ones that changed nothing.
- **WKWebView restarts a paused video on its own, so `player.js` re-asserts state rather than applying it once.** Measured: roughly one PAUSE in twenty is followed 1–3s later by a `play` event with no `play()` call behind it — confirmed by wrapping `video.play` and finding the wrapper was never invoked, with hls.js ruled out (v1.7.0 has no reachable `.play()` call, and its gap controller no-ops when `media.paused`). Nothing in the page can prevent it, only correct it, which is what `desiredState` and the `play`/`pause` listeners do. Deleting that guard silently desyncs the window from a paused session.
- **Keep `-hls_list_size 0` if you keep hls.js.** `StreamController.synchronizeToLiveEdge` sets `media.currentTime` with no `paused` guard (a 2019 fix, video-dev/hls.js#2417, lost in later refactors). It cannot fire today only because the retained-segment playlist keeps `currentTime` inside the sliding window and `liveMaxLatencyDurationCount` defaults to `Infinity` — measured as 48 playlist reloads while paused with zero seeks. Switching to a sliding window, or setting `liveMaxLatencyDuration`, arms it and the window will start jumping to the live edge while paused.
- **Photino's custom scheme handler cannot serve the player** — it can't serve the initial page, sends no CORS headers, and has no Range support. That's why `PlayerServer` is a real loopback `HttpListener`.

## Conventions

- Tests: xUnit v2 + NSubstitute, names `Method_StateUnderTest_ExpectedBehaviour`. New test files mirror the src/ folder of the type under test. Real-I/O tests go in IntegrationTests, pure ones in UnitTests. Handler tests that only assert direct replies use an empty `IConnectionRegistry` substitute so broadcasts can't add stray frames to the stream.
- Serilog assertions go through `TestSupport/CollectingSink` (captures real pipeline `LogEvent`s), not a mocked `ILogger`.
- Comments: brief, WHY-only — the non-obvious constraint, present tense. No narration of changes.
- Log messages are structured templates (`"launched {AppId}"`, PascalCase placeholders), never interpolated strings. Routine network faults log `ex.Message` as `{Reason}`; genuine faults pass the exception object.
