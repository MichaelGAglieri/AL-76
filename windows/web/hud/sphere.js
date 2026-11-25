// AL-76's sphere ("rete neurale"), ported from the Mac (Sources/JARVIS/HUD/Reactor.swift) to three.js for
// WebView2. Same seeded random stream, same order of draws, same shaders: the neurons, axons, ribbons and
// plexus sit exactly where the user approved them on the Mac. Two levels of detail, as on the Mac:
// "full" (the HUD) and "light" (the Mac's compact geometry, for Base PCs without a real GPU).
import * as THREE from './vendor/three.module.min.js';
import { Quality } from './quality.js';

// ── Seeded random: xorshift on 64 bits, as SeededRandom in Swift ────────────────────────────────────
const MASK = (1n << 64n) - 1n;
class SeededRandom {
  constructor(seed) { this.s = (BigInt(seed) * 0x9E3779B97F4A7C15n) & MASK; }
  next() {
    let s = this.s;
    s ^= (s << 13n) & MASK;
    s ^= s >> 7n;
    s ^= (s << 17n) & MASK;
    this.s = s;
    return Number(s % 1000000n) / 1000000;
  }
}

// ── Small vector helpers (Float precision as in Swift's SIMD3<Float>) ──────────────────────────────
const f32 = Math.fround;
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const mul = (a, k) => [a[0] * k, a[1] * k, a[2] * k];
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const len = a => Math.sqrt(dot3(a, a));
const norm = a => { const l = len(a); return l > 0 ? mul(a, 1 / l) : [0, 0, 0]; };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dist = (a, b) => len(sub(a, b));

