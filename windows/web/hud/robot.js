// The ROBOTTINO style of the nucleus, as on the Mac (RobotKit.swift, RobotGLTF.swift, RobotNucleus.swift): a small
// 3D robot that floats on jet flames, walks on the bottom edge of the display, draws its face on its screen and
// acts JARVIS's states. The model is NOT part of AL-76: it is a glTF file the user puts in the data folder
// (on the Mac the approved one is "Mini Robot" by Onur on Sketchfab, CC-BY 4.0). The rig uses that model's part
// names; another model is refused and the app shows the satellite instead.
import * as THREE from './vendor/three.module.min.js';

const { PI, sin, cos, abs, max, min, exp, random } = Math;
const lerp = (a, b, k) => a + (b - a) * k;
const srgb = (r, g, b) => new THREE.Color().setRGB(r, g, b, THREE.SRGBColorSpace);
const CYAN = [0.35, 0.88, 1.0];
/** SceneKit's lighting environment intensity, as it is (measured: body colour within 1% of the Mac's). */
const ENV = 1.3;
/** SceneKit's euler angles are applied as three's 'ZYX' (checked on the satellite). */
const ORDER = 'ZYX';
/** SceneKit draws a particle about twice as wide as its particleSize (measured on the Mac's flames). */
const PARTICLE_SCALE = 2;

// ── Small images (RobotKit: softDot, star, studio) ───────────────────────────────────────────────────────

function canvas2d(w, h = w) {
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  return [c, c.getContext('2d')];
}

function softDot() {
  const [c, x] = canvas2d(64);
  const g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
  g.addColorStop(0, 'rgba(255,255,255,1)'); g.addColorStop(0.35, 'rgba(255,255,255,0.5)'); g.addColorStop(1, 'rgba(255,255,255,0)');
  x.fillStyle = g; x.fillRect(0, 0, 64, 64);
  return texture(c);
}

function star() {
  const [c, x] = canvas2d(64);
  x.fillStyle = '#fff';
  x.beginPath(); x.moveTo(32, 2);
  for (let i = 1; i < 8; i++) {
    const a = i * PI / 4 + PI / 2, r = i % 2 === 0 ? 30 : 7;
    x.lineTo(32 + cos(a) * r, 32 + sin(a) * r);
  }
  x.closePath(); x.fill();
  return texture(c);
}

function texture(c, colorSpace = THREE.NoColorSpace) {
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = colorSpace;
  return t;
}

/** The Mac's studio: white above, blue-grey at the horizon, dark below, and one bright window. SceneKit reads an
 * equirectangular image a quarter turn away from three.js (u = atan2(x, -z) there, atan2(z, x) here). */
function studio(renderer) {
  const w = 512, h = 256;
  const [src, s] = canvas2d(w, h);
  const g = s.createLinearGradient(0, 0, 0, h);
  g.addColorStop(0, 'rgb(247,247,247)'); g.addColorStop(0.5, 'rgb(128,143,168)'); g.addColorStop(1, 'rgb(20,23,33)');
  s.fillStyle = g; s.fillRect(0, 0, w, h);
  s.fillStyle = 'rgba(255,255,255,0.95)'; s.fillRect(150, h - 175 - 40, 80, 40);
  const [c, x] = canvas2d(w, h);
  x.drawImage(src, -w / 4, 0); x.drawImage(src, w * 3 / 4, 0);
  const t = texture(c, THREE.SRGBColorSpace);
  t.mapping = THREE.EquirectangularReflectionMapping;
  const pmrem = new THREE.PMREMGenerator(renderer);
  const env = pmrem.fromEquirectangular(t).texture;
  pmrem.dispose(); t.dispose();
  return env;
}

// ── The model (RobotGLTF.swift): a separate .gltf and .bin, triangle meshes, named nodes, PBR textures ─────

