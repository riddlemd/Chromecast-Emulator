# Third-party code in the render window

## hls.js

`Assets/hls.light.min.js` is hls.js v1.7.0, the "light" build (no subtitles, alternate
audio tracks, or EME), vendored unmodified from
<https://cdn.jsdelivr.net/npm/hls.js@1/dist/hls.light.min.js>.

Copyright the hls.js authors. Licensed under the Apache License, Version 2.0 —
<https://github.com/video-dev/hls.js/blob/master/LICENSE>. The minified build carries no
banner of its own, which is why this notice exists.

It is vendored rather than fetched from a CDN because the emulator must work offline and
the webview's page is served from loopback.

To update: download the same file, replace it, and check the version recorded above.
