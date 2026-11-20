// AL-76 SATELLITE for Windows: the Mac's small 3D satellite (Sources/JARVIS/HUD/Satellite.swift), itself a port of
// the three.js preview the user approved on 10 November, with the detail added on 11 November. Same sizes, same
// materials, same canvas textures (same random seeds), same motion per state, same frame rates.
import * as THREE from './vendor/three.module.min.js';

// The picture follows the Mac's SceneKit, not the old three.js preview: colours are sRGB (colour managed), canvas
// textures were brightened on the Mac so SceneKit reads them as they are (here: linear, no colour space), lights in
// SceneKit units / 1000, no tone mapping, the same painted studio as reflections (see SHINE and ENV below).
// SceneKit applies euler angles as Rz·Ry·Rx: every node here uses the "ZYX" order.

/** The Mac's SatelliteRandom (an LCG), so textures and particles are the same at every launch and on both systems. */
class SatelliteRandom {
  constructor(seed) { this.state = (BigInt(seed) * 0x9E3779B97F4A7C15n) & 0xFFFFFFFFFFFFFFFFn; }
  next() {
    this.state = (this.state * 6364136223846793005n + 1442695040888963407n) & 0xFFFFFFFFFFFFFFFFn;
    return Number(this.state >> 33n) / 2147483648;
  }
}

// SceneKit spreads the reflections of shiny metal more than three.js: matched against renders of the Mac's own
// SatelliteRig (same pose and time): mean colour within 3%, solar wings identical.
const SHINE = 2.2, ENV = 2.4;
const BLUE = 0x4cc2ff, AMBER = 0xff9e33, PRIVACY = 0x8c99a8;

function tex(w, h, draw, repeat) {
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  draw(c.getContext('2d'), w, h);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  if (repeat) t.repeat.set(...repeat);
  t.anisotropy = 4;
  return t;
}

function textures() {
  let g = new SatelliteRandom(3);
  const hex = tex(256, 256, (x, w, h) => {
    x.fillStyle = '#8a929e'; x.fillRect(0, 0, w, h);
    const r = 18;
    for (let j = -1; j < 10; j++) for (let i = -1; i < 10; i++) {
      const cx = i * r * 1.73 + (j % 2 ? r * 0.866 : 0), cy = j * r * 1.5;
      x.beginPath();
      for (let k = 0; k < 6; k++) { const a = k / 6 * Math.PI * 2 + Math.PI / 6; x.lineTo(cx + Math.cos(a) * r * 0.95, cy + Math.sin(a) * r * 0.95); }
      x.closePath();
      const v = 125 + g.next() * 30;
      x.fillStyle = `rgb(${v},${v + 6},${v + 14})`; x.fill();
      x.strokeStyle = 'rgba(30,35,45,.7)'; x.lineWidth = 1.5; x.stroke();
      x.fillStyle = 'rgba(30,35,45,.6)'; x.beginPath(); x.arc(cx, cy, 1.4, 0, 7); x.fill();
    }
  }, [2, 1]);
  g = new SatelliteRandom(11);
  const foil = tex(256, 256, (x, w, h) => {
    x.fillStyle = '#b8862e'; x.fillRect(0, 0, w, h);
    for (let i = 0; i < 260; i++) {
      const px = g.next() * 256, py = g.next() * 256, light = g.next() < 0.5, alpha = 0.25 + g.next() * 0.3;
      x.strokeStyle = `rgba(${light ? '255,230,160' : '90,60,10'},${alpha})`;
      x.lineWidth = 1 + g.next() * 2;
      x.beginPath(); x.moveTo(px, py); x.lineTo(px + (g.next() - 0.5) * 50, py + (g.next() - 0.5) * 30); x.stroke();
    }
  }, [2, 1]);
  g = new SatelliteRandom(17);
  const cell = tex(128, 256, (x, w, h) => {
    x.fillStyle = '#0a1630'; x.fillRect(0, 0, w, h);
    for (let j = 0; j < 16; j++) for (let i = 0; i < 8; i++) {
      const v = 20 + g.next() * 12;
      x.fillStyle = `rgb(${v},${v + 30},${v + 80})`; x.fillRect(i * 16 + 1, j * 16 + 1, 14, 14);
      x.fillStyle = 'rgba(140,190,255,.25)'; x.fillRect(i * 16 + 2, j * 16 + 7, 12, 1);
    }
    x.strokeStyle = 'rgba(200,215,230,.6)'; x.lineWidth = 2; x.strokeRect(1, 1, w - 2, h - 2);
  });
  const glow = tex(64, 64, (x) => {
    const q = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    q.addColorStop(0, 'rgba(255,255,255,1)'); q.addColorStop(0.25, 'rgba(255,255,255,.45)'); q.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = q; x.fillRect(0, 0, 64, 64);
  });
  glow.colorSpace = THREE.SRGBColorSpace;
  return { hex, foil, cell, glow };
}

/** The Mac's studio (Satellite.swift environment()): a dark room (#121822) with five soft light panels, painted as
 * an equirectangular image and read as sRGB. Painted with three.js's equirect mapping, so each panel lies in the
 * same direction as on the Mac. */