/** Loads the user's model from `base` (a folder URL ending in '/'). Returns the root and the nodes by name. */
export async function loadRobotModel(base) {
  const get = async (uri) => {
    const r = await fetch(new URL(uri, base));
    if (!r.ok) throw new Error(`${uri}: ${r.status}`);
    return r;
  };
  const json = await (await get('scene.gltf')).json();
  const buffers = await Promise.all((json.buffers ?? []).map(async b => (await get(b.uri)).arrayBuffer()));
  const images = new Map();
  async function image(index) {
    const source = json.textures[index].source;
    if (!images.has(source)) images.set(source, (async () => {
      const blob = await (await get(json.images[source].uri)).blob();
      let bitmap = await createImageBitmap(blob, { imageOrientation: 'none', premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
      // Textures downscaled to 1024 at most, as on the Mac.
      if (max(bitmap.width, bitmap.height) > 1024) {
        const k = 1024 / max(bitmap.width, bitmap.height);
        bitmap = await createImageBitmap(bitmap, { resizeWidth: Math.round(bitmap.width * k), resizeHeight: Math.round(bitmap.height * k), resizeQuality: 'high' });
      }
      return bitmap;
    })());
    return images.get(source);
  }
  async function map(index, colorSpace) {
    const t = new THREE.Texture(await image(index));
    t.flipY = false;
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.colorSpace = colorSpace;
    t.needsUpdate = true;
    return t;
  }
  const materials = await Promise.all((json.materials ?? []).map(async m => {
    const pbr = m.pbrMetallicRoughness ?? {};
    const mat = new THREE.MeshStandardMaterial({ name: m.name, envMapIntensity: ENV, side: m.doubleSided ? THREE.DoubleSide : THREE.FrontSide });
    if (pbr.baseColorTexture) mat.map = await map(pbr.baseColorTexture.index, THREE.SRGBColorSpace);
    else if (pbr.baseColorFactor) mat.color = srgb(...pbr.baseColorFactor);
    if (pbr.metallicRoughnessTexture) {
      const t = await map(pbr.metallicRoughnessTexture.index, THREE.NoColorSpace);
      mat.roughnessMap = t; mat.metalnessMap = t; mat.roughness = 1; mat.metalness = 1;
    } else {
      mat.roughness = pbr.roughnessFactor ?? 1; mat.metalness = pbr.metallicFactor ?? 1;
    }
    if (m.normalTexture) mat.normalMap = await map(m.normalTexture.index, THREE.NoColorSpace);
    // The Mac reads the emission texture as it is (no emissive factor) and no occlusion.
    if (m.emissiveTexture) { mat.emissiveMap = await map(m.emissiveTexture.index, THREE.SRGBColorSpace); mat.emissive = new THREE.Color(1, 1, 1); }
    return mat;
  }));
  function attribute(index, normalize) {
    const a = json.accessors[index];
    const size = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 }[a.type];
    const view = json.bufferViews[a.bufferView];
    const offset = (view.byteOffset ?? 0) + (a.byteOffset ?? 0);
    const stride = view.byteStride ?? 0;
    const buffer = buffers[view.buffer];
    if (a.componentType !== 5126) {
      const Type = { 5121: Uint8Array, 5123: Uint16Array, 5125: Uint32Array }[a.componentType];
      return new THREE.BufferAttribute(new Uint32Array(new Type(buffer, offset, a.count)), 1);
    }
    let data;
    if (!stride || stride === size * 4) data = new Float32Array(buffer.slice(offset, offset + a.count * size * 4));
    else {
      data = new Float32Array(a.count * size);
      const f = new DataView(buffer);
      for (let i = 0; i < a.count; i++) for (let k = 0; k < size; k++) data[i * size + k] = f.getFloat32(offset + i * stride + k * 4, true);
    }
    return new THREE.BufferAttribute(data, size, normalize);
  }
  const meshes = json.meshes.map(mesh => mesh.primitives.map(p => {
    // One primitive per mesh in this model.
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', attribute(p.attributes.POSITION));
    if (p.attributes.NORMAL !== undefined) g.setAttribute('normal', attribute(p.attributes.NORMAL));
    if (p.attributes.TEXCOORD_0 !== undefined) g.setAttribute('uv', attribute(p.attributes.TEXCOORD_0));
    if (p.attributes.TANGENT !== undefined) g.setAttribute('tangent', attribute(p.attributes.TANGENT));
    g.setIndex(attribute(p.indices));
    return { geometry: g, material: materials[p.material] };
  }));
  const named = {};
  function build(i) {
    const n = json.nodes[i];
    // A node with a mesh is the mesh itself (SceneKit: a node with geometry), so the rig finds it by name.
    const parts = n.mesh !== undefined ? meshes[n.mesh] : [];
    const node = parts.length === 1 ? new THREE.Mesh(parts[0].geometry, parts[0].material) : new THREE.Object3D();
    node.name = n.name ?? '';
    if (n.matrix) new THREE.Matrix4().fromArray(n.matrix).decompose(node.position, node.quaternion, node.scale);
    else {
      if (n.translation) node.position.fromArray(n.translation);
      if (n.rotation) node.quaternion.fromArray(n.rotation);
      if (n.scale) node.scale.fromArray(n.scale);
    }
    if (parts.length > 1) for (const p of parts) node.add(new THREE.Mesh(p.geometry, p.material));
    for (const c of n.children ?? []) node.add(build(c));
    if (node.name) named[node.name] = node;
    return node;
  }
  const root = new THREE.Object3D();
  for (const i of json.scenes[json.scene ?? 0].nodes) root.add(build(i));
  return { root, named };
}

/** Splits a mesh that holds both sides of the body (arms, legs, ears) into a −x half and a +x half, by the world x
 * of each triangle's centre, so each side moves on its own. */
function splitByX(mesh) {
  const g = mesh.geometry, p = g.attributes.position.array, idx = g.index.array, w = mesh.matrixWorld.elements;
  const neg = [], pos = [];
  for (let t = 0; t < idx.length; t += 3) {
    const a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
    const cx = (p[a] + p[b] + p[c]) / 3, cy = (p[a + 1] + p[b + 1] + p[c + 1]) / 3, cz = (p[a + 2] + p[b + 2] + p[c + 2]) / 3;
    const x = cx * w[0] + cy * w[4] + cz * w[8] + w[12];
    (x < 0 ? neg : pos).push(idx[t], idx[t + 1], idx[t + 2]);
  }
  const half = (list) => {
    const h = new THREE.BufferGeometry();
    for (const [name, a] of Object.entries(g.attributes)) h.setAttribute(name, a);
    h.setIndex(new THREE.BufferAttribute(new Uint32Array(list), 1));
    const m = new THREE.Mesh(h, mesh.material);
    m.name = mesh.name;
    return m;
  };
  return [half(neg), half(pos)];
}

/** The "Z" of sleep: the Z of the system font at 10 pt (strokes as thick as they look on the Mac), extruded 0.2 like SCNText. */
function zShape() {
  const s = new THREE.Shape();
  [[0.35, 7.1], [5.75, 7.1], [5.75, 6.0], [1.95, 1.15], [5.85, 1.15], [5.85, 0], [0.2, 0], [0.2, 1.05], [4.0, 5.95], [0.35, 5.95]]
    .forEach(([x, y], i) => i ? s.lineTo(x, y) : s.moveTo(x, y));
  s.closePath();
  const g = new THREE.ExtrudeGeometry(s, { depth: 0.2, bevelEnabled: false });
  g.computeBoundingBox();
  const b = g.boundingBox;
  g.translate(-(b.min.x + b.max.x) / 2, -(b.min.y + b.max.y) / 2, -0.1);
  return g;
}

// ── Particles (SCNParticleSystem, the parts the robot uses) ───────────────────────────────────────────────

const POINT_VERTEX = `
attribute float psize; attribute vec4 pcolor; attribute float pangle;
uniform float halfHeight;
varying vec4 vColor; varying float vAngle;
void main() {
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * mv;
  gl_PointSize = psize * projectionMatrix[1][1] * halfHeight / -mv.z;
  vColor = pcolor; vAngle = pangle;
}`;
const POINT_FRAGMENT = `
uniform sampler2D map;
varying vec4 vColor; varying float vAngle;
void main() {
  vec2 p = gl_PointCoord - 0.5;
  float c = cos(vAngle), s = sin(vAngle);
  vec4 t = texture2D(map, vec2(c * p.x - s * p.y, s * p.x + c * p.y) + 0.5);
  gl_FragColor = vec4(vColor.rgb * t.rgb, vColor.a * t.a);
  #include <colorspace_fragment>
}`;

/** A keyframe curve over a particle's life (CAKeyframeAnimation, linear). */
function curve(values, times) {
  times ??= values.map((_, i) => i / (values.length - 1));
  return (k) => {
    if (k <= times[0]) return values[0];
    for (let i = 1; i < times.length; i++) if (k <= times[i]) {
      const f = (k - times[i - 1]) / (times[i] - times[i - 1]), a = values[i - 1], b = values[i];
      return Array.isArray(a) ? a.map((v, j) => lerp(v, b[j], f)) : lerp(a, b, f);
    }
    return values[values.length - 1];
  };
}
const toLinear = (v) => v < 0.04045 ? v * 0.0773993808 : Math.pow(v * 0.9478672986 + 0.0521327014, 2.4);
function hsb([r, g, b]) {
  const mx = max(r, g, b), d = mx - min(r, g, b);
  const h = d === 0 ? 0 : mx === r ? ((g - b) / d + 6) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h / 6, mx === 0 ? 0 : d / mx, mx];
}
function rgb(h, s, v) {
  const f = (n) => { const k = (n + h * 6) % 6; return v - v * s * max(0, min(k, 4 - k, 1)); };
  return [f(5), f(3), f(1)];
}
const linear = (c) => [...c.slice(0, 3).map(toLinear), c[3]];

