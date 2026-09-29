import * as pc from './playcanvas.min.mjs';
import { detect } from './capabilities.js?v=2026-09-29-9';

const config = JSON.parse(document.getElementById('viewer-config').textContent);
const canvas = document.getElementById('canvas');
const status = document.getElementById('status');
const vrButton = document.getElementById('enter-vr');
const resetButton = document.getElementById('reset');
const budgetSelect = document.getElementById('budget');
const fps = document.getElementById('fps');
const presets = { low: 0.15, medium: 0.35, high: 0.65, full: 1 };
const query = new URLSearchParams(location.search);
const xrTest = query.get('xrtest') === '1';
const xrFill = xrTest && query.get('xrfill') === '1';
const requestedExit = Number(query.get('xrautoexit'));
const xrAutoExitSeconds = xrTest ? 10 :
  query.has('xrautoexit') && Number.isFinite(requestedExit) ? Math.max(1, Math.min(60, requestedExit)) : 0;
let savedBudget = null;
try { savedBudget = localStorage.getItem('vrphotoBudget'); } catch (_) { /* storage is optional */ }
if (!Object.keys(presets).includes(savedBudget)) savedBudget = null;
const chosenBudget = query.get('budget') ?? savedBudget ?? config.budget;
let fraction = presets[chosenBudget] ?? Number(chosenBudget);
if (!(fraction > 0 && fraction <= 1)) fraction = 1;
budgetSelect.value = Object.keys(presets).find(name => presets[name] === fraction) ?? 'full';
budgetSelect.addEventListener('change', () => {
  try { localStorage.setItem('vrphotoBudget', budgetSelect.value); } catch (_) { /* URL still works */ }
  const url = new URL(location.href);
  url.searchParams.set('budget', budgetSelect.value);
  location.href = url.href;
});

function setStatus(message) { status.textContent = message; }

