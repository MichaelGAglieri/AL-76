// AL-76 classic REACTOR for Windows: the Mac's NUCLEUS reactor (Reactor.swift, ClassicReactorView and
// ClassicReactorRenderer), drawn layer by layer with Canvas 2D: same radii, widths, opacities, waves and particles,
// driven by the same ReactorAnimator. 30 fps while working, 12 waiting, 6 after a minute, nothing while hidden.

const CYAN = [79, 219, 255], ICE = [204, 247, 255], DEEP = [5, 115, 204], AMBER = [255, 158, 51], PRIVACY = [140, 153, 168];
// SwiftUI's .shadow(radius: 5) spreads more than a canvas shadowBlur of the same size: 8 points matches renders of
// the Mac's own ClassicReactorRenderer within 2% in every state.
const GLOW = 8;
const scaleOf = (x) => x.getTransform().a;
const rgba = (c, a) => `rgba(${c[0]},${c[1]},${c[2]},${Math.max(0, Math.min(1, a))})`;

/** The Mac's SeededRandom (xorshift on 64 bits), for the same particles. */
class SeededRandom {
  constructor(seed) { this.s = (BigInt(seed) * 0x9E3779B97F4A7C15n) & 0xFFFFFFFFFFFFFFFFn; }
  next() {
    const M = 0xFFFFFFFFFFFFFFFFn;
    this.s ^= (this.s << 13n) & M; this.s ^= this.s >> 7n; this.s ^= (this.s << 17n) & M;
    return Number(this.s % 1000000n) / 1e6;
  }
}

/** ReactorAnimator: speed, energy, level, boot, alert and privacy, eased as on the Mac. */
class Animator {
  constructor() {
    this.start = performance.now() / 1000; this.last = null;
    this.phase = 0; this.time = 0; this.speed = 0.3; this.energy = 0.4; this.level = 0; this.boot = 0; this.alert = 0; this.privacy = 0;
    this.particles = Array.from({ length: 56 }, (_, i) => {
      const g = new SeededRandom((i + 1) * 7919);
      return { radius: 0.30 + g.next() * 0.66, angle: g.next() * Math.PI * 2, speed: (g.next() - 0.5) * 0.5, size: 0.8 + g.next() * 1.8, twinkle: g.next() * 10 };
    });
  }
  step(now, s) {
    const dt = Math.min(1 / 20, this.last === null ? 0 : now - this.last);
    this.last = now;
    this.time = now - this.start;
    this.boot = Math.min(1, this.time / 2.2);
    const t = this.time;
    const [ts, te, raw] = {
      booting: [1.4, 0.7, 0.15 + 0.15 * Math.sin(t * 6)],
      idle: [s.deepIdle ? 0.025 : 0.22, s.deepIdle ? 0.4 : 0.45, s.mic * 0.35],
      listening: [0.65, 0.95, s.mic],
      transcribing: [1.5, 1.0, 0.25],
      thinking: [2.1, 1.0, 0.3 + 0.2 * Math.sin(t * 9)],
      acting: [1.8, 1.0, 0.45 + 0.25 * Math.sin(t * 14)],
      speaking: [0.8, 0.95, s.voice],
      error: [0.12, 0.6, 0.05],
    }[s.state] ?? [0.22, 0.45, 0];
    const k = 1 - Math.exp(-dt * 4);
    this.speed += (ts - this.speed) * k;
    this.energy += (te - this.energy) * k;
    // The level rises at once and falls slowly, like a VU meter.
    this.level += (raw - this.level) * (raw > this.level ? 1 - Math.exp(-dt * 30) : 1 - Math.exp(-dt * 6));
    this.alert += ((s.state === 'error' ? 1 : 0) - this.alert) * k;
    this.privacy += ((s.privacy ? 1 : 0) - this.privacy) * k;
    this.phase += dt * this.speed;
  }
}