class Particles {
  /** o: map, additive, life, lifeVar, velocity, velocityVar, size, sizeVar, direction, spread (degrees), sphere
   * (emitter radius, born on its surface), color (sRGB rgba), colorVar (hsb), colors (curve of sRGB rgba),
   * sizes (curve of factors), opacity (curve), spin (degrees a second), gravity, max. */
  constructor(o, world) {
    this.o = o;
    this.node = new THREE.Object3D();
    this.birthRate = o.birthRate ?? 0;
    this.velocity = o.velocity ?? 0;
    this.list = [];
    this.carry = 0;
    const n = o.max ?? 256;
    this.geometry = new THREE.BufferGeometry();
    for (const [name, size] of [['position', 3], ['pcolor', 4], ['psize', 1], ['pangle', 1]])
      this.geometry.setAttribute(name, new THREE.BufferAttribute(new Float32Array(n * size), size).setUsage(THREE.DynamicDrawUsage));
    this.material = new THREE.ShaderMaterial({
      uniforms: { map: { value: o.map }, halfHeight: { value: 100 } },
      vertexShader: POINT_VERTEX, fragmentShader: POINT_FRAGMENT,
      transparent: true, depthWrite: false, blending: o.additive ? THREE.AdditiveBlending : THREE.NormalBlending,
    });
    this.points = new THREE.Points(this.geometry, this.material);
    this.points.frustumCulled = false;
    this.points.renderOrder = 2;
    this.colors = o.colors ? curve(o.colors.values.map(linear), o.colors.times) : null;
    this.sizes = o.sizes ? curve(o.sizes.values, o.sizes.times) : null;
    this.opacity = o.opacity ? curve(o.opacity.values, o.opacity.times) : null;
    world.add(this.points);
  }

  setColors(values, times) { this.colors = curve(values.map(linear), times); }

  spawn() {
    const o = this.o, m = this.node.matrixWorld;
    const at = new THREE.Vector3();
    if (o.sphere) {
      const u = random() * 2 - 1, a = random() * 2 * PI, r = Math.sqrt(1 - u * u);
      at.set(r * cos(a), u, r * sin(a)).multiplyScalar(o.sphere);
    }
    at.applyMatrix4(m);
    const d = new THREE.Vector3(...(o.direction ?? [0, 0, 1])).normalize();
    if (o.spread) {
      // A random direction inside the cone around d.
      const side = new THREE.Vector3(1, 0, 0);
      if (abs(d.x) > 0.9) side.set(0, 1, 0);
      const u = new THREE.Vector3().crossVectors(d, side).normalize(), v = new THREE.Vector3().crossVectors(d, u);
      const th = random() * o.spread * PI / 180, ph = random() * 2 * PI;
      d.multiplyScalar(cos(th)).addScaledVector(u, sin(th) * cos(ph)).addScaledVector(v, sin(th) * sin(ph));
    }
    d.transformDirection(m);
    const speed = this.velocity + (random() - 0.5) * (o.velocityVar ?? 0);
    let color = o.color;
    if (o.colorVar) {
      // SceneKit varies hue, saturation and brightness each by up to ± the given amount (Mac: gold, purple, red
      // and green sparkles from one gold).
      let [h, sat, b] = hsb(color);
      h = ((h + (random() * 2 - 1) * o.colorVar[0]) % 1 + 1) % 1;
      sat = min(1, max(0, sat + (random() * 2 - 1) * o.colorVar[1]));
      b = min(1, max(0, b + (random() * 2 - 1) * o.colorVar[2]));
      color = [...rgb(h, sat, b), color[3]];
    }
    this.list.push({
      p: at, v: d.multiplyScalar(speed), age: 0,
      life: max(0.01, o.life + (random() - 0.5) * (o.lifeVar ?? 0)),
      size: max(0, o.size + (random() - 0.5) * (o.sizeVar ?? 0)),
      color: color ? linear(color) : null, angle: 0,
    });
  }

  step(dt, halfHeight) {
    const o = this.o;
    this.carry += this.birthRate * dt;
    const room = (o.max ?? 256) - this.list.length;
    let born = min(Math.floor(this.carry), room);
    this.carry -= Math.floor(this.carry);
    // Births spread over the frame, as SceneKit does: born in one batch they would travel as separate dots.
    const fresh = [];
    while (born-- > 0) { this.spawn(); const q = this.list[this.list.length - 1]; q.age = -random() * dt; fresh.push(q); }
    const g = o.gravity;
    this.list = this.list.filter(q => (q.age += dt) < q.life);
    for (const q of fresh) q.p.addScaledVector(q.v, -(dt - q.age));
    const pos = this.geometry.attributes.position.array, col = this.geometry.attributes.pcolor.array;
    const size = this.geometry.attributes.psize.array, ang = this.geometry.attributes.pangle.array;
    this.list.forEach((q, i) => {
      if (g) q.v.x += g[0] * dt, q.v.y += g[1] * dt, q.v.z += g[2] * dt;
      q.p.addScaledVector(q.v, dt);
      q.angle += (o.spin ?? 0) * PI / 180 * dt;
      const k = q.age / q.life;
      const c = this.colors ? this.colors(k) : q.color;
      pos[i * 3] = q.p.x; pos[i * 3 + 1] = q.p.y; pos[i * 3 + 2] = q.p.z;
      col[i * 4] = c[0]; col[i * 4 + 1] = c[1]; col[i * 4 + 2] = c[2];
      // Added light keeps the colour curve's brightness but not its alpha (measured: the flames' orange tail is
      // as bright on the Mac as with alpha 1); the opacity curve and the base colour's alpha still count.
      const alpha = this.colors && o.additive ? 1 : c[3];
      col[i * 4 + 3] = alpha * (this.opacity ? this.opacity(k) : 1);
      size[i] = q.size * PARTICLE_SCALE * (this.sizes ? this.sizes(k) : 1);
      ang[i] = q.angle;
    });
    for (const a of Object.values(this.geometry.attributes)) a.needsUpdate = true;
    this.geometry.setDrawRange(0, this.list.length);
    this.material.uniforms.halfHeight.value = halfHeight;
  }
}

// ── Thrusters ──────────────────────────────────────────────────────────────────────────────────────────────