// ── Geometry: SphereGeometry.init(compact:) line by line ───────────────────────────────────────────
export function buildGeometry(compact) {
  const BASE = 0x4A4152564953;
  let random = new SeededRandom(BASE);
  const segs = [];   // a(4), b(4), style(4): opacity, width (points), flow coordinate, seed
  const pts = [];    // position(4), style(4): radius (points), brightness, seed, kind
  const r = (a, b) => f32(a + f32(random.next()) * (b - a));
  const unit = () => {
    const y = r(-1, 1), angle = r(0, 2 * Math.PI), s = Math.sqrt(Math.max(0, 1 - y * y));
    return [s * Math.cos(angle), y, s * Math.sin(angle)];
  };
  const line = (a, b, group, opacity, width, flow, seed) => segs.push(a[0], a[1], a[2], group, b[0], b[1], b[2], group, opacity, width, flow, seed);
  const dot = (p, group, radius, brightness, kind, seed) => {
    const sd = seed === undefined ? r(0, 20) : seed;
    pts.push(p[0], p[1], p[2], group, radius, brightness, sd, kind);
  };
  // A soft ribbon: several parallel strands plus two wide, faint passes for the glow.
  const ribbon = (steps, strands, group, plane, glow, seed, fade, width, path) => {
    for (let strand = 0; strand < strands; strand++) {
      const s = strands === 1 ? 0 : strand / (strands - 1) - 0.5;
      const wobble = r(0, 6.28);
      const core = 1 - Math.abs(s) * 2;
      let previous = null;
      for (let step = 0; step <= steps; step++) {
        const f = step / steps;
        const c = path(f);
        const tangent = norm(add(sub(path(Math.min(1, f + 0.01)), path(Math.max(0, f - 0.01))), [0, 0, 1e-5]));
        let side = cross(tangent, plane ?? norm(add(c, [1e-4, 1e-4, 0])));
        side = len(side) < 1e-4 ? [0, 0, 1] : norm(side);
        const lift = cross(side, tangent);
        const p = add(add(c, mul(side, s * width(f) + 0.004 * Math.sin(f * 23 + wobble))),
                      mul(lift, 0.012 * Math.abs(s) * Math.sin(f * 17 + wobble * 1.3)));
        if (previous) line(previous, p, group, (0.30 + 0.26 * core) * fade, 0.85 + 0.35 * core, f, seed);
        previous = p;
      }
    }
    if (glow <= 0) return;
    for (let step = 0; step < steps; step += 2) {
      const a = path(step / steps), b = path(Math.min(steps, step + 2) / steps);
      const f = step / steps;
      const swell = 0.4 + 0.6 * Math.max(0, Math.sin(Math.PI * f));
      line(a, b, group, 0.085 * glow * swell * fade, 22, f, seed);
      line(a, b, group, 0.15 * glow * swell * fade, 7, f, seed);
    }
  };

  // 1. Plexus: nodes in the whole volume, denser on the shell, joined to nearby nodes.
  const nodeCount = compact ? 160 : 1500;
  const nodes = [];
  for (let i = 0; i < nodeCount; i++) {
    const radius = i % 9 === 0 ? r(0.08, 0.35) : i % 5 === 0 ? r(0.35, 0.8) : r(0.84, 0.97);
    const p = mul(unit(), radius);
    nodes.push(p);
    dot(p, 0, compact ? 1.8 : 2.7, r(0.8, 1.6), 0);
  }
  const cell = 0.25;
  const key = p => `${Math.floor(p[0] / cell)},${Math.floor(p[1] / cell)},${Math.floor(p[2] / cell)}`;
  const neighbours = (grid, p) => {
    const kx = Math.floor(p[0] / cell), ky = Math.floor(p[1] / cell), kz = Math.floor(p[2] / cell), out = [];
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) for (let dz = -1; dz <= 1; dz++)
      for (const j of grid.get(`${kx + dx},${ky + dy},${kz + dz}`) ?? []) out.push(j);
    return out;
  };
  let grid = new Map();
  nodes.forEach((p, i) => { const k = key(p); if (!grid.has(k)) grid.set(k, []); grid.get(k).push(i); });
  nodes.forEach((p, i) => {
    const near = neighbours(grid, p).filter(j => j !== i).map(j => [dist(p, nodes[j]), j]).sort((x, y) => x[0] - y[0]);
    for (const [d, j] of near.slice(0, compact ? 3 : 4))
      if (j > i && d < cell) line(p, nodes[j], 0, r(0.45, 0.72), 0.85, r(0, 1), r(0, 20));
  });

  // 2. Neural network (group 2): neurons with dendrites, joined by glowing axons.
  const neuronCount = compact ? 5 : 44;
  const neurons = [];
  for (let n = 0; n < neuronCount; n++) {
    const central = !compact && neurons.length === 0;
    let p = [0, 0, 0];
    if (!central) {
      let best = -1;
      for (let t = 0; t < (compact ? 1 : 8); t++) {
        const c = mul(unit(), r(0.25, 0.92));
        const d = neurons.length ? Math.min(...neurons.map(x => dist(x.p, c))) : 1;
        if (d > best) { best = d; p = c; }
      }
    }
    const seed = r(0, 1);
    neurons.push({ p, seed });
    dot(p, 2, compact ? 3.5 : central ? 17 : 8, central ? 1.5 : r(1.0, 1.3), 3, seed);
    for (let k = 0; k < (compact ? 2 : central ? 12 : 6); k++) {
      let direction = unit(), q = p;
      for (let level = 0; level < (compact ? 2 : 3); level++) {
        const length = (level === 0 ? r(0.10, 0.2) : level === 1 ? r(0.05, 0.1) : r(0.03, 0.07)) * (central ? 1.9 : 1);
        const bend = mul(unit(), 0.6);
        let previous = q;
        for (let step = 1; step <= 8; step++) {
          const f = step / 8;
          const next = add(q, mul(norm(add(direction, mul(bend, f))), length * f));
          line(previous, next, 2, 0.5 * (1 - f * 0.5), (level === 0 ? 0.9 : 0.7) * (central ? 2.2 : 1), (level + f) * 0.12, seed);
          previous = next;
        }
        q = previous;
        if (level < 2 && !compact) dot(q, 2, central ? 3 : 2, 0.9, 0);
        direction = norm(add(direction, mul(unit(), 0.9)));
      }
    }
  }
  const axonPath = (a, b, control) => f => {
    let p = add(add(mul(a, (1 - f) * (1 - f)), mul(control, 2 * f * (1 - f))), mul(b, f * f));
    const l = len(p);
    if (l > 0.95) p = mul(p, 0.95 / l);
    return p;
  };
  const axonWidth = f => 0.006 + 0.032 * Math.pow(Math.max(0, Math.sin(Math.PI * f)), 0.8);
  const nearestOf = i => neurons.map((_, j) => j).filter(j => j !== i)
    .sort((x, y) => dist(neurons[i].p, neurons[x].p) - dist(neurons[i].p, neurons[y].p));
  const joined = new Set();
  const strands = compact ? 2 : 6;
  neurons.forEach((n, i) => {
    const near = nearestOf(i);
    const targets = near.slice(0, compact ? 2 : 3);
    const far = random.next() < 0.35 ? near[near.length - 1] : null;
    if (far !== null) targets.push(far);
    for (const j of targets) {
      const id = Math.min(i, j) * 100 + Math.max(i, j);
      if (joined.has(id)) continue;
      joined.add(id);
      if (j === far) { for (let k = 0; k < 3 + strands; k++) random.next(); continue; }
      const a = n.p, b = neurons[j].p;
      const along = norm(sub(b, a));
      let side = unit();
      side = sub(side, mul(along, dot3(side, along)));
      side = len(side) < 1e-3 ? [0, 0, 0] : norm(side);
      const control = add(mul(add(a, b), 0.5), mul(side, dist(a, b) * (random.next() < 0.45 ? 0.05 : 0.22)));
      ribbon(compact ? 16 : 44, strands, 2, null, 1, n.seed, 1, axonWidth, axonPath(a, b, control));
    }
  });

  // 3. Ribbons wrapping the shell: they give the sphere its outline.
  const shellRibbon = (radiusRange, steps, strandCount) => {
    const axis = unit();
    const right = norm(cross(axis, Math.abs(axis[1]) < 0.9 ? [0, 1, 0] : [1, 0, 0]));
    const up = cross(axis, right);
    const start = r(0, 2 * Math.PI), span = r(2.2, 4.2), seed = r(0, 20), radius = r(...radiusRange), wave = r(0.25, 0.5);
    ribbon(steps, strandCount, 0, null, 0.8, seed, 1, f => 0.012 + 0.06 * Math.pow(Math.max(0, Math.sin(Math.PI * f)), 0.7), f => {
      const t = start + span * f;
      return mul(norm(add(add(mul(right, Math.cos(t)), mul(up, Math.sin(t))), mul(axis, wave * Math.sin(t * 2 + seed)))), radius);
    });
  };
  for (let k = 0; k < (compact ? 4 : 13); k++) shellRibbon([0.84, 0.93], compact ? 36 : 110, compact ? 3 : 8);

  // 5. Broken rim (compact only).
  for (let k = 0; k < (compact ? 14 : 0); k++) {
    const angle = r(0, 2 * Math.PI), length = r(0.015, 0.07), radius = r(0.95, 1.05), seed = r(0, 20);
    const thick = random.next() < 0.3;
    line([radius * Math.cos(angle), radius * Math.sin(angle), 0], [radius * Math.cos(angle + length), radius * Math.sin(angle + length), 0],
         1, thick ? 0.95 : 0.6, thick ? 3 : 1.2, r(0, 1), seed);
  }
  // 6. The data screens were removed on the Mac; their random numbers are still drawn.
  for (let i = 0; i < (compact ? 6 : 8); i++) { unit(); i % 3 === 0 ? r(0.3, 0.8) : r(0.85, 0.98); r(4, 7); r(0.65, 1); }

  // 7. Core: a wide halo and a white-hot centre.
  dot([0, 0, 0], 1, compact ? 16 : 46, 1.0, 2);
  dot([0, 0, 0], 1, compact ? 6 : 9, 1.2, 2);

  if (!compact) {
    // 8. Denser outline (Mac, 15 November): 900 more shell nodes and three more ribbons, own random stream.
    random = new SeededRandom(BASE + 15);
    const first = nodes.length;
    for (let k = 0; k < 900; k++) {
      const p = mul(unit(), r(0.88, 0.985));
      nodes.push(p);
      dot(p, 0, 2.7, r(0.8, 1.6), 0);
    }
    grid = new Map();
    nodes.forEach((p, i) => { const k = key(p); if (!grid.has(k)) grid.set(k, []); grid.get(k).push(i); });
    for (let i = first; i < nodes.length; i++) {
      const p = nodes[i];
      const near = neighbours(grid, p).filter(j => j !== i).map(j => [dist(p, nodes[j]), j]).sort((x, y) => x[0] - y[0]);
      for (const [d, j] of near.slice(0, 3))
        if ((j < first || j > i) && d < cell) line(p, nodes[j], 0, r(0.45, 0.72), 0.85, r(0, 1), r(0, 20));
    }
    for (let k = 0; k < 3; k++) shellRibbon([0.88, 0.94], 110, 8);

    // 9. More axons (Mac, 16 November): each neuron also to its 4th and 5th nearest, dimmer when long.
    random = new SeededRandom(BASE + 16);
    neurons.forEach((n, i) => {
      for (const j of nearestOf(i).slice(3, 5)) {
        const id = Math.min(i, j) * 100 + Math.max(i, j);
        if (joined.has(id)) continue;
        joined.add(id);
        const a = n.p, b = neurons[j].p;
        const along = norm(sub(b, a));
        let side = unit();
        side = sub(side, mul(along, dot3(side, along)));
        side = len(side) < 1e-3 ? [0, 0, 0] : norm(side);
        const control = add(mul(add(a, b), 0.5), mul(side, dist(a, b) * (random.next() < 0.45 ? 0.05 : 0.22)));
        const fade = Math.min(1, Math.max(0.35, 1.35 - dist(a, b) * 1.3));
        ribbon(44, 6, 2, null, 1, n.seed, fade, axonWidth, axonPath(a, b, control));
      }
    });
  }
  return { segments: new Float32Array(segs), points: new Float32Array(pts) };
}