function environment(renderer) {
  const w = 512, h = 256;
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  const x = c.getContext('2d');
  x.fillStyle = '#121822'; x.fillRect(0, 0, w, h);
  const panels = [[12, [4.2, 4.1, 4], [1, 10, 3]], [3, [2.2, 2.3, 2.5], [-10, 3, 4]], [3, [0.5, 1.4, 3], [10, 2, -3]],
                  [8, [1.3, 1.4, 1.6], [0, 2, 12]], [10, [0.3, 0.6, 1.4], [0, -9, 0]]];
  for (const [size, col, dir] of panels) {
    const dist = Math.hypot(...dir), d = dir.map(v => v / dist);
    const u = (Math.atan2(d[2], d[0]) / (2 * Math.PI) + 0.5) * w, v = Math.acos(d[1]) / Math.PI * h;
    const radius = Math.atan(size / 2 / dist) / Math.PI * h * 1.2;
    const rgb = col.map(k => Math.min(255, Math.round(k * 255 / 2.2))).join(',');
    for (const shift of [-w, 0, w]) {
      const q = x.createRadialGradient(u + shift, v, 0, u + shift, v, radius);
      q.addColorStop(0, `rgba(${rgb},1)`); q.addColorStop(0.3, `rgba(${rgb},1)`); q.addColorStop(1, `rgba(${rgb},0)`);
      x.fillStyle = q; x.fillRect(0, 0, w, h);
    }
  }
  const t = new THREE.CanvasTexture(c);
  t.mapping = THREE.EquirectangularReflectionMapping;
  t.colorSpace = THREE.SRGBColorSpace;
  const pmrem = new THREE.PMREMGenerator(renderer);
  const texture = pmrem.fromEquirectangular(t).texture;
  pmrem.dispose(); t.dispose();
  return texture;
}

/**
 * The satellite in a canvas (the window is 221×130 on the Mac). The state comes from outside:
 * satellite.set({ state: 'speaking', voice: 0.4, privacy: false, deepIdle: false }).
 * 30 fps while working, 15 waiting, 10 after a minute of waiting, nothing while hidden (as on the Mac).
 */