const FLAMES = {
  fire: { colors: [[1, 0.93, 0.7, 0.55], [1, 0.55, 0.12, 0.4], [0.9, 0.2, 0.05, 0]], glow: [1, 0.7, 0.35] },
  ion: { colors: [[0.85, 1, 1, 0.55], [0.25, 0.78, 1, 0.4], [0.1, 0.25, 1, 0]], glow: CYAN },
};

class Thruster {
  constructor(size, images, world) {
    this.size = size;
    this.flame = new Particles({
      map: images.dot, additive: true, birthRate: 320, life: 0.3, lifeVar: 0.06, direction: [0, -1, 0], spread: 7,
      velocity: 1.1 * size, velocityVar: 0.25 * size, sphere: 0.012 * size, size: 0.11 * size,
      sizes: { values: [1, 0.72, 0.18], times: [0, 0.4, 1] }, max: 200,
    }, world);
    this.node = this.flame.node;
    this.glow = new THREE.Sprite(new THREE.SpriteMaterial({ map: images.dot, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true }));
    this.glow.position.set(0, -0.02 * size, 0);
    this.glow.renderOrder = 2;
    this.node.add(this.glow);
    this.style('fire');
  }
  style(s) {
    const f = FLAMES[s] ?? FLAMES.fire;
    this.flame.setColors(f.colors, [0, 0.35, 1]);
    this.glow.material.color = srgb(...f.glow);
  }
  /** power 0…1.5: flame length, density and glow. */
  update(power, flicker) {
    const p = max(0, power) * (0.9 + 0.2 * flicker);
    this.flame.birthRate = 420 * min(1, p * 1.2);
    this.flame.velocity = (0.7 + 1.2 * p) * this.size;
    this.glow.material.opacity = min(1, p * 0.9);
    const k = 0.3 * this.size * (0.7 + 0.4 * p);
    this.glow.scale.set(k, k, 1);
  }
}

// ── The face on the screen ─────────────────────────────────────────────────────────────────────────────────

/** Where the face sits in the model's emission texture: the screen's dark area, turned a quarter turn. */
const FACE = { x: 0.33, y: 0.376, width: 0.2, pixels: 256, r: 0.0145 };

class Face {
  constructor() {
    Object.assign(this, { mood: 'idle', blink: 1, lookX: 0, lookY: 0, eyeScale: 1, mouth: 0, happyEyes: false, privacy: false });
    [this.canvas, this.x] = canvas2d(FACE.pixels);
    this.texture = texture(this.canvas, THREE.SRGBColorSpace);
    this.texture.flipY = false;
    this.texture.wrapS = this.texture.wrapT = THREE.ClampToEdgeWrapping;
    // Mac: contentsTransform = translate(−origin), then scale(1 / width).
    this.texture.matrixAutoUpdate = false;
    this.texture.matrix.set(1 / FACE.width, 0, -FACE.x / FACE.width, 0, 1 / FACE.width, -FACE.y / FACE.width, 0, 0, 1);
  }
  get key() {
    return `${this.mood}${this.happyEyes ? 1 : 0}${this.privacy ? 1 : 0} ${this.blink.toFixed(2)} ${this.lookX.toFixed(3)} ${this.lookY.toFixed(3)} ${this.eyeScale.toFixed(2)} ${this.mouth.toFixed(2)}`;
  }
  /** Drawn as on the Mac, in CoreGraphics' coordinates (y up), so every arc and turn is the same. */
  draw() {
    const c = this.x, n = FACE.pixels, r = FACE.r, s = n / FACE.width, mood = this.mood;
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.fillStyle = '#000'; c.fillRect(0, 0, n, n);   // black outside the face, like the clamped border
    c.translate(0, n); c.scale(1, -1);
    c.translate(n / 2, n / 2); c.rotate(-PI / 2); c.scale(s, s); c.scale(-1, 1);
    const error = mood === 'error';
    const eye = error ? 'rgb(255,82,56)' : this.privacy ? 'rgb(199,153,255)' : 'rgb(219,250,255)';
    c.fillStyle = eye; c.strokeStyle = eye; c.lineCap = 'round'; c.lineJoin = 'round'; c.lineWidth = r * 0.55;
    const eyes = [[-0.0485 + this.lookX, 0.008 + this.lookY], [0.0485 + this.lookX, 0.008 + this.lookY]];
    const ellipse = (x, y, w, h) => { c.beginPath(); c.ellipse(x, y, w, h, 0, 0, 2 * PI); c.fill(); };
    for (const [ex, ey] of eyes) {
      c.beginPath();
      if (error) {
        const k = r * 0.8;
        c.moveTo(ex - k, ey - k); c.lineTo(ex + k, ey + k); c.moveTo(ex - k, ey + k); c.lineTo(ex + k, ey - k); c.stroke();
      } else if (mood === 'sleep') {
        c.arc(ex, ey + r * 0.2, r * 0.8, PI * 1.15, PI * 1.85, false); c.stroke();
      } else if (this.happyEyes || mood === 'happy') {
        c.arc(ex, ey - r * 0.4, r * 0.85, PI * 0.15, PI * 0.85, false); c.stroke();
      } else if (mood === 'think') {
        // Looking up: the lower lid is flat, like a dome.
        const w = r * 1.12, h = r * 1.3 * max(0.15, this.blink);
        c.moveTo(ex - w, ey - h * 0.15);
        c.bezierCurveTo(ex - w, ey + h * 1.25, ex + w, ey + h * 1.25, ex + w, ey - h * 0.15);
        c.closePath(); c.fill();
      } else {
        const h = r * this.eyeScale * max(0.08, this.blink), w = r * this.eyeScale;
        ellipse(ex, ey, w, h);
      }
    }
    // Blush when happy.
    if (mood === 'happy' || this.happyEyes) {
      c.fillStyle = 'rgba(255,115,153,0.55)';
      for (const [ex, ey] of eyes) ellipse(ex, ey - r * 2.0 + r * 0.35, r * 0.75, r * 0.35);
      c.fillStyle = eye;
    }
    const mx = this.lookX * 0.6, my = -0.02 + this.lookY * 0.5, k = r * 0.55;
    c.lineWidth = r * 0.4;
    c.beginPath();
    switch (mood) {
      case 'speak': {
        const h = max(r * 0.15, r * 1.4 * this.mouth), w = r * (0.9 + 0.3 * this.mouth);
        ellipse(mx, my, w, h);
        break;
      }
      case 'happy':
        c.moveTo(mx - r * 1.1, my + r * 0.3);
        c.arc(mx, my + r * 0.3, r * 1.1, PI, 0, false);
        c.closePath(); c.fill();
        break;
      case 'listen':
        // A soft, wide smile.
        c.arc(mx, my + k * 0.9, k * 1.25, PI * 1.25, PI * 1.75, false); c.stroke();
        break;
      case 'think':
        // "Hmm": a short line pushed to the side, slightly tilted.
        c.moveTo(mx - k * 0.2, my - k * 0.1); c.lineTo(mx - k * 1.4, my + k * 0.3); c.stroke();
        break;
      case 'sleep':
        c.ellipse(mx, my, k * 0.45, k * 0.45, 0, 0, 2 * PI); c.stroke();
        break;
      case 'error':
        c.moveTo(mx - k * 1.4, my);
        for (let i = 1; i <= 4; i++) c.lineTo(mx - k * 1.4 + i * k * 0.7, my + (i % 2 === 1 ? k * 0.5 : 0));
        c.stroke();
        break;
      default:
        c.moveTo(mx - k, my + k * 0.5); c.lineTo(mx, my - k * 0.3); c.lineTo(mx + k, my + k * 0.5); c.stroke();
    }
    this.texture.needsUpdate = true;
  }
}

