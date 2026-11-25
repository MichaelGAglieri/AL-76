// How finely the sphere, the satellite and the robot are drawn on this PC. Without a usable GPU (WebGL on the
// software renderer: a virtual machine, a broken driver) every pixel costs CPU time, so they start at one canvas
// pixel per screen pixel and without multisampling. Where the frame rate still stays well under the wanted one
// for a few seconds, the drawing resolution steps down (the window and its size do not change, the image gets a
// little softer) until it keeps up. Two testers' gaming laptops drew the sphere very slowly (25/11).

const SOFTWARE = /swiftshader|llvmpipe|softpipe|software|basic render|microsoft basic/i;
const STEPS = [2, 1.5, 1, 0.75, 0.6];

/** The GPU WebGL runs on, as the browser names it ("" when it does not say). */
export function rendererName(renderer) {
  try {
    const gl = renderer.getContext();
    const info = gl.getExtension('WEBGL_debug_renderer_info');
    return info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : '';
  } catch { return ''; }
}

/** Whether WebGL runs on the software renderer here, asked before a renderer exists (multisampling of the
 * canvas is chosen when the renderer is created). */
export const softwareGpu = (() => {
  try {
    const gl = document.createElement('canvas').getContext('webgl2') || document.createElement('canvas').getContext('webgl');
    if (!gl) return false;
    const info = gl.getExtension('WEBGL_debug_renderer_info');
    const name = info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : '';
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return SOFTWARE.test(name);
  } catch { return false; }
})();

export class Quality {
  constructor(renderer) {
    this.renderer = rendererName(renderer);
    this.software = SOFTWARE.test(this.renderer);
    // On the software renderer: at most 1 canvas pixel per screen pixel.
    this.step = this.software ? STEPS.indexOf(1) : 0;
    this.slow = 0;
    this.checked = 0;
  }

  /** Canvas pixels per CSS pixel. */
  get ratio() { return Math.min(window.devicePixelRatio || 1, STEPS[this.step]); }

  /** Multisampling only with a real GPU. */
  get samples() { return this.software ? 0 : 4; }

  /** Called once a second with the measured and the wanted frame rate; true when the resolution went down. */
  feed(fps, target) {
    const now = performance.now();
    if (document.hidden || fps <= 0 || now - this.checked < 1900) return false;
    this.checked = now;
    this.slow = fps < target * 0.7 ? this.slow + 1 : 0;
    if (this.slow < 3) return false;
    this.slow = 0;
    // The next step that really lowers the resolution (the higher ones may be above the screen's own density).
    const next = STEPS.findIndex((s, i) => i > this.step && s < this.ratio - 0.01);
    if (next < 0) return false;
    this.step = next;
    return true;
  }
}
