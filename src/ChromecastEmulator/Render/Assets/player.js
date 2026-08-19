// Player page for the render window. Receives state from the emulator over Photino's
// bridge and mirrors it onto a video element fed by ffmpeg's HLS output.
(function () {
  'use strict';

  var video = document.getElementById('video');
  var banner = document.getElementById('banner');
  var errorBox = document.getElementById('error');
  var idleHeading = document.getElementById('idle-heading');
  var idleDetail = document.getElementById('idle-detail');
  var idleTitle = document.getElementById('idle-title');
  var hls = null;
  var bannerTimer = null;
  var attached = false;

  // What the emulator last said the session is doing. hls.js resumes playback on its own
  // when it recovers from a buffer hole or media error, which silently desynced the
  // window from a PAUSED session, so the state is held and re-asserted rather than
  // applied once and forgotten.
  var desiredState = null;
  var lastCorrection = 0;

  // Below roughly one segment the player is just drifting behind its own clock; forcing a
  // seek for that would stutter every status update.
  var SEEK_THRESHOLD = 1.5;

  function report(message) {
    if (window.external && window.external.sendMessage) window.external.sendMessage(message);
  }


  // A play() promise rejects with AbortError whenever a pause or a new load interrupts
  // it, which is routine here — the emulator drives both.
  function play() {
    var started = video.play();
    if (started && started.catch) {
      started.catch(function (e) {
        if (e && e.name !== 'AbortError') report('play rejected: ' + e);
      });
    }
  }

  function showError(text) {
    errorBox.textContent = text;
    errorBox.classList.add('show');
    report('error: ' + text);
  }

  function clearError() {
    errorBox.classList.remove('show');
  }

  function showBanner(text) {
    banner.textContent = text;
    banner.classList.add('show');
    clearTimeout(bannerTimer);
    bannerTimer = setTimeout(function () { banner.classList.remove('show'); }, 4000);
  }

  function detach() {
    attached = false;
    if (hls) {
      hls.destroy();
      hls = null;
    }
    video.removeAttribute('src');
    video.load();
  }

  function attach(src) {
    detach();
    clearError();

    if (window.Hls && window.Hls.isSupported()) {
      // ffmpeg appends to the playlist while it plays, so a miss usually means "not
      // written yet" rather than a broken stream. These are hls.js 1.x load policies;
      // the older manifestLoadingMaxRetry-style options are silently ignored.
      var retry = {
        maxTimeToFirstByteMs: 10000,
        maxLoadTimeMs: 30000,
        timeoutRetry: { maxNumRetry: 4, retryDelayMs: 500, maxRetryDelayMs: 2000 },
        errorRetry: { maxNumRetry: 6, retryDelayMs: 500, maxRetryDelayMs: 2000 },
      };
      hls = new window.Hls({
        playlistLoadPolicy: { default: retry },
        manifestLoadPolicy: { default: retry },
        fragLoadPolicy: { default: retry },
      });

      hls.on(window.Hls.Events.ERROR, function (_event, data) {
        report('hls ' + (data.fatal ? 'fatal' : 'recoverable') + ' ' + data.details
          + (data.response && data.response.code ? ' (' + data.response.code + ')' : ''));
        if (!data.fatal) return;
        if (data.type === window.Hls.ErrorTypes.NETWORK_ERROR) {
          hls.startLoad();
        } else if (data.type === window.Hls.ErrorTypes.MEDIA_ERROR) {
          hls.recoverMediaError();
        } else {
          showError('playback failed: ' + data.details);
        }
      });

      hls.loadSource(src);
      hls.attachMedia(video);
      attached = true;
      return;
    }

    // WebKit plays HLS natively; other webviews without MSE have no path at all.
    if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.src = src;
      attached = true;
      return;
    }

    showError('no HLS support in this webview');
  }

  function apply(command) {
    switch (command.cmd) {
      case 'loading':
        document.body.classList.remove('playing');
        desiredState = null;
        clearError();
        detach();
        idleHeading.textContent = 'Preparing media';
        idleDetail.textContent = 'Waiting for the first segment from ffmpeg';
        idleTitle.textContent = command.title || '';
        break;

      case 'load':
        document.body.classList.add('playing');
        desiredState = command.autoplay ? 'PLAYING' : 'PAUSED';
        attach(command.src);
        if (command.currentTime > 0) {
          video.addEventListener('loadedmetadata', function seekOnce() {
            video.removeEventListener('loadedmetadata', seekOnce);
            video.currentTime = command.currentTime;
          });
        }
        if (command.autoplay) play();
        if (command.title) showBanner(command.title);
        break;

      case 'sync':
        desiredState = command.state === 'PLAYING' ? 'PLAYING' : 'PAUSED';
        // State keeps arriving while ffmpeg starts up and after an error; driving a
        // video element with no source just stalls it.
        if (!attached) break;
        if (typeof command.rate === 'number' && command.rate > 0) video.playbackRate = command.rate;
        if (typeof command.currentTime === 'number'
            && isFinite(video.duration)
            && Math.abs(video.currentTime - command.currentTime) > SEEK_THRESHOLD) {
          video.currentTime = command.currentTime;
        }
        if (command.state === 'PLAYING') play();
        else video.pause();
        break;

      case 'volume':
        video.volume = Math.max(0, Math.min(1, command.level));
        video.muted = !!command.muted;
        break;

      case 'idle':
        document.body.classList.remove('playing');
        desiredState = null;
        clearError();
        detach();
        idleHeading.textContent = 'Ready to cast';
        idleDetail.textContent = 'Waiting for a sender to load media';
        idleTitle.textContent = '';
        break;

      case 'error':
        showError(command.text);
        break;
    }
  }

  if (window.external && window.external.receiveMessage) {
    window.external.receiveMessage(function (raw) {
      try {
        apply(JSON.parse(raw));
      } catch (e) {
        report('bad command: ' + e);
      }
    });
  }

  video.addEventListener('error', function () {
    var code = video.error ? video.error.code : '?';
    showError('video element error ' + code);
  });

  // The emulator's own clock is a stopwatch, so these are the only evidence of what the
  // window actually rendered.
  ['playing', 'pause', 'waiting', 'ended'].forEach(function (name) {
    video.addEventListener(name, function () {
      report(name + ' @ ' + video.currentTime.toFixed(2));
    });
  });

  // Put the element back where the session says it should be when something else moved
  // it. Throttled so a source that refuses to hold the state logs once a second instead
  // of spinning.
  function enforce(what, correct) {
    if (!attached) return;
    var now = Date.now();
    if (now - lastCorrection < 1000) return;
    lastCorrection = now;
    report('unrequested ' + what + '; restoring ' + desiredState);
    correct();
  }

  video.addEventListener('play', function () {
    if (desiredState === 'PAUSED') enforce('play', function () { video.pause(); });
  });

  video.addEventListener('pause', function () {
    // An element that has errored cannot be resumed; forcing it would just loop.
    if (desiredState === 'PLAYING' && !video.error && !video.ended) enforce('pause', play);
  });

  report('ready');
})();