// ── The robot (RobotKit.Robot) ─────────────────────────────────────────────────────────────────────────────

const STEP_RATE = 3.6, STEP_SWING = 0.2, LEG_LENGTH = 0.56;
/** Body travel per second while walking, in model units: two steps per swing, so the planted foot does not slide. */
export const WALK_SPEED = 2 * LEG_LENGTH * sin(STEP_SWING) / (PI / STEP_RATE);

/** Keeps an arm out of the head and the body, whatever the pose asks. side: 1 for the +x arm, −1 for the other. */
function armLimit(ex, ey, ez, side) {
  let z = ez * side;
  const forward = -ex;
  z = min(z, 2.2);
  z = max(z, forward > 0.4 ? -0.35 : 0.06);
  const x = z > 1.2 ? max(min(ex, 0.25), -0.6) : max(min(ex, 0.6), -1.3);
  return [x, ey, z * side];
}

class Robot {
  constructor(model, images, world) {
    const pivot = () => { const o = new THREE.Object3D(); o.rotation.order = ORDER; return o; };
    this.root = new THREE.Object3D();
    [this.spin, this.tilt, this.body, this.head, this.armNeg, this.armPos, this.legNeg, this.legPos, this.earNeg, this.earPos] =
      Array.from({ length: 10 }, pivot);
    this.root.add(this.spin);
    this.spin.add(this.tilt);
    this.tilt.position.set(0, 0.85, 0);
    this.tilt.add(this.body);
    this.body.position.set(0, -0.85, 0);
    this.body.add(model.root);
    this.root.updateMatrixWorld(true);
    const place = (node, parent, x, y, z) => {
      parent.add(node);
      parent.updateMatrixWorld(true);
      node.position.copy(parent.worldToLocal(new THREE.Vector3(x, y, z)));
      node.updateMatrixWorld(true);
    };
    place(this.head, this.body, 0, 1.1, 0);
    place(this.armNeg, this.body, -0.23, 0.97, 0); place(this.armPos, this.body, 0.23, 0.97, 0);
    place(this.legNeg, this.body, -0.1, 0.56, 0); place(this.legPos, this.body, 0.1, 0.56, 0);
    place(this.earNeg, this.head, -0.3, 1.39, -0.02); place(this.earPos, this.head, 0.3, 1.39, -0.02);

    const holders = {};
    for (const [name, n] of Object.entries(model.named)) { const h = n.children.find(c => c.isMesh); if (h) holders[name] = h; }
    // The rig needs this model's parts; another model is refused (the app then shows the satellite).
    for (const part of ['head', 'screen', 'body', 'bag', 'arms_high', 'legs_high', 'ears'])
      if (!holders[part]) throw new Error(`manca la parte ${part} nel modello`);
    this.root.updateMatrixWorld(true);
    for (const name of ['head', 'screen', 'cap', 'ears_part_1', 'ears_part_2']) if (holders[name]) this.head.attach(holders[name]);
    const split = (names, neg, pos) => {
      for (const name of names) {
        const h = holders[name];
        if (!h) continue;
        h.updateMatrixWorld(true);
        const w = h.matrixWorld.clone();
        const [a, b] = splitByX(h);
        h.removeFromParent();
        for (const [half, parent] of [[a, neg], [b, pos]]) {
          parent.add(half);
          parent.updateMatrixWorld(true);
          new THREE.Matrix4().copy(parent.matrixWorld).invert().multiply(w).decompose(half.position, half.quaternion, half.scale);
        }
      }
    };
    split(['arms_high', 'arms_high.002', 'arms_high.003', 'arm_joints', 'arm_joints_2', 'arm_joint_part', 'Cube.001', 'Cylinder.008', 'Cylinder.010'], this.armNeg, this.armPos);
    split(['legs_high', 'legs_high.001'], this.legNeg, this.legPos);
    split(['ears'], this.earNeg, this.earPos);
    // Only the model itself catches the mouse (layer 1), not flames or effects.
    this.body.traverse(n => { if (n.isMesh) n.layers.enable(1); });
    this.mouthMark = new THREE.Object3D();
    place(this.mouthMark, this.head, 0, 1.31, 0.27);

    // The screen gets its own material, so its face can be redrawn; the grey face printed in the base colour is
    // painted over with the screen's own dark.
    const screenMesh = holders.screen;
    const screen = screenMesh.material.clone();
    screenMesh.material = screen;
    this.face = new Face();
    screen.emissiveMap = this.face.texture;
    screen.emissive = new THREE.Color(1, 1, 1);
    screen.emissiveIntensity = 1.3;
    const base = screen.map?.image;
    if (base) {
      const w = base.width, h = base.height;
      const [c, x] = canvas2d(w, h);
      x.drawImage(base, 0, 0);
      const px = x.getImageData(Math.floor(0.375 * w), Math.floor(0.47 * h), 1, 1).data;
      x.fillStyle = `rgb(${px[0]},${px[1]},${px[2]})`;
      x.fillRect(0.392 * w, 0.40 * h, 0.066 * w, 0.15 * h);
      const t = texture(c, THREE.SRGBColorSpace);
      t.flipY = false; t.wrapS = t.wrapT = THREE.RepeatWrapping;
      screen.map = t;
    }
    this.screen = screen;
    this.drawnFace = '';

    // Jet nozzles under the backpack, and small ones under the feet.
    const metal = new THREE.MeshStandardMaterial({ color: srgb(0.2, 0.2, 0.2), metalness: 0.85, roughness: 0.3, envMapIntensity: ENV });
    this.thrusters = []; this.feet = [];
    for (const x of [-0.09, 0.09]) {
      const bell = new THREE.Mesh(new THREE.CylinderGeometry(0.028, 0.045, 0.06, 48), metal);
      bell.position.set(x, 0.69, -0.22);
      this.body.add(bell);
      const t = new Thruster(1, images, world);
      t.node.position.set(x, 0.655, -0.22);
      this.body.add(t.node);
      this.thrusters.push(t);
    }
    for (const [leg, x] of [[this.legNeg, -0.11], [this.legPos, 0.11]]) {
      const t = new Thruster(0.6, images, world);
      place(t.node, leg, x, -0.01, 0);
      this.feet.push(t);
    }

    // Effects for the states, around the head.
    this.effects = new THREE.Object3D();
    this.tilt.add(this.effects);
    const holo = () => new THREE.MeshBasicMaterial({ color: srgb(...CYAN), transparent: true, opacity: 0.85, blending: THREE.AdditiveBlending,
                                                       side: THREE.DoubleSide, depthWrite: false });
    // Mac 1.30.75 (user request): no blue gear and no blue dots above the head while thinking.
    const z = zShape();
    this.zs = [0, 1, 2].map(() => { const m = new THREE.Mesh(z, holo()); this.effects.add(m); return m; });

    const sparkleNode = new THREE.Object3D();
    sparkleNode.position.set(0, 0.1, 0);
    this.effects.add(sparkleNode);
    this.sparkles = new Particles({
      map: images.star, additive: true, life: 1, sphere: 0.75, velocity: 0.15, size: 0.07, sizeVar: 0.04, spin: 180,
      color: [1, 0.85, 0.3, 1], colorVar: [0.35, 0.2, 0], opacity: { values: [0, 1, 0], times: [0, 0.3, 1] }, max: 64,
    }, world);
    sparkleNode.add(this.sparkles.node);
    this.smoke = new Particles({
      map: images.dot, additive: false, life: 1.4, color: [0.45, 0.45, 0.45, 0.5], velocity: 0.25, direction: [0, 1, 0], spread: 25,
      size: 0.08, sizes: { values: [0.7, 2.6] }, opacity: { values: [0.6, 0] }, max: 64,
    }, world);
    this.sparks = new Particles({
      map: images.dot, additive: true, life: 0.5, color: [1, 0.8, 0.3, 1], velocity: 0.9, velocityVar: 0.4, spread: 70,
      direction: [0, 1, 0], size: 0.02, gravity: [0, -9.8 - 3, 0], max: 64,
    }, world);
    const vent = new THREE.Object3D();
    vent.position.set(0, 1.12, -0.22);
    this.body.add(vent);
    vent.add(this.smoke.node, this.sparks.node);
    this.particles = [...this.thrusters.map(t => t.flame), ...this.feet.map(t => t.flame), this.sparkles, this.smoke, this.sparks];

    Object.assign(this, { poke: -10, lean: 0, grounded: false, walk: 0, smooth: {} });
  }