// ── Motion: ReactorAnimator + SphereMotion + SphereUniforms ─────────────────────────────────────────
// States: booting, idle, listening, transcribing, thinking, acting, speaking, error.
const TARGETS = {
  booting: (t) => [1.4, 0.7, 0.15 + 0.15 * Math.sin(t * 6)],
  idle: (t, deep, mic) => [deep ? 0.025 : 0.22, deep ? 0.4 : 0.45, mic * 0.35],
  listening: (t, deep, mic) => [0.65, 0.95, mic],
  transcribing: () => [1.5, 1.0, 0.25],
  thinking: (t) => [2.1, 1.0, 0.3 + 0.2 * Math.sin(t * 9)],
  acting: (t) => [1.8, 1.0, 0.45 + 0.25 * Math.sin(t * 14)],
  speaking: (t, deep, mic, voice) => [0.8, 0.95, voice],
  error: () => [0.12, 0.6, 0.05],
};

const COLORS = {
  cyan: [0.31, 0.86, 1.0], ice: [0.80, 0.97, 1.0], deep: [0.02, 0.45, 0.80],
  amber: [1.0, 0.62, 0.20], privacy: [0.55, 0.60, 0.66],
};
const mix3 = (a, b, k) => [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k];

class Motion {
  constructor() {
    this.start = performance.now() / 1000; this.last = null;
    // ReactorAnimator
    this.phase = 0; this.time = 0; this.speed = 0.3; this.energy = 0.4; this.level = 0; this.boot = 0; this.alert = 0; this.privacy = 0;
    // SphereMotion
    this.flow = 0.5; this.direction = -1; this.density = 0; this.mlevel = 0; this.swell = 0; this.spin = 0; this.idleBoost = 0; this.calm = 0; this.fire = 0;
  }
  step(now, s) {
    const dtRaw = this.last === null ? 0 : now - this.last;
    this.last = now;
    // ReactorAnimator.step
    let dt = Math.min(1 / 20, dtRaw);
    this.time = now - this.start;
    this.boot = Math.min(1, this.time / 2.2);
    const [ts, te, raw] = TARGETS[s.state](this.time, s.deepIdle, s.mic, s.voice);
    let k = 1 - Math.exp(-dt * 4);
    this.speed += (ts - this.speed) * k;
    this.energy += (te - this.energy) * k;
    this.level += (raw - this.level) * (raw > this.level ? 1 - Math.exp(-dt * 30) : 1 - Math.exp(-dt * 6));
    this.alert += ((s.state === 'error' ? 1 : 0) - this.alert) * k;
    this.privacy += ((s.privacy ? 1 : 0) - this.privacy) * k;
    this.phase += dt * this.speed;
    // SphereMotion.step
    dt = Math.min(0.1, dtRaw);
    const ease = 1 - Math.exp(-dt * 3);
    this.idleBoost += ((s.state === 'idle' && !s.deepIdle ? 0.5 : 0) - this.idleBoost) * ease;
    this.calm += ((s.state === 'idle' ? (s.deepIdle ? 0.6 : 1) : s.state === 'speaking' ? 1.4 : 0) - this.calm) * (1 - Math.exp(-dt * 1.5));
    this.spin += dt * (this.speed + this.idleBoost);
    this.fire = (this.fire + dt * (0.45 + this.speed * 0.5 + this.idleBoost * 0.3)) % 400;
    this.direction += ((s.state === 'listening' ? 1 : -1) - this.direction) * ease;
    this.density += ((s.state === 'thinking' || s.state === 'acting' ? 1 : 0) - this.density) * ease;
    this.mlevel += (this.level - this.mlevel) * (1 - Math.exp(-dt * 12));
    this.swell += (this.mlevel * (s.state === 'speaking' ? 0.065 : 0.025) - this.swell) * (1 - Math.exp(-dt * 10));
    this.flow = (this.flow + dt * this.speed * 0.24 * this.direction) % 1;
    if (this.flow < 0) this.flow += 1;
  }
  // SphereUniforms.init
  uniforms(u) {
    const yaw = this.spin * 0.14, tilt = 0.24 + Math.sin(this.spin * 0.09) * 0.18;
    const muted = 1 - this.privacy * 0.85;
    const breath = 1 + 0.008 * Math.sin(this.time * 1.35);
    u.motion.value.set(this.time, this.spin, this.energy, this.mlevel);
    u.state.value.set(this.boot, this.alert, this.privacy, this.flow);
    u.rotation.value.set(Math.cos(yaw), Math.sin(yaw), Math.cos(tilt), Math.sin(tilt));
    u.viewport.value.w = this.calm;
    u.dynamics.value.set(breath + this.swell * muted, this.speed, this.fire, this.density);
    const primary = mix3(mix3(COLORS.cyan, COLORS.amber, this.alert), COLORS.privacy, this.privacy);
    const grey = Math.max(this.alert, this.privacy);
    u.primary.value.set(...primary, 1);
    u.ice.value.set(...mix3(COLORS.ice, primary, grey), 1);
    u.deep.value.set(...mix3(COLORS.deep, mul(primary, 0.45), grey), 1);
  }
}