/** ClassicReactorRenderer, compact (the NUCLEUS: no outer brackets). */
function drawReactor(x, w, h, a) {
  const c = [w / 2, h / 2], R = Math.min(w, h) / 2 * 0.96;
  const primary = a.privacy > 0.5 ? PRIVACY : a.alert > 0.5 ? AMBER : CYAN;
  const e = a.energy, lv = a.level, PI = Math.PI;
  const reveal = (i) => { const v = Math.max(0, Math.min(1, (a.boot - i * 0.07) / 0.45)); return 1 - Math.pow(1 - v, 3); };
  const pt = (ang, r) => [c[0] + Math.cos(ang) * r, c[1] + Math.sin(ang) * r];
  // Two "contexts" as on the Mac: soft (added light) and glow (added light with a blur when JARVIS is active).
  const glowOn = e > 0.6, glowAlpha = 0.9 * Math.min(1, (e - 0.6) * 4);
  const use = (glow) => {
    x.globalCompositeOperation = 'lighter';
    if (glow && glowOn) { x.shadowColor = rgba(primary, glowAlpha); x.shadowBlur = GLOW * scaleOf(x); } else { x.shadowColor = 'transparent'; x.shadowBlur = 0; }
  };
  const stroke = (path, color, width) => { x.strokeStyle = color; x.lineWidth = width; x.setLineDash([]); x.stroke(path); };
  const arc = (r, from, to, width, color) => { const p = new Path2D(); p.arc(c[0], c[1], Math.max(0, r), from, to); x.lineCap = 'butt'; stroke(p, color, width); };
  const circle = (r, width, color, dash = []) => { const p = new Path2D(); p.arc(c[0], c[1], Math.max(0, r), 0, PI * 2); x.strokeStyle = color; x.lineWidth = width; x.setLineDash(dash); x.stroke(p); x.setLineDash([]); };
  const radial = (p, ang, r1, r2) => { p.moveTo(...pt(ang, r1)); p.lineTo(...pt(ang, r2)); };

  // Core glow.
  use(false);
  { const r = R * (0.62 + lv * 0.12);
    const g = x.createRadialGradient(c[0], c[1], 0, c[0], c[1], r);
    g.addColorStop(0, rgba(primary, 0.32 * e)); g.addColorStop(0.5, rgba(DEEP, 0.10 * e)); g.addColorStop(1, rgba(DEEP, 0));
    x.fillStyle = g; x.beginPath(); x.arc(c[0], c[1], r, 0, PI * 2); x.fill(); }

  // Data ring: 72 solid segments, light flowing in waves.
  let v = reveal(7);
  if (v > 0) {
    use(false);
    const r = R * 0.775 * v, inner = r - R * 0.011, outer = r + R * 0.011, n = 72, slot = PI * 2 / n;
    const rot = -a.phase * 0.06, flow = a.time * (0.5 + e * 1.6);
    const levels = [new Path2D(), new Path2D(), new Path2D(), new Path2D()];
    for (let i = 0; i < n; i++) {
      const wave = 0.5 + 0.5 * Math.sin(PI * 2 * (i / n) * 3 - flow);
      const level = Math.min(3, Math.floor(Math.pow(wave, 2.2) * 4));
      const s = rot + i * slot, t = s + slot * 0.72, p = levels[level];
      p.moveTo(...pt(s, inner)); p.arc(c[0], c[1], outer, s, t); p.arc(c[0], c[1], inner, t, s, true); p.closePath();
    }
    levels.forEach((p, i) => { x.fillStyle = rgba(primary, [0.12, 0.24, 0.40, 0.62][i] * v); x.fill(p); });
    circle(inner - R * 0.006, 0.5, rgba(primary, 0.3 * v));
    circle(outer + R * 0.006, 0.5, rgba(primary, 0.3 * v));
  }

  // Spectrum: 128 bars that grow with the voice.
  v = reveal(6);
  if (v > 0) {
    use(true);
    const base = R * 0.64 * v, t = a.time, outer = new Path2D(), inner = new Path2D();
    for (let i = 0; i < 128; i++) {
      const ang = i / 128 * PI * 2 - PI / 2;
      const nn = 0.5 + 0.5 * Math.sin(i * 0.71 + t * 7.3) * Math.cos(i * 0.23 - t * 4.1);
      const breath = 0.012 + 0.008 * Math.sin(t * 1.4 + i * 0.3);
      const len = R * (breath + lv * 0.17 * (0.25 + 0.75 * nn));
      radial(outer, ang, base, base + len); radial(inner, ang, base - len * 0.25, base);
    }
    const bright = 0.35 + 0.65 * Math.min(1, lv * 1.6);
    stroke(outer, rgba(primary, bright * v), 1.6);
    stroke(inner, rgba(primary, 0.35 * bright * v), 1);
  }

  // Scanner: a comet with a fading trail.
  v = reveal(5);
  if (v > 0) {
    use(true);
    const r = R * 0.535 * v;
    circle(r, 0.7, rgba(primary, 0.35 * v));
    const head = a.phase * 1.4, steps = 24;
    for (let s = 0; s < steps; s++) {
      const f = s / steps, from = head - 1.6 * (1 - f);
      arc(r, from, from + 1.6 / steps + 0.01, 2.4, rgba(ICE, Math.pow(f, 2) * (0.25 + 0.75 * e) * v));
    }
    circle(r - R * 0.03, 0.5, rgba(primary, 0.25 * v), [2, 5]);
  }

  // Heavy arcs.
  v = reveal(4);
  if (v > 0) {
    use(true);
    const r = R * 0.445 * v, rot = -a.phase * 0.45;
    for (const [offset, span] of [[0, 1.25], [1.7, 0.85], [3.05, 0.55], [4.1, 1.5]])
      arc(r, rot + offset, rot + offset + span, R * 0.03, rgba(primary, (0.55 + 0.35 * e) * v));
    circle(r - R * 0.03, 0.8, rgba(primary, 0.4 * v));
    const rot2 = a.phase * 0.8;
    for (let k = 0; k < 3; k++) { const s = rot2 + k * 2.094; arc(r + R * 0.035, s, s + 0.5, 1.2, rgba(ICE, 0.6 * v)); }
  }

  // Tick ring: small solid rectangles that turn slowly.
  v = reveal(3);
  if (v > 0) {
    use(false);
    const r = R * 0.355 * v, rot = a.phase * 0.18, major = new Path2D(), minor = new Path2D();
    const tick = (p, ang, r1, length, width) => {
      const half = width / 2, dx = Math.cos(ang), dy = Math.sin(ang), nx = -dy * half, ny = dx * half;
      const A = [c[0] + dx * r1, c[1] + dy * r1], B = [c[0] + dx * (r1 + length), c[1] + dy * (r1 + length)];
      p.moveTo(A[0] + nx, A[1] + ny); p.lineTo(B[0] + nx, B[1] + ny); p.lineTo(B[0] - nx, B[1] - ny); p.lineTo(A[0] - nx, A[1] - ny); p.closePath();
    };
    for (let i = 0; i < 60; i++) {
      const ang = rot + i / 60 * PI * 2;
      if (i % 5 === 0) tick(major, ang, r, R * 0.045, 2.6); else tick(minor, ang, r, R * 0.022, 1.6);
    }
    x.fillStyle = rgba(primary, 0.85 * v); x.fill(major);
    x.fillStyle = rgba(primary, 0.45 * v); x.fill(minor);
  }

  // Coils: ten trapezoids around the core.
  v = reveal(2);
  if (v > 0) {
    use(true);
    const r1 = R * 0.17 * v, r2 = R * 0.27 * v, rot = -a.phase * 0.12;
    for (let i = 0; i < 10; i++) {
      const mid = rot + i / 10 * PI * 2, w1 = 0.19, w2 = 0.16, p = new Path2D();
      p.moveTo(...pt(mid - w1, r1)); p.lineTo(...pt(mid - w2, r2)); p.lineTo(...pt(mid + w2, r2)); p.lineTo(...pt(mid + w1, r1)); p.closePath();
      x.fillStyle = rgba(primary, (0.18 + 0.25 * e + 0.3 * lv) * v); x.fill(p);
      stroke(p, rgba(primary, 0.85 * v), 1);
    }
    circle(r2 + R * 0.012, 1.5, rgba(primary, 0.9 * v));
    circle(r1 - R * 0.012, 1.2, rgba(primary, 0.8 * v));
  }

  // Core.
  v = reveal(1);
  if (v > 0) {
    use(true);
    const pulse = 1 + lv * 0.35 + 0.04 * Math.sin(a.time * 2.2), r = R * 0.13 * pulse * v;
    const g = x.createRadialGradient(c[0], c[1], 0, c[0], c[1], r);
    g.addColorStop(0, `rgba(255,255,255,${0.95 * v})`); g.addColorStop(1 / 3, rgba(ICE, 0.8 * v));
    g.addColorStop(2 / 3, rgba(primary, 0.5 * v)); g.addColorStop(1, rgba(primary, 0));
    x.fillStyle = g; x.beginPath(); x.arc(c[0], c[1], r, 0, PI * 2); x.fill();
    circle(R * 0.075 * v, 1.5, `rgba(255,255,255,${0.85 * v})`);
  }

  // Particles, in four brightness buckets.
  v = reveal(5);
  if (v > 0) {
    use(false);
    const buckets = [new Path2D(), new Path2D(), new Path2D(), new Path2D()];
    for (const p of a.particles) {
      const [px, py] = pt(p.angle + a.phase * p.speed, R * p.radius * v);
      const tw = 0.5 + 0.5 * Math.sin(a.time * 2 + p.twinkle), s = p.size * (1 + lv * 0.6);
      const b = buckets[Math.min(3, Math.floor(tw * 4))];
      b.moveTo(px + s / 2, py); b.ellipse(px, py, s / 2, s / 2, 0, 0, PI * 2);
    }
    buckets.forEach((p, i) => { x.fillStyle = rgba(ICE, (0.3 + 0.7 * i / 3) * 0.7 * v); x.fill(p); });
  }
  x.shadowBlur = 0; x.shadowColor = 'transparent';
  x.globalCompositeOperation = 'source-over';
}