  get walkAmount() { return this.smooth.walk ?? 0; }
  get facing() { return this.spin.rotation.y; }
  flames(style) { [...this.thrusters, ...this.feet].forEach(t => t.style(style)); }

  /** Points around the head at mouth height (its widest outline there), in world space. */
  headOutline() {
    return Array.from({ length: 24 }, (_, i) => {
      const a = i / 24 * 2 * PI;
      return this.head.localToWorld(new THREE.Vector3(cos(a) * 0.29, 0.21, sin(a) * 0.27));
    });
  }

  ease(key, target, rate = 0.1) {
    const v = (this.smooth[key] ?? target) + (target - (this.smooth[key] ?? target)) * rate;
    this.smooth[key] = v;
    return v;
  }

  /** One step of the Mac's 30 Hz tick. */
  update(t, mood, level) {
    const ease = this.ease.bind(this);
    const idle = mood === 'idle', listen = mood === 'listen', think = mood === 'think', speak = mood === 'speak';
    const sleep = mood === 'sleep', error = mood === 'error';
    const poked = t - this.poke < 2.4;
    const happy = mood === 'happy' || poked;
    // Little routines while waiting: a wave every 14 s, a look around, a pirouette every 26 s.
    const waveT = t % 14;
    const g = ease('ground', this.grounded ? 1 : 0, 0.1);
    const w = ease('walk', this.walk !== 0 && this.grounded ? 1 : 0, 0.12);
    const phase = t * STEP_RATE;
    const legSwing = STEP_SWING * sin(phase) * w;
    const waving = (idle && waveT > 10 && waveT < 12.4 && w < 0.1) || poked;
    const spinT = t % 26;
    const pirouette = idle && spinT > 20 && spinT < 21.4 && !this.grounded;
    const gx = sin(t * 0.55);
    const glance = ease('glance', w > 0.5 && t % 7 > 5.2 ? 1 : 0, 0.08);
    const happySpin = mood === 'happy' && t % 4 < 1.1;

    // Floating: a slow bob; lower and calmer asleep; jittery on error.
    const bob = (sin(t * 1.7) * (sleep ? 0.02 : 0.045)) * (1 - g) + (happy ? abs(sin(t * 6)) * 0.06 : 0) + LEG_LENGTH * (cos(legSwing) - 1);
    const shake = error ? sin(t * 37) * 0.012 : 0;
    this.root.position.set(shake, ease('base', sleep && !this.grounded ? -0.12 : 0, 0.04) + bob, 0);
    let yaw = ease('yaw', this.walk > 0 && this.grounded ? 1.15 : this.walk < 0 && this.grounded ? -1.15 : 0, 0.08);
    if (pirouette) yaw += (spinT - 20) / 1.4 * 2 * PI;
    if (happySpin) yaw += (t % 4) / 1.1 * 2 * PI;
    this.spin.rotation.y = yaw;
    this.tilt.rotation.set(ease('lean', listen ? 0.14 : sleep ? 0.08 : speak ? 0.05 : 0) + sin(t * 0.9) * 0.025 * (1 - w) + 0.06 * w, 0,
      ease('side', -this.lean * 0.6, 0.08) + sin(t * 0.7) * 0.03 * (1 - g) + (think ? sin(t * 0.55 + 0.6) * 0.05 : 0) + sin(phase) * 0.035 * w);
    // Head.
    const lookT = t % 9;
    const lookAround = idle ? (lookT > 5 && lookT < 6.8 ? 0.45 : lookT > 6.8 && lookT < 8 ? -0.35 : 0) : 0;
    this.head.rotation.set(
      ease('hp', sleep ? 0.3 : think ? -0.15 : speak ? sin(t * 4.5) * 0.05 + level * 0.06
        : listen ? 0.05 + max(0, sin(t * 3.2)) * 0.07 * (sin(t * 0.7) > 0.3 ? 1 : 0) : sin(t * 0.6) * 0.04),
      ease('hy', lookAround + (think ? gx * 0.22 : 0) - this.walk * 0.75 * glance, 0.08),
      ease('hr', listen ? -0.16 : think ? -gx * 0.1 : happy ? sin(t * 6) * 0.14 : sleep ? -0.1 : 0));
    // Arms: hang and sway; wave with the +x arm; flail on error; up for joy.
    const sway = sin(t * 1.7 + 0.5) * 0.06;
    const wave = waving ? ease('wave', 1.85, 0.15) + sin(t * 11) * 0.28 : ease('wave', 0, 0.12);
    const cheer = mood === 'happy' ? ease('cheer', 0.9 + sin(t * 6) * 0.2) : ease('cheer', 0);
    const flail = error ? sin(t * 9) * 0.5 : 0;
    const attend = ease('attend', listen ? 1 : 0, 0.1);
    const ponder = ease('ponder', think ? 1 : 0, 0.08);
    const tap = sin(t * 8) * 0.07 * ponder;
    const swing = sin(phase) * 0.26 * w;
    this.armPos.rotation.set(...armLimit(sway * (1 - attend) * (1 - w) - attend * 0.55 + swing, 0, 0.12 + wave + cheer + flail - attend * 0.3, 1));
    this.armNeg.rotation.set(...armLimit(-sway * (1 - attend) * (1 - w) - attend * 0.55 - ponder * 0.85 + tap - swing, 0,
      -0.12 - cheer + flail * 0.7 + attend * 0.3 + ponder * 0.12, -1));
    // Legs dangle, out of step.
    const dangle = sleep ? 0.05 : 0.11;
    this.legNeg.rotation.set((0.12 + sin(t * 1.7) * dangle) * (1 - g) + legSwing, 0, -0.04);
    this.legPos.rotation.set((0.12 + sin(t * 1.7 + 2.2) * dangle) * (1 - g) - legSwing, 0, 0.04);
    // Ears: perk forward to listen, droop asleep, wiggle for joy.
    const ear = ease('ear', listen ? 0.4 : sleep ? -0.3 : 0);
    this.earNeg.rotation.x = ear + (happy ? sin(t * 12) * 0.25 : sin(t * 2.1) * 0.04);
    this.earPos.rotation.x = ear + (happy ? sin(t * 12 + 1.5) * 0.25 : sin(t * 2.3 + 1) * 0.04);

    // Thrusters: steady, pulsing with the voice, low asleep, sputtering on error.
    const sputter = error ? (sin(t * 23) + sin(t * 37) > 0.4 ? 1 : 0.08) : 1;
    const power = (ease('power', sleep ? 0.35 : happy ? 1.3 : speak ? 0.9 + level * 0.5 : 1, 0.15) * sputter + cos(t * 1.7) * 0.12) * (1 - g);
    this.thrusters.forEach((th, i) => th.update(power, random() * 0.6 + i * 0.1));
    for (const th of this.feet) th.update(power * 0.85, random());

    // Face.
    const cycle = t % 3.7;
    const blink = cycle < 0.14 ? abs(cycle - 0.07) / 0.07 : (t % 11.1 < 0.4 && cycle > 0.3 && cycle < 0.44 ? 0.1 : 1);
    const f = this.face;
    f.mood = poked ? 'happy' : mood;
    f.happyEyes = waving;
    f.blink = blink;
    f.eyeScale = ease('es', listen ? 1.1 : speak ? 1 + level * 0.1 : 1);
    f.lookX = ease('lx', think ? -gx * 0.011 : idle ? lookAround * -0.04 : 0);
    f.lookY = ease('ly', think ? 0.007 : sleep ? -0.004 : 0);
    f.mouth = ease('mouth', speak ? level : 0, 0.4);
    if (f.key !== this.drawnFace) { this.drawnFace = f.key; f.draw(); }
    this.screen.emissiveIntensity = sleep ? 0.6 : 1.3;

    // Effects.
    this.zs.forEach((z, i) => {
      const p = (t * 0.35 + i / 3) % 1;
      z.position.set(0.18 + p * 0.25 + sin(p * 6) * 0.04, 0.75 + p * 0.55, 0.05);
      z.scale.setScalar(0.012 + p * 0.01);
      z.material.opacity = 0.85 * (sleep ? sin(p * PI) * ease('zs', 1, 0.05) : ease('zs', 0, 0.2) * sin(p * PI));
      z.visible = z.material.opacity > 0.002;
    });
    this.sparkles.birthRate = mood === 'happy' || poked ? 30 : 0;
    this.smoke.birthRate = error ? 14 : 0;
    this.sparks.birthRate = error && sin(t * 5) > 0.6 ? 60 : 0;
  }
}