export function createSatellite(canvas) {
  THREE.ColorManagement.enabled = true;
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, premultipliedAlpha: true });
  renderer.setClearColor(0x000000, 0);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.NoToneMapping;
  renderer.useLegacyLights = true;
  const scene = new THREE.Scene();
  scene.environment = environment(renderer);
  // Mac: SceneKit camera, 30° across the width, at (0, 0.03, 2.45) looking at the centre.
  const camera = new THREE.PerspectiveCamera(17.9, 221 / 130, 0.02, 60);
  camera.position.set(0, 0.03, 2.45); camera.lookAt(0, 0, 0);
  scene.add(new THREE.AmbientLight(0xcfe0ff, 0.25));
  const key = new THREE.DirectionalLight(0xfff6ea, 1.8); key.position.set(3, 5, 6); scene.add(key);
  const rim = new THREE.DirectionalLight(0x6cc8ff, 1.4); rim.position.set(-4, 2, -5); scene.add(rim);

  const T = textures();
  const std = (o) => new THREE.MeshStandardMaterial({ envMapIntensity: ENV, ...o, roughness: o.metalness >= 0.9 ? Math.min(1, o.roughness * SHINE) : o.roughness });
  const BLU = std({ color: BLUE, emissive: BLUE, emissiveIntensity: 2.2, metalness: 0, roughness: 0.5 });
  const HULL = std({ map: T.hex, metalness: 0.95, roughness: 0.18 });
  const TI = std({ color: 0x9aa3af, metalness: 1, roughness: 0.16 });
  const TI2 = std({ color: 0xdde3ea, metalness: 1, roughness: 0.16 });
  const DK = std({ color: 0x151a22, metalness: 0.7, roughness: 0.42 });
  const FOIL = std({ map: T.foil, metalness: 1, roughness: 0.3, color: 0xffd88a });
  const CELL = std({ map: T.cell, metalness: 0.6, roughness: 0.15, side: THREE.DoubleSide });
  const GLS = std({ color: 0x04070b, metalness: 0.4, roughness: 0.03 });
  const LENS2 = std({ color: 0x1a2a44, metalness: 0.8, roughness: 0.05, transparent: true, opacity: 0.85 });
  const RED = new THREE.MeshBasicMaterial({ color: 0xff3b3b }), GRN = new THREE.MeshBasicMaterial({ color: 0x3bff7a });
  const WHL = new THREE.MeshBasicMaterial({ color: 0xffffff });
  const CAB = std({ color: 0x2a2f38, metalness: 0, roughness: 0.7 });
  const BACK = std({ color: 0xe8e8e2, metalness: 0, roughness: 0.6 });
  const DISH = std({ color: 0xeef1f4, metalness: 0.5, roughness: 0.3, side: THREE.DoubleSide });

  const B = (x, y, z) => new THREE.BoxGeometry(x, y, z);
  const Cy = (a, b, h, n = 8) => new THREE.CylinderGeometry(a, b, h, n);
  const Sp = (r, a = 16, b = 12) => new THREE.SphereGeometry(r, a, b);
  const To = (r, t, n = 48) => new THREE.TorusGeometry(r, t, 10, n);
  const Cap = (r, theta, w, h) => new THREE.SphereGeometry(r, w, h, 0, Math.PI * 2, 0, theta);
  function mk(geo, mat, p, pos, sc, rot) {
    const m = new THREE.Mesh(geo, mat);
    m.rotation.order = 'ZYX';
    if (pos) m.position.set(...pos); if (sc) m.scale.set(...sc); if (rot) m.rotation.set(...rot);
    p.add(m); return m;
  }
  function grp(p, pos, rot) {
    const g = new THREE.Group();
    g.rotation.order = 'ZYX';
    if (pos) g.position.set(...pos); if (rot) g.rotation.set(...rot);
    p.add(g); return g;
  }
  function glow(p, s, pos, col = 0x7fd6ff) {
    const m = new THREE.Sprite(new THREE.SpriteMaterial({ map: T.glow, color: col, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true }));
    m.scale.setScalar(s); m.position.set(...pos); m.renderOrder = 20; p.add(m); return m;
  }
  const Y = new THREE.Vector3(0, 1, 0);
  /** A thin cylinder from a to b (Mac: strut). */
  function strut(a, b, r, mat, p) {
    const va = new THREE.Vector3(...a), vb = new THREE.Vector3(...b);
    const n = mk(Cy(r, r, va.distanceTo(vb), 6), mat, p);
    n.position.copy(va).add(vb).multiplyScalar(0.5);
    n.quaternion.setFromUnitVectors(Y, vb.clone().sub(va).normalize());
    return n;
  }

  const PI = Math.PI, half = PI / 2;
  const random = new SatelliteRandom(5);
  const root = grp(scene), sat = grp(root);
  const RX = [half, 0, PI / 8];

  // Hull, gold foil, bands, front collar and rear taper.
  mk(Cy(0.1, 0.1, 0.2), HULL, sat, null, null, RX);
  mk(Cy(0.103, 0.103, 0.07), FOIL, sat, [0, 0, -0.04], null, RX);
  for (const z of [-0.075, -0.005, 0.075]) mk(Cy(0.104, 0.104, 0.006), TI2, sat, [0, 0, z], null, RX);
  mk(Cy(0.085, 0.1, 0.04), TI2, sat, [0, 0, 0.12], null, RX);
  mk(Cy(0.1, 0.07, 0.05), TI, sat, [0, 0, -0.125], null, RX);
  mk(Cy(0.07, 0.05, 0.03), DK, sat, [0, 0, -0.16], null, RX);
  for (let i = 0; i < 8; i++) {
    const a = i / 8 * PI * 2 + PI / 8, ca = Math.cos(a), sa = Math.sin(a);
    mk(B(0.003, 0.003, 0.16), BLU, sat, [ca * 0.101, sa * 0.101, 0]);
    if (i % 2) mk(B(0.03, 0.02, 0.012), TI, sat, [ca * 0.104, sa * 0.104, 0.04], null, [0, 0, a]);
    else {
      mk(B(0.026, 0.014, 0.03), DK, sat, [ca * 0.104, sa * 0.104, -0.03], null, [0, 0, a]);
      for (let k = 0; k < 3; k++) mk(B(0.022, 0.002, 0.004), TI2, sat, [ca * 0.112, sa * 0.112, -0.042 + k * 0.012], null, [0, 0, a]);
    }
  }
  for (let i = 0; i < 4; i++) {
    const a = i / 4 * PI * 2 + 0.4;
    const c = grp(sat, [Math.cos(a) * 0.1, Math.sin(a) * 0.1, -0.06], [0, 0, a]);
    mk(B(0.01, 0.006, 0.11), CAB, c, [0.006, 0, 0.02]);
  }

  // Eye.
  const eye = grp(sat, [0, 0, 0.145]);
  mk(Cy(0.066, 0.074, 0.035, 48), DK, eye, null, null, [half, 0, 0]);
  mk(To(0.064, 0.007, 64), TI2, eye, [0, 0, 0.018]);
  mk(To(0.054, 0.003, 64), TI, eye, [0, 0, 0.02]);
  for (let i = 0; i < 24; i++) { const a = i / 24 * PI * 2; mk(B(0.004, 0.008, 0.006), TI, eye, [Math.cos(a) * 0.07, Math.sin(a) * 0.07, 0.008], null, [0, 0, a]); }
  mk(Cy(0.05, 0.05, 0.01, 48), GLS, eye, [0, 0, 0.018], null, [half, 0, 0]);
  const blades = [];
  for (let i = 0; i < 8; i++) blades.push(mk(B(0.03, 0.012, 0.002), DK, eye, [0, 0, 0.022]));
  const iris = mk(Cy(0.022, 0.022, 0.004, 40), BLU, eye, [0, 0, 0.023], null, [half, 0, 0]);
  const iris2 = mk(To(0.014, 0.0015, 40), new THREE.MeshBasicMaterial({ color: 0xcff2ff }), eye, [0, 0, 0.026]);
  mk(Cap(0.05, PI / 5, 40, 16), LENS2, eye, [0, 0, -0.02], null, [half, 0, 0]);
  const irisG = glow(eye, 0.16, [0, 0, 0.045]);
  mk(Sp(0.005), WHL, eye, [-0.016, 0.016, 0.03]);
  mk(Sp(0.0025), WHL, eye, [0.012, -0.01, 0.03]);
  const brows = [-1, 1].map(d => mk(B(0.05, 0.006, 0.008), TI2, sat, [d * 0.035, 0.078, 0.135], null, [0, 0, d * -0.15]));
  const mouth = [];
  for (let i = 0; i < 9; i++) mouth.push(mk(B(0.006, 0.01, 0.004), BLU, sat, [-0.032 + i * 0.008, -0.087, 0.128]));
  mk(B(0.08, 0.018, 0.004), DK, sat, [0, -0.087, 0.126]);

  // Gyroscopic rings (moved forward on 11 November so they do not cross the wing supports).
  const ring = grp(sat, [0, 0, 0.08]);
  mk(To(0.21, 0.009, 128), TI2, ring);
  mk(To(0.198, 0.003, 128), DK, ring);
  const segs = [];
  for (let i = 0; i < 16; i++) { const a = i / 16 * PI * 2; segs.push(mk(B(0.026, 0.005, 0.005), BLU, ring, [Math.cos(a) * 0.21, Math.sin(a) * 0.21, 0.01], null, [0, 0, a + half])); }
  for (let i = 0; i < 4; i++) {
    const a = i / 4 * PI * 2 + PI / 4;
    mk(B(0.005, 0.09, 0.005), TI, ring, [Math.cos(a) * 0.155, Math.sin(a) * 0.155, 0], null, [0, 0, a + half]);
    mk(B(0.016, 0.016, 0.016), TI, ring, [Math.cos(a) * 0.21, Math.sin(a) * 0.21, 0], null, [0, 0, a]);
  }
  const ring2 = grp(sat, [0, 0, 0.09]);
  mk(To(0.235, 0.004, 128), TI, ring2);
  for (let i = 0; i < 6; i++) { const a = i / 6 * PI * 2; mk(Sp(0.006), BLU, ring2, [Math.cos(a) * 0.235, Math.sin(a) * 0.235, 0]); }

  // Solar wings.
  const wings = [-1, 1].map(d => {
    const arm = grp(sat, [d * 0.105, 0, -0.02]);
    mk(Cy(0.009, 0.009, 0.13, 12), TI, arm, [d * 0.065, 0, 0], null, [0, 0, half]);
    mk(Cy(0.014, 0.014, 0.025, 16), TI2, arm, [d * 0.02, 0, 0], null, [0, 0, half]);
    mk(Sp(0.018), TI2, arm, [d * 0.13, 0, 0]);
    mk(B(0.05, 0.004, 0.004), CAB, arm, [d * 0.07, 0.012, 0]);
    const p1 = grp(arm, [d * 0.135, 0, 0]);
    const panels = [];
    let par = p1;
    for (let k = 0; k < 3; k++) {
      const pg = grp(par, k ? [d * 0.122, 0, 0] : [0, 0, 0]);
      const pl = grp(pg, [d * 0.061, 0, 0]);
      mk(B(0.118, 0.15, 0.004), TI, pl);
      mk(B(0.11, 0.142, 0.005), CELL, pl);
      mk(B(0.118, 0.004, 0.008), TI2, pl, [0, 0.075, 0]);
      mk(B(0.118, 0.004, 0.008), TI2, pl, [0, -0.075, 0]);
      for (const yy of [-0.05, 0.05]) mk(Cy(0.003, 0.003, 0.008, 8), TI2, pl, [d * -0.059, yy, 0], null, [0, 0, half]);
      mk(B(0.112, 0.144, 0.002), BACK, pl, [0, 0, -0.004]);
      panels.push(pg);
      par = pg;
    }
    mk(Sp(0.006), d > 0 ? GRN : RED, par, [d * 0.125, 0.08, 0]);
    const tg = glow(par, 0, [d * 0.125, 0.08, 0], d > 0 ? 0x3bff7a : 0xff3b3b);
    return { arm, p1, panels, d, tg };
  });

  // Dish.
  const dish = grp(sat, [0, 0.105, -0.04]);
  mk(Cy(0.012, 0.016, 0.012, 16), TI2, dish);
  mk(Cy(0.006, 0.006, 0.06, 10), TI, dish, [0, 0.035, 0]);
  const dsh = grp(dish, [0, 0.07, 0]);
  mk(Cap(0.058, PI / 3.2, 40, 10), DISH, dsh, null, [1, 0.5, 1], [PI, 0, 0]);
  mk(To(0.05, 0.003, 64), TI, dsh, [0, -0.02, 0], null, [half, 0, 0]);
  for (let i = 0; i < 3; i++) {
    const a = i / 3 * PI * 2, from = [Math.cos(a) * 0.022, -0.03, Math.sin(a) * 0.022];
    const s = mk(Cy(0.0015, 0.0015, 0.05, 6), TI, dsh, from);
    s.quaternion.setFromUnitVectors(Y, new THREE.Vector3(0, -0.05, 0).sub(new THREE.Vector3(...from)).normalize());
  }
  mk(Cy(0.008, 0.004, 0.012, 12), DK, dsh, [0, -0.052, 0]);
  const ftip = mk(Sp(0.005), BLU, dsh, [0, -0.06, 0]);

  // Star trackers, boom with red LED, whip antenna.
  const trackers = [-1, 1].map(d => {
    const g = grp(sat, [d * 0.06, 0.085, -0.08], [-0.5, d * 0.4, 0]);
    mk(Cy(0.012, 0.012, 0.03, 16), DK, g, [0, 0.015, 0]);
    mk(Cy(0.016, 0.012, 0.01, 16), TI2, g, [0, 0.034, 0]);
    mk(Cy(0.009, 0.009, 0.002, 16), GLS, g, [0, 0.04, 0]);
    return g;
  });
  const boom = grp(sat, [0.045, -0.1, -0.06]);
  mk(Cy(0.003, 0.003, 0.14, 6), TI, boom, [0, -0.07, 0]);
  for (let i = 0; i < 4; i++) mk(B(0.012, 0.002, 0.002), TI, boom, [0, -0.03 - i * 0.03, 0]);
  mk(Sp(0.006), RED, boom, [0, -0.145, 0]);
  const ledG = glow(boom, 0, [0, -0.145, 0], 0xff4040);
  const whip = grp(sat, [-0.05, -0.095, -0.09], [0.3, 0, 0.3]);
  mk(Cy(0.0015, 0.0015, 0.18, 6), TI2, whip, [0, -0.09, 0]);

  // RCS thruster clusters.
  const thrusters = [];
  for (const [x, y] of [[0.074, 0.074], [-0.074, 0.074], [0.074, -0.074], [-0.074, -0.074]]) {
    const n = grp(sat, [x, y, -0.1]);
    mk(B(0.03, 0.03, 0.02), TI, n);
    for (const [dx, dy, rx, ry] of [[0.018, 0, 0, half], [0, 0.018, half, 0], [0, 0, 0, 0]]) {
      const side = dx !== 0 || dy !== 0;
      mk(Cy(0.004, 0.007, 0.012, 10), DK, n, [dx * (x > 0 ? 1 : -1), dy * (y > 0 ? 1 : -1), side ? 0 : -0.014], null,
         [rx || (side ? 0 : half), 0, ry ? (x > 0 ? -1 : 1) * half : 0]);
    }
    thrusters.push(glow(n, 0, [0, 0, -0.03], 0xbfe8ff));
  }

  // Engine and strobe.
  const eng = grp(sat, [0, 0, -0.18]);
  mk(Cy(0.03, 0.045, 0.04, 24), TI2, eng, null, null, [half, 0, 0]);
  mk(Cy(0.026, 0.026, 0.004, 24), BLU, eng, [0, 0, -0.02], null, [half, 0, 0]);
  const engG = glow(eng, 0.1, [0, 0, -0.03]);
  mk(Sp(0.005), WHL, sat, [0, 0.09, 0.05]);
  const strobeG = glow(sat, 0, [0, 0.09, 0.05], 0xffffff);

  // Particles (thinking) and signal waves.
  const particleGroup = grp(sat);
  const dotGeo = Sp(0.0035, 6, 4);
  const particles = [];
  for (let i = 0; i < 60; i++) {
    const node = new THREE.Mesh(dotGeo, BLU); particleGroup.add(node);
    particles.push({ node, a: random.next() * 6.28, r: 0.27 + random.next() * 0.1, y: (random.next() - 0.5) * 0.12, s: 0.5 + random.next() });
  }
  particleGroup.visible = false;
  const waves = [];
  for (let i = 0; i < 3; i++)
    waves.push(mk(To(0.1, 0.003, 64), new THREE.MeshBasicMaterial({ color: 0x7fd6ff, transparent: true, opacity: 0, blending: THREE.AdditiveBlending, depthWrite: false }), sat, [0, 0, 0.17]));

  // Extra detail (Mac, 11 November, "more beautiful and detailed", same style).
  for (let i = 0; i < 4; i++) {
    const a = i / 4 * PI * 2, x = Math.cos(a) * 0.083, y = Math.sin(a) * 0.083;
    mk(Sp(0.023), FOIL, sat, [x, y, -0.137]);
    mk(To(0.0236, 0.0014, 24), TI2, sat, [x, y, -0.137]);
    mk(To(0.0236, 0.0014, 24), TI2, sat, [x, y, -0.137], null, [half, 0, a]);
    strut([x, y, -0.137], [Math.cos(a) * 0.04, Math.sin(a) * 0.04, -0.172], 0.0018, CAB, sat);
  }
  mk(To(0.0465, 0.0022, 40), TI, sat, [0, 0, -0.2]);
  mk(To(0.039, 0.0016, 40), DK, sat, [0, 0, -0.19]);
  mk(To(0.033, 0.0016, 40), TI2, sat, [0, 0, -0.178]);
  for (let i = 0; i < 4; i++) {
    const a = i / 4 * PI * 2 + PI / 4, ca = Math.cos(a), sa = Math.sin(a);
    strut([ca * 0.052, sa * 0.052, -0.15], [ca * 0.036, sa * 0.036, -0.186], 0.002, TI2, sat);
    mk(Cy(0.0035, 0.0075, 0.014, 10), DK, sat, [ca * 0.06, sa * 0.06, -0.168], null, [half, 0, 0]);
    mk(Cy(0.0028, 0.0028, 0.002, 10), BLU, sat, [ca * 0.06, sa * 0.06, -0.1745], null, [half, 0, 0]);
  }
  for (const w of wings) {
    const d = w.d;
    for (const s of [-1, 1]) {
      strut([d * 0.03, 0, 0], [d * 0.132, s * 0.062, 0], 0.0022, TI, w.arm);
      strut([d * 0.075, s * 0.027, 0], [d * 0.105, 0, 0], 0.0015, TI2, w.arm);
    }
    mk(B(0.004, 0.13, 0.004), TI2, w.arm, [d * 0.132, 0, 0]);
    for (let k = 0; k < 5; k++) mk(B(0.0015, 0.034, 0.03), DK, w.arm, [d * (0.036 + k * 0.005), 0, 0]);
    for (const pg of w.panels) {
      mk(B(0.112, 0.0016, 0.0016), BLU, pg, [d * 0.061, 0.0712, 0.0045]);
      mk(B(0.112, 0.0016, 0.0016), BLU, pg, [d * 0.061, -0.0712, 0.0045]);
      for (const off of [-0.03, 0.03]) mk(B(0.003, 0.14, 0.004), TI, pg, [d * (0.061 + off), 0, -0.0065]);
      mk(B(0.11, 0.003, 0.004), TI, pg, [d * 0.061, 0, -0.0065]);
      mk(Cy(0.0045, 0.0045, 0.03, 10), TI2, pg, [0, 0, 0]);
    }
  }
  const dish2 = grp(sat, [0, -0.098, -0.005], [PI - 0.75, 0, 0]);
  mk(Cy(0.008, 0.011, 0.008, 12), TI2, dish2);
  mk(Cy(0.004, 0.004, 0.034, 8), TI, dish2, [0, 0.02, 0]);
  const bowl = grp(dish2, [0, 0.046, 0]);
  mk(Cap(0.036, PI / 3.2, 28, 8), DISH, bowl, null, [1, 0.5, 1], [PI, 0, 0]);
  mk(To(0.031, 0.002, 40), TI, bowl, [0, -0.0125, 0], null, [half, 0, 0]);
  mk(Cy(0.001, 0.001, 0.03, 6), TI, bowl, [0, -0.02, 0]);
  mk(Sp(0.0035), BLU, bowl, [0, -0.036, 0]);
  const helix = grp(sat, [-0.047, 0.088, 0.02], [0, 0, 0.25]);
  mk(Cy(0.007, 0.009, 0.006, 12), TI2, helix);
  mk(Cy(0.0012, 0.0012, 0.062, 6), TI, helix, [0, 0.031, 0]);
  for (let k = 0; k < 7; k++) mk(To(0.0075 - k * 0.0005, 0.0009, 20), TI2, helix, [0, 0.012 + k * 0.0075, 0], null, [half, 0, 0]);
  mk(Sp(0.0028), BLU, helix, [0, 0.064, 0]);
  for (let i = 0; i < 16; i++) { const a = i / 16 * PI * 2 + PI / 16; mk(Sp(0.0022), BLU, eye, [Math.cos(a) * 0.0745, Math.sin(a) * 0.0745, 0.019]); }
  for (let i = 0; i < 48; i += 2) { const a = i / 48 * PI * 2; mk(B(0.0012, 0.005, 0.002), TI2, eye, [Math.cos(a) * 0.0585, Math.sin(a) * 0.0585, 0.0235], null, [0, 0, a + half]); }
  mk(To(0.21, 0.0016, 128), BLU, ring, [0, 0, -0.011]);
  for (let i = 0; i < 32; i++) { const a = i / 32 * PI * 2; mk(B(0.002, 0.007, 0.011), i % 4 === 0 ? TI2 : DK, ring, [Math.cos(a) * 0.221, Math.sin(a) * 0.221, 0], null, [0, 0, a + half]); }
  for (let i = 0; i < 6; i++) { const a = i / 6 * PI * 2 + PI / 6; mk(B(0.012, 0.007, 0.007), TI2, ring2, [Math.cos(a) * 0.235, Math.sin(a) * 0.235, 0], null, [0, 0, a + half]); }

  // Fewer draw calls (Mac: mergeStaticParts): parts that never move on their own become one mesh per material.
  const moving = new Set([root, sat, ring, ring2, iris, iris2, irisG, dish, dsh, ftip, ledG, strobeG, engG, particleGroup,
    ...segs, ...blades, ...brows, ...mouth, ...trackers, ...thrusters, ...waves, ...wings.flatMap(w => [w.arm, w.p1, w.tg, ...w.panels])]);
  mergeStaticParts(root, moving);

  // ── Motion (Mac: SatelliteRig.renderer(_:updateAtTime:)) ──────────────────────────────────────────
  const input = { state: 'idle', voice: 0, privacy: false, deepIdle: false };
  const cur = {};
  let last = null, voice = 0, accent = -1, lastFrame = 0, frames = 0, fpsStart = performance.now(), fps = 0;

  function mode() {
    switch (input.state) {
      case 'listening': return 'listen';
      case 'transcribing': case 'thinking': case 'acting': case 'booting': return 'think';
      case 'speaking': return 'speak';
      default: return 'idle';
    }
  }

  function step(time) {
    const t = time;
    const dt = Math.min(0.2, Math.max(0, t - (last ?? t)));
    last = t;
    const S = mode();
    const color = input.privacy ? PRIVACY : input.state === 'error' ? AMBER : BLUE;
    if (color !== accent) { accent = color; BLU.color.setHex(color); BLU.emissive.setHex(color); }
    // The preview eases by a fixed fraction per 60 fps frame: the same speed at any frame rate.
    const L = (k, v, a = 0.1) => { if (cur[k] === undefined) return (cur[k] = v); return (cur[k] += (v - cur[k]) * (1 - Math.pow(1 - a, dt * 60))); };
    const raw = Math.min(1, Math.max(0, input.voice * 1.8));
    voice += (raw - voice) * (1 - Math.exp(-dt * 18));
    const lv = S === 'speak' ? voice : S === 'listen' ? 0.5 : S === 'think' ? 0.3 : 0.1;
    const think = S === 'think', listen = S === 'listen', speak = S === 'speak', facing = listen || speak;

    // While waiting everything moves at about half the preview speed: calmer and smooth at 15 fps.
    root.position.set(0, Math.sin(t * (facing ? 1.1 : 0.5)) * 0.03, 0);
    sat.rotation.set(L('rx', facing ? 0.1 : Math.sin(t * 0.22) * 0.18, 0.05), L('ry', facing ? 0 : Math.sin(t * 0.15) * 1.3, 0.05),
                     L('rz', facing ? 0 : Math.sin(t * 0.13) * 0.12, 0.05));
    const open = listen ? 1 : think ? 0.8 : speak ? 0.92 : 0.6;
    wings.forEach((w, i) => {
      const o = L('o' + i, open, 0.06);
      w.panels.forEach((pg, k) => { if (k > 0) pg.rotation.y = w.d * (1 - o) * 2.6 * (k % 2 === 1 ? 1 : -1); });
      w.arm.rotation.x = L('ax' + i, think ? Math.sin(t * 1.5) * 0.35 : listen ? -0.25 : 0, 0.05);
      w.p1.rotation.y = w.d * (1 - o) * 0.4;
      const phase = t * 1.3 + i * 0.5;
      w.tg.scale.setScalar(Math.floor(phase) % 2 === 1 && phase % 1 < 0.3 ? 0.05 : 0);
    });
    ring.rotation.z += dt * (think ? 3 : speak ? 1 + lv * 2 : listen ? 0.35 : 0.15);
    ring.rotation.x = L('rgx', think ? Math.sin(t * 1.3) * 0.3 : facing ? 0 : 0.3, 0.05);
    ring.rotation.y = L('rgy', think ? Math.cos(t * 1.1) * 0.2 : 0, 0.05);
    ring2.rotation.z -= dt * (think ? 2 : listen || speak ? 0.2 : 0.09);
    ring2.rotation.x = L('r2x', think ? Math.cos(t * 0.9) * 0.3 : facing ? 0 : -0.28, 0.05);
    ring2.rotation.y = L('r2y', think ? Math.sin(t * 1.4) * 0.2 : 0.15, 0.05);
    const step12 = Math.floor(t * 12);
    segs.forEach((s, i) => { s.visible = think ? (i + step12) % 4 === 0 : speak ? i / 16 < lv + 0.2 : true; });
    const ap = listen ? 1.35 : speak ? 0.9 + lv * 0.6 : think ? 0.7 : 1;
    const blink = t % 4.6 < 0.13;
    iris.scale.set(ap, 1, blink ? 0.1 : ap);
    iris2.scale.setScalar(blink ? 0.1 : ap);
    blades.forEach((b, i) => {
      const a = i / 8 * PI * 2 + t * (think ? 2 : 0.1), r = 0.034 + (1 - ap) * 0.012;
      b.position.set(Math.cos(a) * r, Math.sin(a) * r, 0.022);
      b.rotation.z = a + half + 0.4;
    });
    const ix = L('ix', think ? Math.sin(t * 2) * 0.012 : 0), iy = L('iy', think ? 0.006 : 0);
    iris.position.x = ix; iris.position.y = iy; iris2.position.x = ix; iris2.position.y = iy;
    irisG.scale.setScalar(0.12 + lv * 0.12 + (listen ? 0.06 : 0));
    brows.forEach((b, i) => {
      b.rotation.z = L('bw' + i, (i === 1 ? 1 : -1) * (listen ? -0.05 : think ? 0.3 : 0.15), 0.1);
      b.position.y = L('by' + i, listen ? 0.085 : 0.078, 0.1);
    });
    const step8 = Math.floor(t * 8);
    mouth.forEach((m, i) => {
      m.scale.y = speak ? 0.5 + lv * 3.5 * Math.abs(Math.sin(t * 11 + i * 0.9)) : 1;
      m.visible = !(think && (i + step8) % 9 >= 3);
    });
    dsh.rotation.x = L('dx', listen ? -0.7 : think ? Math.sin(t * 1.5) * 0.5 : -0.2, 0.05);
    dish.rotation.y += dt * (think ? 2.5 : listen || speak ? 0.4 : 0.18);
    ftip.scale.setScalar(think ? 1 + 0.6 * Math.abs(Math.sin(t * 8)) : 1);
    trackers.forEach((g, i) => { g.rotation.y = L('tk' + i, (i === 1 ? 1 : -1) * 0.4 + (think ? Math.sin(t * 2 + i) * 0.4 : 0), 0.05); });
    ledG.scale.setScalar(Math.floor(t * 1.5) % 2 === 1 ? 0.05 : 0);
    strobeG.scale.setScalar(t % 2.2 < 0.08 ? 0.12 : 0);
    for (const g of thrusters) {
      if (random.next() < 0.008 * dt * 60) g.scale.setScalar(0.09);
      else g.scale.setScalar(g.scale.x * Math.pow(0.88, dt * 60));
    }
    engG.scale.setScalar(0.08 + lv * 0.08 + (think ? 0.04 * Math.sin(t * 6) : 0));
    BLU.emissiveIntensity = L('ei', listen ? 3 : speak ? 1.6 + lv * 2 : 2.2, 0.15);
    particleGroup.visible = think;
    if (think) for (const p of particles) {
      p.a += dt * p.s * 2;
      p.node.position.set(Math.cos(p.a) * p.r, p.y + Math.sin(p.a * 2) * 0.02, Math.sin(p.a) * p.r);
    }
    waves.forEach((w, i) => {
      const kk = (t * 0.9 + i / 3) % 1;
      w.scale.setScalar(0.6 + kk * (listen ? 1.4 : 2.2));
      w.position.z = 0.17 + (listen ? 0.3 - kk * 0.3 : kk * 0.25);
      w.material.opacity = facing ? (1 - kk) * 0.55 : 0;
    });
  }

  function resize() {
    const scale = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(canvas.clientWidth * scale)), h = Math.max(1, Math.round(canvas.clientHeight * scale));
    if (canvas.width !== w || canvas.height !== h) {
      renderer.setSize(w, h, false);
      // The angle is measured across the width (Mac): a lower window only crops above and below.
      const aspect = w / h;
      camera.aspect = aspect;
      camera.fov = 2 * Math.atan(Math.tan(15 * PI / 180) / aspect) * 180 / PI;
      camera.updateProjectionMatrix();
    }
  }
  function interval() {
    if (input.state !== 'idle') return 1000 / 30;
    return input.deepIdle ? 1000 / 10 : 1000 / 15;
  }
  function draw(now) {
    resize();
    step(now / 1000);
    renderer.render(scene, camera);
  }
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
    /** One frame now, even while hidden (tests and snapshots). */
    draw(now = performance.now()) { draw(now); },
    get fps() { return fps; },
    get drawCalls() { return renderer.info.render.calls; },
    renderer, scene,
  };
}