/**
 * The reactor in a canvas (170×170 on the Mac). reactor.set({ state, mic, voice, privacy, deepIdle }).
 */
export function createReactor(canvas) {
  const x = canvas.getContext('2d');
  const input = { state: 'idle', mic: 0, voice: 0, privacy: false, deepIdle: false };
  const a = new Animator();
  let lastFrame = 0, frames = 0, fpsStart = performance.now(), fps = 0;
  function draw(now) {
    const scale = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(canvas.clientWidth * scale)), h = Math.max(1, Math.round(canvas.clientHeight * scale));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    a.step(now / 1000, input);
    x.setTransform(scale, 0, 0, scale, 0, 0);
    x.clearRect(0, 0, w / scale, h / scale);
    drawReactor(x, w / scale, h / scale, a);
  }
  const interval = () => input.state !== 'idle' ? 1000 / 30 : input.deepIdle ? 1000 / 6 : 1000 / 12;
  function frame(now) {
    requestAnimationFrame(frame);
    if (document.hidden || now - lastFrame < interval() - 2) return;
    lastFrame = now;
    draw(now);
    frames++;
    if (now - fpsStart > 1000) { fps = frames * 1000 / (now - fpsStart); frames = 0; fpsStart = now; }
  }
  requestAnimationFrame(frame);
  return {
    set(values) { Object.assign(input, values); },
    draw(now = performance.now()) { draw(now); },
    get fps() { return fps; },
  };
}