/** Mac: the voice level faked while the bubble types an answer and nothing is spoken. */
const fakeVoice = (t) => max(0, 0.5 + 0.5 * sin(t * 13)) * (0.55 + 0.45 * sin(t * 2.1));

/**
 * The robot in a canvas (the window is 160×264 on the Mac, 200×330 when large). Loads the user's model from
 * `model` (a folder URL with scene.gltf); throws if it is missing or is another model.
 * robot.set({ state, voice, privacy, walk, grounded, lean, typing, flames }); robot.poke() for a pat.
 * 30 fps, 24 while waiting and standing still, nothing while hidden (as on the Mac).
 */
export async function createRobot(canvas, { model }) {
  THREE.ColorManagement.enabled = true;
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, premultipliedAlpha: true });
  renderer.setClearColor(0x000000, 0);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.NoToneMapping;
  renderer.useLegacyLights = true;
  const scene = new THREE.Scene();
  scene.environment = studio(renderer);
  // Mac: SceneKit camera, 29° vertical, at (1, 1.25, 5.4) looking at (0, 0.8, 0).
  const camera = new THREE.PerspectiveCamera(29, 160 / 264, 0.5, 60);
  camera.position.set(1.0, 1.25, 5.4);
  camera.lookAt(0, 0.8, 0);
  const light = (color, intensity, x, y) => {
    const l = new THREE.DirectionalLight(color, intensity / 1000);
    // A SceneKit directional light shines along its node's −z.
    const d = new THREE.Vector3(0, 0, -1).applyEuler(new THREE.Euler(x, y, 0, ORDER));
    l.position.copy(d.multiplyScalar(-10));
    scene.add(l, l.target);
  };
  light(srgb(1, 0.96, 0.9), 1100, -0.6, 0.5);
  light(srgb(...CYAN), 900, -0.3, 2.7);
  scene.add(new THREE.AmbientLight(0xffffff, 0.18));

  const images = { dot: softDot(), star: star() };
  const robot = new Robot(await loadRobotModel(model), images, scene);
  scene.add(robot.root);

  const input = { state: 'idle', voice: 0, privacy: false, walk: 0, grounded: false, lean: 0, typing: false, flames: 'fire', mood: null };
  let flames = 'fire';
  let start = null, logicT = 0, last = null, voice = 0;
  let lastFrame = 0, frames = 0, fps = 0, fpsStart = performance.now(), calls = 0;

  function moodNow() {
    if (input.mood) return input.mood;   // the preview page only
    switch (input.state) {
      case 'listening': return 'listen';
      case 'transcribing': case 'thinking': case 'acting': case 'booting': return 'think';
      case 'speaking': return 'speak';
      case 'error': return 'error';
      // Waiting it stays awake (no sleep in deep idle), and speaks while the bubble types.
      default: return input.typing ? 'speak' : 'idle';
    }
  }
  function tick(t, dt) {
    const mood = moodNow();
    const raw = min(1, max(0, input.voice * 1.8));
    voice += (raw - voice) * (1 - exp(-dt * 18));
    const level = mood === 'speak' ? (input.state === 'speaking' ? voice : fakeVoice(t)) : 0;
    robot.face.privacy = !!input.privacy;
    robot.walk = input.walk; robot.grounded = !!input.grounded; robot.lean = input.lean;
    if (input.flames !== flames) { flames = input.flames; robot.flames(flames === 'ion' ? 'ion' : 'fire'); }
    robot.update(t, mood, level);
  }
  // The scene is drawn in linear light and only then encoded for the screen, as SceneKit does: the flames' particles
  // add up in linear light (orange, softly saturating) and not in sRGB, where they turned white at once.
  const linearTarget = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, samples: 4 });
  const copy = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.ShaderMaterial({
    uniforms: { map: { value: linearTarget.texture } },
    vertexShader: 'varying vec2 vUv; void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }',
    fragmentShader: 'uniform sampler2D map; varying vec2 vUv; void main() { gl_FragColor = texture2D(map, vUv); \n#include <colorspace_fragment>\n }',
    blending: THREE.NoBlending, depthTest: false, depthWrite: false,
  }));
  copy.frustumCulled = false;
  const copyScene = new THREE.Scene().add(copy), copyCamera = new THREE.Camera();
  function resize() {
    const scale = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(canvas.clientWidth * scale)), h = Math.max(1, Math.round(canvas.clientHeight * scale));
    if (canvas.width !== w || canvas.height !== h) {
      renderer.setSize(w, h, false);
      linearTarget.setSize(w, h);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    }
  }
  function draw(now) {
    resize();
    const s = now / 1000;
    start ??= s;
    const t = s - start;
    // The Mac moves the robot 30 times a second, whatever it draws.
    if (last === null) { tick(t, 0); logicT = t; }
    let n = 0;
    while (logicT + 1 / 30 <= t && n++ < 4) { logicT += 1 / 30; tick(logicT, 1 / 30); }
    if (logicT + 1 / 30 <= t) logicT = t;
    const dt = last === null ? 0 : min(0.1, t - last);
    last = t;
    robot.root.updateMatrixWorld(true);
    for (const z of robot.zs) {
      // Billboards: the Zs always face the camera.
      z.quaternion.copy(z.parent.getWorldQuaternion(new THREE.Quaternion()).invert().multiply(camera.quaternion));
    }
    for (const p of robot.particles) p.step(dt, renderer.getDrawingBufferSize(new THREE.Vector2()).y / 2);
    renderer.setRenderTarget(linearTarget);
    renderer.clear();
    renderer.render(scene, camera);
    calls = renderer.info.render.calls;
    renderer.setRenderTarget(null);
    renderer.render(copyScene, copyCamera);
  }
  const interval = () => moodNow() === 'idle' && input.walk === 0 ? 1000 / 24 : 1000 / 30;
  let running = true;
  function frame(now) {
    if (!running) return;
    requestAnimationFrame(frame);
    if (document.hidden || now - lastFrame < interval() - 2) return;
    lastFrame = now;
    draw(now);
    frames++;
    if (now - fpsStart > 1000) { fps = frames * 1000 / (now - fpsStart); frames = 0; fpsStart = now; }
  }
  requestAnimationFrame(frame);

  /** World point → canvas point in CSS pixels, from the top left. */
  const project = (v) => {
    const p = v.clone().project(camera);
    return { x: (p.x + 1) / 2 * canvas.clientWidth, y: (1 - p.y) / 2 * canvas.clientHeight };
  };
  const pick = new THREE.WebGLRenderTarget(1, 1);
  const pickMaterial = new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide });
  const pixel = new Uint8Array(4);

  return {
    set(values) { Object.assign(input, values); },
    /** A click on the robot: it lights up and waves. */
    poke() { robot.poke = logicT; },
    /** One frame now, even while hidden (tests and snapshots). */
    draw(now = performance.now()) { draw(now); },
    /** For the window: the mouth and the head's edges (the speech bubble), the feet (fraction of the height from
     * the bottom: the window stands on them) and the walking speed in CSS pixels a second. */
    metrics() {
      const xs = robot.headOutline().map(p => project(p).x);
      const o = project(new THREE.Vector3(0, 0, 0)), x1 = project(new THREE.Vector3(1, 0, 0));
      const feet = o.y / Math.max(1, canvas.clientHeight);
      return {
        mouth: project(robot.mouthMark.getWorldPosition(new THREE.Vector3())),
        left: Math.min(...xs), right: Math.max(...xs),
        feet: feet > 0.5 ? 1 - feet : feet,
        walk: WALK_SPEED * robot.walkAmount * abs(sin(robot.facing)) * abs(x1.x - o.x),
      };
    },
    /** True when the point (CSS pixels from the top left) is on the model itself, not on flames or effects:
     * only there the window catches the mouse. */
    hit(x, y) {
      const w = canvas.clientWidth, h = canvas.clientHeight;
      if (x < 0 || y < 0 || x >= w || y >= h) return false;
      camera.setViewOffset(w, h, x, y, 1, 1);
      camera.layers.set(1);
      scene.overrideMaterial = pickMaterial;
      const hidden = robot.particles.map(p => p.points);
      hidden.forEach(p => { p.visible = false; });
      renderer.setRenderTarget(pick);
      renderer.setClearColor(0x000000, 0);
      renderer.clear();
      renderer.render(scene, camera);
      renderer.readRenderTargetPixels(pick, 0, 0, 1, 1, pixel);
      renderer.setRenderTarget(null);
      hidden.forEach(p => { p.visible = true; });
      scene.overrideMaterial = null;
      camera.layers.set(0);
      camera.clearViewOffset();
      return pixel[3] > 0;
    },
    get fps() { return fps; },
    get drawCalls() { return calls; },
    /** Stops drawing and frees the GPU memory (another style chosen, or the window closed). */
    dispose() { running = false; renderer.dispose(); },
    renderer, scene, robot,
  };
}