// ── Shaders: the Metal ones, in GLSL ES 3.0 ─────────────────────────────────────────────────────────
const COMMON = /* glsl */`
precision highp float;
uniform vec4 motion, state, rotation, viewport, primary, ice, deep, dynamics;
vec3 project(vec4 q) {
  vec3 p = q.xyz;
  float live = 1.0 - state.z * 0.85;
  float ripple = sin(dot(p, vec3(4,7,3)) - motion.y * 2.5);
  p *= dynamics.x + ripple * (motion.w * 0.018 + (dynamics.x - 1.0) * 0.55) * live;
  if (q.w < 0.5 || q.w > 1.5) {
    float t = motion.x;
    float wave = sin(dot(p, vec3(1.1,0.8,0.9)) + t*0.55) * 0.7 + sin(t*0.4) * 0.3;
    p *= 1.0 + wave * 0.014 * (0.6 + 0.4 * live);
    float calm = viewport.w * live;
    if (calm > 0.001) {
      float s = motion.y;
      float r1 = sin(dot(p, vec3(4,7,3)) - s*2.2);
      float r2 = sin(dot(p, vec3(-5,2,4)) + s*1.6 + 1.3);
      p *= 1.0 + (r1 * 0.034 + r2 * 0.017) * calm;
    }
  }
  if (q.w > 0.5 && q.w < 1.5) {
    float s = -motion.y * 0.30, cs = cos(s), sn = sin(s);
    p.xy = vec2(p.x*cs - p.y*sn, p.x*sn + p.y*cs);
    float t = 0.16 * sin(motion.y * 0.09), w = 0.18 * sin(motion.y * 0.07);
    p.yz = vec2(p.y*cos(t) - p.z*sin(t), p.y*sin(t) + p.z*cos(t));
    p.xz = vec2(p.x*cos(w) + p.z*sin(w), -p.x*sin(w) + p.z*cos(w));
  } else {
    p.xz = vec2(p.x*rotation.x + p.z*rotation.y, -p.x*rotation.y + p.z*rotation.x);
    p.yz = vec2(p.y*rotation.z - p.z*rotation.w, p.y*rotation.w + p.z*rotation.z);
    float calm = viewport.w * live;
    if (calm > 0.001) {
      float s = motion.y;
      float a = s * 0.31;
      vec2 axis = vec2(cos(a), sin(a));
      float sway = sin(s * 0.9) * 0.018 * calm;
      p.xy += (axis * dot(p.xy, axis) * 2.0 - p.xy) * sway;
      float rr = length(p.xy), angle = atan(p.y, p.x);
      float edge = smoothstep(0.55, 0.95, rr);
      float rim = sin(angle * 3.0 - s * 1.7) * 0.6 + sin(angle * 5.0 + s * 1.2 + 0.7) * 0.4;
      p.xy *= 1.0 + rim * 0.022 * calm * edge;
    }
  }
  float reveal = smoothstep(0.0, 0.85, state.x);
  float scale = 0.93 * (0.86 + 0.14 * reveal) / (1.12 - p.z*0.16);
  vec2 aspect = min(viewport.x, viewport.y) / viewport.xy;
  return vec3(p.xy * scale * aspect, p.z);
}
float pulseAt(float beat) { return pow(max(0.0, 1.0 - abs(beat - 0.5) * 2.0), 14.0); }
float cycleOf(float seed) { return fract(dynamics.z * 0.25 + seed); }
float fireAt(float coordinate, float seed) {
  float cycle = cycleOf(seed);
  float d = (coordinate - cycle / 0.4) * 9.0;
  float pulse = exp(-d * d) * (1.0 - smoothstep(0.38, 0.45, cycle));
  float lit = 0.22 * (1.0 - smoothstep(0.3, 0.45, cycle)) * smoothstep(0.0, 0.03, cycle);
  float activity = (0.6 + motion.z * 0.5) * (1.0 - state.z * 0.8);
  return 0.65 + (pulse * 3.2 + lit) * activity;
}
float limb(vec3 original, float z) {
  float r = length(original);
  float l = 1.0 - min(1.0, abs(z) / max(r, 0.2));
  return mix(1.0, mix(0.8, 1.5, l * l), smoothstep(0.7, 0.85, r));
}
float flowAt(float coordinate, float seed) {
  float shift = state.w + seed;
  float pulse = mix(pulseAt(fract(coordinate + shift)), pulseAt(fract(coordinate * 3.0 + shift)), dynamics.w);
  float activity = (0.22 + motion.z * 0.65 + min(dynamics.y, 2.1) * 0.12) * (1.0 - state.z * 0.8);
  return 0.60 + pulse * activity * 2.3;
}
`;

