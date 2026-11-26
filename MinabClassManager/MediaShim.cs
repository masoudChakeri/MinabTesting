using System.Text.Json;

namespace MinabClassManager;

/// <summary>
/// JavaScript injected into every page of a student session. It wraps
/// navigator.mediaDevices so that:
///  - only the kinds enabled for this student (camera / mic) are requested,
///    so a camera-only student is not broken by a combined camera+mic request;
///  - if Chromium's fake capture device is unavailable or rejects the
///    constraints, a generated video (canvas) / audio (oscillator) track is
///    returned instead;
///  - enumerateDevices always lists a camera/mic when enabled;
///  - every step is reported to the host log.
/// </summary>
public static class MediaShim
{
    public static string Build(bool camera, bool mic, string studentName) =>
        Script
            .Replace("__CAM__", camera ? "true" : "false")
            .Replace("__MIC__", mic ? "true" : "false")
            .Replace("__NAME__", JsonSerializer.Serialize(studentName));

    private const string Script = """
(() => {
  const CAM = __CAM__, MIC = __MIC__, NAME = __NAME__;
  const md = navigator.mediaDevices;
  if (!md || md.__minabShim) return;
  md.__minabShim = true;

  const log = (m) => { try { window.chrome && window.chrome.webview && window.chrome.webview.postMessage(String(m)); } catch (e) {} };
  const where = () => { try { return location.host + location.pathname; } catch (e) { return '?'; } };

  // ---- generated fallback tracks -------------------------------------------
  let canvas = null, canvasStream = null;
  function fakeVideoTrack() {
    if (!canvasStream) {
      canvas = document.createElement('canvas');
      canvas.width = 640; canvas.height = 480;
      const g = canvas.getContext('2d');
      let t = 0;
      const draw = () => {
        t++;
        const hue = (t * 2) % 360;
        const grad = g.createLinearGradient(0, 0, 640, 480);
        grad.addColorStop(0, `hsl(${hue},60%,35%)`);
        grad.addColorStop(1, `hsl(${(hue + 120) % 360},60%,25%)`);
        g.fillStyle = grad; g.fillRect(0, 0, 640, 480);
        g.fillStyle = 'rgba(255,255,255,0.9)';
        g.beginPath(); g.arc(320 + Math.sin(t / 15) * 120, 220, 60, 0, Math.PI * 2); g.fill();
        g.font = 'bold 36px Segoe UI, sans-serif'; g.textAlign = 'center';
        g.fillText(NAME, 320, 360);
        g.font = '22px Segoe UI, sans-serif';
        g.fillText(new Date().toLocaleTimeString(), 320, 400);
      };
      draw();
      setInterval(draw, 66);
      canvasStream = canvas.captureStream(15);
    }
    return canvasStream.getVideoTracks()[0].clone();
  }

  let audioCtx = null;
  function fakeAudioTrack() {
    audioCtx = audioCtx || new AudioContext();
    const osc = audioCtx.createOscillator();
    const gain = audioCtx.createGain();
    gain.gain.value = 0.02;
    osc.frequency.value = 440;
    const dst = audioCtx.createMediaStreamDestination();
    osc.connect(gain).connect(dst);
    osc.start();
    if (audioCtx.resume) audioCtx.resume();
    return dst.stream.getAudioTracks()[0];
  }

  // ---- getUserMedia ----------------------------------------------------------
  const origGUM = md.getUserMedia ? md.getUserMedia.bind(md) : null;
  md.getUserMedia = async function (c) {
    c = c || {};
    const wantV = !!c.video, wantA = !!c.audio;
    const v = wantV && CAM, a = wantA && MIC;
    log(`getUserMedia on ${where()} requested video=${wantV} audio=${wantA} -> using video=${v} audio=${a}`);
    if (!v && !a) {
      log('-> denied: requested kinds are disabled for this student');
      throw new DOMException('Permission denied', 'NotAllowedError');
    }
    const out = new MediaStream();
    let gotV = false, gotA = false;
    if (origGUM) {
      try {
        const s = await origGUM({ video: v ? c.video : false, audio: a ? c.audio : false });
        s.getTracks().forEach(tr => { out.addTrack(tr); if (tr.kind === 'video') gotV = true; else gotA = true; });
        log('-> native tracks: ' + s.getTracks().map(tr => `${tr.kind}:${tr.label}:${tr.readyState}`).join(', '));
      } catch (e) {
        log(`-> native getUserMedia failed: ${e.name}: ${e.message}`);
        // Retry without device/size constraints (e.g. a saved deviceId that does not exist here)
        try {
          const s = await origGUM({ video: v, audio: a });
          s.getTracks().forEach(tr => { out.addTrack(tr); if (tr.kind === 'video') gotV = true; else gotA = true; });
          log('-> native retry ok: ' + s.getTracks().map(tr => `${tr.kind}:${tr.label}`).join(', '));
        } catch (e2) {
          log(`-> native retry failed: ${e2.name}: ${e2.message}`);
        }
      }
    }
    if (v && !gotV) { out.addTrack(fakeVideoTrack()); log('-> using generated video track'); }
    if (a && !gotA) { out.addTrack(fakeAudioTrack()); log('-> using generated audio track'); }
    out.getTracks().forEach(watchTrack);
    return out;
  };

  // ---- diagnostics: track lifecycle ----------------------------------------
  let trackSeq = 0;
  function watchTrack(tr) {
    const id = `${tr.kind}#${++trackSeq}`;
    tr.__minabId = id;
    tr.addEventListener('ended', () => log(`track ${id} ended`));
    tr.addEventListener('mute', () => log(`track ${id} muted (no frames arriving)`));
    tr.addEventListener('unmute', () => log(`track ${id} unmuted`));
    const origStop = tr.stop.bind(tr);
    tr.stop = () => { log(`track ${id} stopped by page`); origStop(); };
    const s = tr.getSettings ? tr.getSettings() : {};
    log(`track ${id} created: ${tr.label} ${s.width || '?'}x${s.height || '?'}@${s.frameRate || '?'} enabled=${tr.enabled} muted=${tr.muted}`);
  }

  // ---- diagnostics: WebRTC sending -----------------------------------------
  const pcs = [];
  const OrigPC = window.RTCPeerConnection;
  if (OrigPC && !OrigPC.__minab) {
    class MinabPC extends OrigPC {
      constructor(...args) {
        super(...args);
        const n = pcs.push(this);
        log(`RTCPeerConnection #${n} created`);
        this.addEventListener('connectionstatechange', () => log(`pc#${n} connection=${this.connectionState}`));
      }
      addTrack(track, ...rest) { log(`pc addTrack ${track && (track.__minabId || track.kind)}`); return super.addTrack(track, ...rest); }
      addTransceiver(t, init) { log(`pc addTransceiver ${typeof t === 'string' ? t : (t.__minabId || t.kind)} dir=${init && init.direction}`); return super.addTransceiver(t, init); }
    }
    MinabPC.__minab = true;
    window.RTCPeerConnection = MinabPC;
    if (window.RTCRtpSender && RTCRtpSender.prototype.replaceTrack) {
      const origReplace = RTCRtpSender.prototype.replaceTrack;
      RTCRtpSender.prototype.replaceTrack = function (t) {
        log(`sender replaceTrack -> ${t ? (t.__minabId || t.kind) + ':' + t.readyState : 'null'}`);
        return origReplace.call(this, t);
      };
    }
    setInterval(async () => {
      for (let i = 0; i < pcs.length; i++) {
        const pc = pcs[i];
        if (pc.connectionState === 'closed') continue;
        const senders = pc.getSenders().map(s => s.track ? `${s.track.__minabId || s.track.kind}(${s.track.readyState},muted=${s.track.muted},enabled=${s.track.enabled})` : 'empty').join(' ');
        let rtp = '';
        try {
          const stats = await pc.getStats();
          stats.forEach(r => {
            if (r.type === 'outbound-rtp' && r.kind === 'video')
              rtp += ` [video out ${r.rid || ''} bytes=${r.bytesSent} framesEnc=${r.framesEncoded} ${r.frameWidth || 0}x${r.frameHeight || 0} fps=${r.framesPerSecond || 0} limit=${r.qualityLimitationReason}]`;
            if (r.type === 'media-source' && r.kind === 'video')
              rtp += ` [video source frames=${r.frames} fps=${r.framesPerSecond || 0} ${r.width || 0}x${r.height || 0}]`;
          });
        } catch (e) { rtp = ' stats error ' + e.message; }
        log(`pc#${i + 1} ${pc.connectionState} senders: ${senders || 'none'}${rtp}`);
      }
    }, 10000);
  }

  // ---- enumerateDevices ------------------------------------------------------
  const origEnum = md.enumerateDevices ? md.enumerateDevices.bind(md) : null;
  const fakeInfo = (kind, label) => ({
    deviceId: 'minab-' + kind, groupId: 'minab', kind, label,
    toJSON() { return { deviceId: this.deviceId, groupId: this.groupId, kind: this.kind, label: this.label }; }
  });
  md.enumerateDevices = async function () {
    let list = [];
    try { list = origEnum ? Array.from(await origEnum()) : []; } catch (e) { log('enumerateDevices failed: ' + e.message); }
    if (!CAM) list = list.filter(d => d.kind !== 'videoinput');
    if (!MIC) list = list.filter(d => d.kind !== 'audioinput');
    if (CAM && !list.some(d => d.kind === 'videoinput')) list.push(fakeInfo('videoinput', 'Minab Fake Camera'));
    if (MIC && !list.some(d => d.kind === 'audioinput')) list.push(fakeInfo('audioinput', 'Minab Fake Microphone'));
    log('enumerateDevices -> ' + list.map(d => `${d.kind}:${d.label || '(no label)'}`).join(', '));
    return list;
  };

  // ---- Permissions API -------------------------------------------------------
  if (navigator.permissions && navigator.permissions.query) {
    const origQuery = navigator.permissions.query.bind(navigator.permissions);
    navigator.permissions.query = async function (desc) {
      const name = desc && desc.name;
      if (name === 'camera' || name === 'microphone') {
        const on = name === 'camera' ? CAM : MIC;
        log(`permissions.query(${name}) -> ${on ? 'granted' : 'denied'}`);
        try {
          const real = await origQuery(desc);
          Object.defineProperty(real, 'state', { get: () => on ? 'granted' : 'denied' });
          return real;
        } catch (e) {
          return { name, state: on ? 'granted' : 'denied', onchange: null, addEventListener() {}, removeEventListener() {} };
        }
      }
      return origQuery(desc);
    };
  }

  log(`media shim active on ${where()} (camera=${CAM}, mic=${MIC})`);
})();
""";
}