/** Every group of children that never moves on its own becomes one mesh per material (same picture, fewer draws). */
function mergeStaticParts(root, moving) {
  root.updateMatrixWorld(true);
  const hasMoving = (n) => moving.has(n) || n.children.some(hasMoving);
  function merge(n) {
    const still = n.children.filter(c => !hasMoving(c));
    if (still.length > 1) {
      const inverse = n.matrixWorld.clone().invert();
      const byMaterial = new Map();
      for (const c of still) c.traverse(m => {
        if (!m.isMesh) return;
        const g = m.geometry.index ? m.geometry.toNonIndexed() : m.geometry.clone();
        g.applyMatrix4(new THREE.Matrix4().multiplyMatrices(inverse, m.matrixWorld));
        if (!byMaterial.has(m.material)) byMaterial.set(m.material, []);
        byMaterial.get(m.material).push(g);
      });
      for (const c of still) n.remove(c);
      for (const [material, list] of byMaterial) n.add(new THREE.Mesh(mergeGeometries(list), material));
    }
    n.children.filter(hasMoving).forEach(merge);
  }
  merge(root);
}

/** Joins non-indexed geometries with position, normal and uv into one. */
function mergeGeometries(list) {
  const out = new THREE.BufferGeometry();
  for (const name of ['position', 'normal', 'uv']) {
    const size = list[0].attributes[name].itemSize;
    const total = list.reduce((s, g) => s + g.attributes[name].count * size, 0);
    const data = new Float32Array(total);
    let offset = 0;
    for (const g of list) { data.set(g.attributes[name].array, offset); offset += g.attributes[name].count * size; }
    out.setAttribute(name, new THREE.BufferAttribute(data, size));
  }
  return out;
}