const LINE_VERTEX = COMMON + /* glsl */`
in vec2 corner;
in vec4 a, b, style;
out vec2 vUv; out float vBrightness;
void main() {
  vec3 pa = project(a), pb = project(b);
  vec2 direction = (pb.xy - pa.xy) * viewport.xy;
  vec2 normal = vec2(-direction.y, direction.x) / max(length(direction), 0.001);
  vec3 p = mix(pa, pb, (corner.x + 1.0) * 0.5);
  float width = style.y * viewport.z * (1.0 + motion.w * 0.3);
  gl_Position = vec4(p.xy + normal * corner.y * max(width, 0.75) / viewport.xy, 0.0, 1.0);
  vUv = corner;
  float m = a.w > 1.5 ? fireAt(style.z, style.w) : flowAt(style.z, style.w);
  float edge = a.w < 0.5 ? limb(a.xyz, p.z) : 1.0;
  vBrightness = style.x * (0.45 + 0.55 * smoothstep(-0.95, 0.9, p.z)) * edge * m * smoothstep(0.04, 0.85, state.x);
}`;

const LINE_FRAGMENT = /* glsl */`
precision highp float;
uniform vec4 motion, primary, ice, deep;
in vec2 vUv; in float vBrightness;
out vec4 color;
void main() {
  float coverage = exp(-vUv.y * vUv.y * 3.8);
  float alpha = clamp(vBrightness * coverage * (0.75 + motion.z * 0.5), 0.0, 1.0);
  vec3 c = mix(mix(deep.rgb, primary.rgb, 0.95), ice.rgb, min(vBrightness * 0.35, 0.6));
  color = vec4(c * alpha, alpha);
}`;