try {
  const caps = await detect();
  // Prefer the proven WebGL2 XR path. WebGPU XR remains an explicit diagnostic opt-in.
  const wantsGpu = config.renderer === 'webgpu' && caps['WebGPU + WebXR support'] === true;
  let type = wantsGpu ? pc.DEVICETYPE_WEBGPU : pc.DEVICETYPE_WEBGL2;
  let device;
  try {
    device = await pc.createGraphicsDevice(canvas, {
      deviceTypes: [type], antialias: false, alpha: true, xrCompatible: true
    });
  } catch (error) {
    if (type !== pc.DEVICETYPE_WEBGPU) throw error;
    type = pc.DEVICETYPE_WEBGL2;
    device = await pc.createGraphicsDevice(canvas, {
      deviceTypes: [type], antialias: false, alpha: true, xrCompatible: true
    });
  }
  const options = new pc.AppOptions();
  options.graphicsDevice = device;
  options.componentSystems = [pc.RenderComponentSystem, pc.CameraComponentSystem, pc.GSplatComponentSystem];
  options.resourceHandlers = [pc.GSplatHandler];
  options.xr = pc.XrManager;
  const app = new pc.AppBase(canvas);
  app.init(options);
  app.setCanvasFillMode(pc.FILLMODE_FILL_WINDOW);
  app.setCanvasResolution(pc.RESOLUTION_AUTO);
  app.start();
  window.addEventListener('resize', () => { app.resizeCanvas(); app.renderNextFrame = true; });
  // Keep updating the splat sorter, but stop redrawing an unchanged desktop view.
  // The sorter requests another rendered frame when its result changes.
  app.systems.gsplat.on('frame:request', () => { app.renderNextFrame = true; });
  let readyFrames = 0;
  let xrPending = false;
  app.systems.gsplat.on('frame:ready', (_camera, _layer, ready, loadingCount) => {
    readyFrames = ready && loadingCount === 0 ? readyFrames + 1 : 0;
    if (readyFrames >= 2 && !xrPending && !app.xr.active) app.autoRender = false;
  });

  const rig = new pc.Entity('Photo origin');
  app.root.addChild(rig);
  const camera = new pc.Entity('Photo camera');
  camera.addComponent('camera', { clearColor: new pc.Color(0.025, 0.028, 0.035), nearClip: 0.01, farClip: 1000 });
  rig.addChild(camera);

  const splat = new pc.Entity('SHARP splat');
  // SHARP uses OpenCV (+X right, +Y down, +Z forward). PlayCanvas looks toward -Z.
  splat.setEulerAngles(180, 0, 0);
  app.root.addChild(splat);
  let marker;
  if (xrTest) {
    marker = new pc.Entity('XR diagnostic cube');
    marker.addComponent('render', { type: 'box' });
    marker.setPosition(0, 0, -1);
    marker.setLocalScale(0.25, 0.25, 0.25);
    const material = new pc.StandardMaterial();
    material.emissive = new pc.Color(1, 0, 0);
    material.update();
    marker.render.material = material;
    app.root.addChild(marker);
  }
  if (!xrTest) {
    const assetUrl = new URL(config.plyUrl, location.href);
    assetUrl.searchParams.set('budget', String(fraction));
    const asset = new pc.Asset('scene', 'gsplat', { url: assetUrl.pathname + assetUrl.search });
    app.assets.add(asset);
    await new Promise((resolve, reject) => {
      asset.once('load', resolve);
      asset.once('error', reject);
      app.assets.load(asset);
    });
    splat.addComponent('gsplat', { asset });
    // Raw PLY reduction is done by the server; native budget is for future LOD assets.
    app.scene.gsplat.splatBudget = 10000000;
    app.scene.gsplat.antiAlias = query.get('splatAA') !== '0';
  } else {
    budgetSelect.disabled = true;
    app.autoRender = false;
    app.renderNextFrame = true;
  }

  let yaw = 0, pitch = 0, distance = 0, panX = 0, panY = 0;
  let dragging = false, panMode = false, lastX = 0, lastY = 0;
  let xrAligned = false;
  function applyDesktopCamera() {
    if (app.xr.active) return;
    camera.setLocalPosition(panX, panY, distance);
    camera.setLocalEulerAngles(pitch, yaw, 0);
    app.renderNextFrame = true;
  }
  function reset() {
    yaw = pitch = panX = panY = 0;
    distance = 0;
    rig.setPosition(0, 0, 0);
    rig.setEulerAngles(0, 0, 0);
    camera.setLocalPosition(0, 0, 0);
    camera.setLocalEulerAngles(0, 0, 0);
    app.renderNextFrame = true;
  }
  reset();
  resetButton.addEventListener('click', reset);
  canvas.addEventListener('pointerdown', event => {
    if (app.xr.active) return;
    dragging = true; panMode = event.button === 2 || event.shiftKey;
    lastX = event.clientX; lastY = event.clientY;
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointerup', () => { dragging = false; });
  canvas.addEventListener('contextmenu', event => event.preventDefault());
  canvas.addEventListener('pointermove', event => {
    if (!dragging || app.xr.active) return;
    const dx = event.clientX - lastX, dy = event.clientY - lastY;
    lastX = event.clientX; lastY = event.clientY;
    if (panMode) { panX -= dx * 0.002; panY += dy * 0.002; }
    else { yaw += dx * 0.15; pitch = Math.max(-80, Math.min(80, pitch + dy * 0.15)); }
    applyDesktopCamera();
  });
  canvas.addEventListener('wheel', event => { if (!app.xr.active) { distance = Math.max(-1, Math.min(4, distance + event.deltaY * 0.002)); applyDesktopCamera(); } event.preventDefault(); }, { passive: false });
  let frames = 0, lastFrame = performance.now();
  app.on('update', () => {
    frames++;
    const now = performance.now();
    if (now - lastFrame > 1000) { fps.textContent = `${app.autoRender ? `${Math.round(frames * 1000 / (now - lastFrame))} FPS` : 'Idle'} · ${type === pc.DEVICETYPE_WEBGPU ? 'WebGPU' : 'WebGL2'}`; frames = 0; lastFrame = now; }
  });
  vrButton.disabled = !app.xr || !app.xr.isAvailable(pc.XRTYPE_VR);
  app.xr?.on('available', () => { vrButton.disabled = !app.xr.isAvailable(pc.XRTYPE_VR); });
  function recordXR(message) {
    const event = { time: new Date().toISOString(), scene: config.plyUrl, message };
    try {
      const history = JSON.parse(localStorage.getItem('vrphotoXRHistory') || '[]');
      history.push(event);
      localStorage.setItem('vrphotoXRHistory', JSON.stringify(history.slice(-40)));
      localStorage.setItem('vrphotoLastXR', `${event.time} · ${message}`);
    } catch (_) { /* optional diagnostics */ }
    fetch('/debug/xr-event', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(event),
      keepalive: true
    }).catch(() => { /* the server may be unavailable */ });
  }
  window.addEventListener('error', event => recordXR(`JavaScript error: ${event.message}`));
  window.addEventListener('unhandledrejection', event => recordXR(`Promise error: ${event.reason}`));
  canvas.addEventListener('webglcontextlost', () => recordXR('WebGL context lost'));
  let xrFrames = 0;
  let xrRenderPasses = 0;
  app.on('postrender', () => {
    if (!app.xr.active) return;
    xrRenderPasses++;
    if (xrRenderPasses === 1 || xrRenderPasses === 30) {
      recordXR(`${xrRenderPasses} XR render passes completed`);
    }
    if (xrFill) {
      const layer = app.xr.session?.renderState.baseLayer;
      const gl = device.gl;
      if (layer && gl) {
        gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
        if (xrRenderPasses === 30) {
          const pixel = new Uint8Array(4);
          gl.readPixels(Math.floor(layer.framebufferWidth / 4), Math.floor(layer.framebufferHeight / 2),
            1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
          recordXR(`PlayCanvas XR left-eye center pixel before red fill: ${Array.from(pixel).join(',')}; GL error ${gl.getError()}`);
        }
        gl.disable(gl.SCISSOR_TEST);
        gl.colorMask(true, true, true, true);
        gl.viewport(0, 0, layer.framebufferWidth, layer.framebufferHeight);
        gl.clearColor(1, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        if (xrRenderPasses === 1) {
          recordXR(`PlayCanvas XR layer ${layer.framebufferWidth}x${layer.framebufferHeight}; default framebuffer matches ${device.defaultFramebuffer === layer.framebuffer}; GL error ${gl.getError()}`);
        }
      }
    }
  });
  let diagnosticTimeout;
  vrButton.addEventListener('click', () => {
    reset();
    xrPending = true;
    app.autoRender = true;
    xrAligned = false;
    xrFrames = 0;
    xrRenderPasses = 0;
    recordXR(`Enter VR requested · ${type === pc.DEVICETYPE_WEBGPU ? 'WebGPU' : 'WebGL2'} · budget ${fraction}`);
    if (xrAutoExitSeconds) diagnosticTimeout = setTimeout(() => {
      if (app.xr?.session) {
        recordXR(`Timeout: ending VR after ${xrAutoExitSeconds} seconds`);
        app.xr.end();
      }
    }, xrAutoExitSeconds * 1000);
    // No desktop orbit or artificial locomotion runs during the XR session.
    app.xr.start(camera.camera, pc.XRTYPE_VR, pc.XRSPACE_LOCALFLOOR, {
      callback: error => {
        if (!error) return;
        xrPending = false;
        clearTimeout(diagnosticTimeout);
        app.autoRender = false;
        app.renderNextFrame = true;
        recordXR(`VR start failed: ${error.message}`);
        if (type === pc.DEVICETYPE_WEBGPU) {
          const url = new URL(location.href);
          url.searchParams.set('renderer', 'webgl2');
          location.href = url.href;
        } else {
          setStatus(`VR start failed: ${error.message}`);
        }
      }
    });
  });
  app.xr?.on('start', () => {
    xrPending = false;
    app.autoRender = true;
    vrButton.disabled = true;
    recordXR('VR session started; rendering enabled; waiting for first head pose');
    setStatus('VR active · waiting for head pose');
  });
  app.xr?.on('update', () => {
    xrFrames++;
    if (xrTest) {
      // Keep the diagnostic cube directly in front of the tracked head.
      const inFront = camera.getRotation().transformVector(new pc.Vec3(0, 0, -1.2));
      marker.setPosition(camera.getPosition().clone().add(inFront));
      marker.setRotation(camera.getRotation());
      if (!xrAligned) {
        xrAligned = true;
        recordXR('First tracked pose received; diagnostic cube positioned');
      }
      if (xrFrames === 30) recordXR('30 XR updates received');
      return;
    }
    if (!xrAligned) {
      // Place the first headset pose at the original SHARP camera (world origin).
      // Later headset poses remain relative to that first pose: natural 6DoF.
      const originRotation = camera.getLocalRotation().clone().invert();
      const originOffset = originRotation.transformVector(camera.getLocalPosition().clone().mulScalar(-1));
      rig.setRotation(originRotation);
      rig.setPosition(originOffset);
      xrAligned = true;
      recordXR('First head pose aligned; XR updates running');
      setStatus('VR active · head pose aligned');
    } else if (xrFrames === 30) {
      recordXR('30 XR updates received');
    }
  });
  app.xr?.on('error', error => {
    recordXR(`XR error: ${error.message || error}`);
    setStatus(`XR error: ${error.message || error}`);
  });
  app.xr?.on('end', () => {
    xrPending = false;
    clearTimeout(diagnosticTimeout);
    recordXR(`VR ended after ${xrFrames} pose updates and ${xrRenderPasses} render passes`);
    reset();
    app.autoRender = false;
    app.renderNextFrame = true;
    vrButton.disabled = !app.xr.isAvailable(pc.XRTYPE_VR);
    setStatus('VR ended');
  });
  setStatus(xrTest ? `XR diagnostic cube ready · no PLY loaded · ${xrFill ? 'XR buffer will be filled red · ' : ''}VR auto-exits after 10 s` :
    `Ready · ${type === pc.DEVICETYPE_WEBGPU ? 'WebGPU' : 'WebGL2'} · ${Math.round(fraction * 100)}% splats${xrAutoExitSeconds ? ` · VR auto-exits after ${xrAutoExitSeconds} s` : ''}`);
} catch (error) {
  console.error(error);
  setStatus(`Viewer error: ${error.message || error}`);
}