const POINT_VERTEX = COMMON + /* glsl */`
in vec2 corner;
in vec4 position4, style;
out vec2 vUv; out float vBrightness; out float vKind;
void main() {
  vec3 p = project(position4);
  float core = step(1.5, style.w) * step(style.w, 2.5);
  float soma = step(2.5, style.w);
  float cycle = cycleOf(style.z);
  float flash = soma * (exp(-cycle * 9.0) + 0.35 * exp(-(1.0 - cycle) * 30.0)) * (1.0 - state.z * 0.8);
  float radius = style.x * viewport.z * (1.0 + motion.w * (core > 0.5 ? 0.8 : 0.3)) * (1.0 + 0.7 * flash);
  gl_Position = vec4(p.xy + corner * radius * 2.0 / viewport.xy, 0.0, 1.0);
  vUv = corner; vKind = style.w;
  float twinkle = 0.72 + 0.28 * sin(motion.y * 2.0 + style.z);
  float edge = position4.w < 0.5 ? limb(position4.xyz, p.z) : 1.0;
  vBrightness = style.y * mix(0.4 + 0.6 * smoothstep(-0.9, 0.9, p.z), 1.0, core) * edge
              * mix(twinkle, 1.0, max(core, soma)) * (1.0 + 2.2 * flash) * smoothstep(0.0, 0.7, state.x);
}`;

const POINT_FRAGMENT = /* glsl */`
precision highp float;
uniform vec4 motion, primary, ice;
in vec2 vUv; in float vBrightness; in float vKind;
out vec4 color;
void main() {
  float r = length(vUv);
  float alpha; vec3 c;
  if (vKind > 2.5) {
    alpha = (exp(-r*r*4.0)*0.45 + exp(-r*r*28.0)*1.0) * vBrightness;
    c = mix(mix(primary.rgb, ice.rgb, exp(-r*r*10.0)), vec3(1.0), clamp(vBrightness - 1.2, 0.0, 1.0) * exp(-r*r*30.0));
    alpha = clamp(alpha, 0.0, 1.0) * (1.0 - smoothstep(0.85, 1.0, r));
  } else if (vKind > 1.5) {
    float halo = exp(-r*r*5.0)*0.42 + exp(-r*r*22.0)*0.45;
    float center = exp(-r*r*140.0)*1.2;
    alpha = (halo + center) * (0.75 + motion.z*0.25 + motion.w*0.5) * vBrightness;
    c = mix(mix(primary.rgb, ice.rgb, exp(-r*r*18.0)), vec3(1.0), exp(-r*r*90.0));
    alpha = clamp(alpha, 0.0, 1.0) * (1.0 - smoothstep(0.85, 1.0, r));
  } else {
    alpha = (exp(-r*r*6.0)*0.45 + exp(-r*r*45.0)*0.95) * vBrightness * (0.75 + motion.z*0.3 + motion.w*0.4);
    c = mix(primary.rgb, ice.rgb, exp(-r*r*8.0));
    alpha = clamp(alpha, 0.0, 1.0) * (1.0 - smoothstep(0.85, 1.0, r));
  }
  color = vec4(c * alpha, alpha);
}`;

const CORNERS = new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]);

function material(vertexShader, fragmentShader, uniforms) {
  return new THREE.RawShaderMaterial({
    glslVersion: THREE.GLSL3, vertexShader, fragmentShader, uniforms,
    transparent: true, depthTest: false, depthWrite: false,
    // As on the Mac: colours add up, alpha is a premultiplied union (transparent window, no black boxes).
    blending: THREE.CustomBlending, blendEquation: THREE.AddEquation,
    blendSrc: THREE.OneFactor, blendDst: THREE.OneFactor,
    blendSrcAlpha: THREE.OneFactor, blendDstAlpha: THREE.OneMinusSrcAlphaFactor,
  });
}

function instanced(count, attributes) {
  const g = new THREE.InstancedBufferGeometry();
  g.setAttribute('corner', new THREE.BufferAttribute(CORNERS, 2));
  for (const [name, array, stride, offset] of attributes) {
    const buffer = new THREE.InstancedInterleavedBuffer(array, stride, 1);
    g.setAttribute(name, new THREE.InterleavedBufferAttribute(buffer, 4, offset));
  }
  g.instanceCount = count;
  // No `position` attribute, so three.js cannot count the vertices: six corners per quad.
  g.setDrawRange(0, 6);
  return g;
}

/**
 * The sphere in a canvas. `detail`: "full" or "light". The state comes from outside (the app sends it):
 * sphere.set({ state: 'speaking', voice: 0.4 }). Frame rate as on the Mac: 30 while working, 20 waiting,
 * 6 after a minute of waiting (deepIdle), nothing while hidden.
 */
export function createSphere(canvas, { detail = 'full' } = {}) {
  const geometry = buildGeometry(detail === 'light');
  const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, premultipliedAlpha: true, antialias: false, powerPreference: 'high-performance' });
  renderer.setClearColor(0x000000, 0);
  const quality = new Quality(renderer);
  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
  const uniforms = {
    motion: { value: new THREE.Vector4() }, state: { value: new THREE.Vector4(0, 0, 0, 0) },
    rotation: { value: new THREE.Vector4(1, 0, 1, 0) }, viewport: { value: new THREE.Vector4(980, 980, 2, 0) },
    primary: { value: new THREE.Vector4() }, ice: { value: new THREE.Vector4() }, deep: { value: new THREE.Vector4() },
    dynamics: { value: new THREE.Vector4(1, 1, 0, 0) },
  };
  const lines = new THREE.Mesh(instanced(geometry.segments.length / 12,
    [['a', geometry.segments, 12, 0], ['b', geometry.segments, 12, 4], ['style', geometry.segments, 12, 8]]),
    material(LINE_VERTEX, LINE_FRAGMENT, uniforms));
  const dots = new THREE.Mesh(instanced(geometry.points.length / 8,
    [['position4', geometry.points, 8, 0], ['style', geometry.points, 8, 4]]),
    material(POINT_VERTEX, POINT_FRAGMENT, uniforms));
  lines.frustumCulled = dots.frustumCulled = false;
  scene.add(lines, dots);

  const input = { state: 'idle', deepIdle: false, privacy: false, mic: 0, voice: 0 };
  const motion = new Motion();
  let lastFrame = 0, frames = 0, fpsStart = performance.now(), fps = 0, stopped = false;

  function resize() {
    const scale = quality.ratio;
    const w = Math.max(1, Math.round(canvas.clientWidth * scale)), h = Math.max(1, Math.round(canvas.clientHeight * scale));
    if (canvas.width !== w || canvas.height !== h) renderer.setSize(w, h, false);
    uniforms.viewport.value.set(w, h, scale, uniforms.viewport.value.w);
  }
  function interval() {
    if (input.state !== 'idle') return 1000 / 30;
    return input.deepIdle ? 1000 / 6 : 1000 / 20;
  }
  function draw(now) {
    resize();
    motion.step(now / 1000, input);
    motion.uniforms(uniforms);
    renderer.render(scene, camera);
  }
  function frame(now) {
    if (stopped) return;
    requestAnimationFrame(frame);
    if (document.hidden || now - lastFrame < interval() - 2) return;
    lastFrame = now;
    draw(now);
    frames++;
    if (now - fpsStart > 1000) { fps = frames * 1000 / (now - fpsStart); frames = 0; fpsStart = now; quality.feed(fps, 1000 / interval()); }
  }
  requestAnimationFrame(frame);
  return {
    quality,
    set(values) { Object.assign(input, values); },
    /** One frame now, even while hidden (tests and snapshots). */
    draw(now = performance.now()) { draw(now); },
    get fps() { return fps; },
    /** The frame rate wanted right now (30 working, 20 waiting, 6 after a minute). */
    get target() { return 1000 / interval(); },
    /** Stops drawing and frees the GPU (the page swaps in the light sphere). */
    dispose() { stopped = true; renderer.dispose(); renderer.forceContextLoss(); },
    get counts() { return { segments: geometry.segments.length / 12, points: geometry.points.length / 8 }; },
    renderer,
  };
}
