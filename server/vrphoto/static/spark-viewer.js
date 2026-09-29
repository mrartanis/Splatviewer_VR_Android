import * as THREE from 'three';
import { SparkRenderer, SplatMesh } from './spark.module.js';

const config = JSON.parse(document.getElementById('viewer-config').textContent);
const canvas = document.getElementById('canvas');
const preview = document.getElementById('preview');
const status = document.getElementById('status');
const vrButton = document.getElementById('enter-vr');
const budgetSelect = document.getElementById('budget');
const fps = document.getElementById('fps');
const sendLogButton = document.getElementById('send-xr-log');
const sendLogStatus = document.getElementById('send-log-status');
const debug = await fetch('/api/runtime', { cache: 'no-store' })
  .then(response => response.ok ? response.json() : { debug: false })
  .then(config => config.debug === true)
  .catch(() => false);
fps.textContent = 'Static preview';
const presets = { low: 0.15, medium: 0.35, high: 0.65, full: 1 };
const query = new URLSearchParams(location.search);
const xrTest = debug && query.get('xrtest') === '1';
const requestedExit = Number(query.get('xrautoexit'));
const xrAutoExitSeconds = xrTest ? 10 :
  debug && query.has('xrautoexit') && Number.isFinite(requestedExit) ? Math.max(1, Math.min(60, requestedExit)) : 0;
let savedBudget = null;
try { savedBudget = localStorage.getItem('vrphotoBudget'); } catch (_) { /* storage is optional */ }
if (!Object.keys(presets).includes(savedBudget)) savedBudget = null;
const chosenBudget = query.get('budget') ?? savedBudget ?? config.budget;
let fraction = presets[chosenBudget] ?? Number(chosenBudget);
if (!(fraction > 0 && fraction <= 1)) fraction = 1;

function setStatus(message) { status.textContent = message; }
function recordXR(message) {
  if (!debug) return;
  const event = { time: new Date().toISOString(), scene: config.plyUrl, message: `Spark: ${message}` };
  try {
    const history = JSON.parse(localStorage.getItem('vrphotoXRHistory') || '[]');
    history.push(event);
    localStorage.setItem('vrphotoXRHistory', JSON.stringify(history.slice(-40)));
    localStorage.setItem('vrphotoLastXR', `${event.time} · ${event.message}`);
  } catch (_) { /* local history is optional */ }
  fetch('/debug/xr-event', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(event), keepalive: true
  }).catch(() => {});
}
sendLogButton?.addEventListener('click', async () => {
  sendLogButton.disabled = true;
  try {
    const events = JSON.parse(localStorage.getItem('vrphotoXRHistory') || '[]');
    if (!Array.isArray(events) || !events.length) throw new Error('No XR events saved in this browser');
    const response = await fetch('/debug/xr-upload', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ events })
    });
    if (!response.ok) throw new Error(`Server returned ${response.status}`);
    const result = await response.json();
    sendLogButton.textContent = `Sent ${result.saved} events`;
    sendLogStatus.textContent = 'Saved on server';
  } catch (error) {
    sendLogButton.textContent = 'Retry XR log';
    sendLogStatus.textContent = error.message;
  } finally { sendLogButton.disabled = false; }
});
if (debug) {
  window.addEventListener('error', event => recordXR(`JavaScript error: ${event.message}`));
  window.addEventListener('unhandledrejection', event => recordXR(`Promise error: ${event.reason}`));
  canvas.addEventListener('webglcontextlost', () => recordXR('WebGL context lost'));
}

budgetSelect.value = Object.keys(presets).find(name => presets[name] === fraction) ?? 'full';
budgetSelect.disabled = xrTest;
budgetSelect.addEventListener('change', () => {
  try { localStorage.setItem('vrphotoBudget', budgetSelect.value); } catch (_) { /* URL still works */ }
  const url = new URL(location.href);
  url.searchParams.set('budget', budgetSelect.value);
  location.href = url.href;
});

try {
  const supported = Boolean(await navigator.xr?.isSessionSupported('immersive-vr'));
  vrButton.disabled = !supported || !xrTest;

  const renderer = new THREE.WebGLRenderer({ canvas, antialias: false, alpha: true, powerPreference: 'high-performance' });
  renderer.xr.enabled = true;
  renderer.xr.setReferenceSpaceType('local-floor');
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.5));
  renderer.setSize(window.innerWidth, window.innerHeight);
  renderer.setClearColor(0x10131a, 1);
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(70, window.innerWidth / window.innerHeight, 0.01, 1000);
  camera.rotation.order = 'YXZ';
  const content = new THREE.Group();
  scene.add(content);

  // Head-following panel that can be selected with a controller target ray.
  const menuCanvas = document.createElement('canvas');
  menuCanvas.width = 768;
  menuCanvas.height = 256;
  const menuPaint = menuCanvas.getContext('2d');
  menuPaint.fillStyle = '#233b60';
  menuPaint.fillRect(0, 0, 768, 256);
  menuPaint.strokeStyle = '#9cc9ff';
  menuPaint.lineWidth = 10;
  menuPaint.strokeRect(5, 5, 758, 246);
  menuPaint.fillStyle = '#ffffff';
  menuPaint.font = 'bold 70px system-ui, sans-serif';
  menuPaint.textAlign = 'center';
  menuPaint.fillText('← Library', 384, 118);
  menuPaint.font = '36px system-ui, sans-serif';
  menuPaint.fillText('Point and press trigger', 384, 192);
  const menuTexture = new THREE.CanvasTexture(menuCanvas);
  menuTexture.colorSpace = THREE.SRGBColorSpace;
  const menu = new THREE.Mesh(new THREE.PlaneGeometry(0.68, 0.226),
    new THREE.MeshBasicMaterial({ map: menuTexture, side: THREE.DoubleSide,
      transparent: true, depthTest: false, depthWrite: false }));
  menu.renderOrder = 10000;
  menu.visible = false;
  scene.add(menu);
  const cursor = new THREE.Mesh(new THREE.CircleGeometry(0.012, 20),
    new THREE.MeshBasicMaterial({ color: 0xffffff, depthTest: false, depthWrite: false }));
  cursor.renderOrder = 10001;
  cursor.visible = false;
  scene.add(cursor);
  const pointerRay = new THREE.Raycaster();
  const rayOrigin = new THREE.Vector3();
  const rayRotation = new THREE.Quaternion();
  const rayDirection = new THREE.Vector3();
  const menuNormal = new THREE.Vector3();
  const menuOffset = new THREE.Vector3(0, -0.20, -0.78);
  function backHit(inputSource, frame) {
    const referenceSpace = renderer.xr.getReferenceSpace();
    if (!frame || !referenceSpace || !inputSource.targetRaySpace) return null;
    const pose = frame.getPose(inputSource.targetRaySpace, referenceSpace);
    if (!pose) return null;
    const { position, orientation } = pose.transform;
    rayOrigin.set(position.x, position.y, position.z);
    rayRotation.set(orientation.x, orientation.y, orientation.z, orientation.w);
    rayDirection.set(0, 0, -1).applyQuaternion(rayRotation);
    pointerRay.set(rayOrigin, rayDirection);
    return pointerRay.intersectObject(menu, false)[0] ?? null;
  }
  function updateMenu(head, frame) {
    if (!menu.visible) { cursor.visible = false; return; }
    menu.position.copy(head.position).add(menuOffset.clone().applyQuaternion(head.quaternion));
    menu.quaternion.copy(head.quaternion);
    cursor.visible = false;
    for (const source of session?.inputSources ?? []) {
      const hit = backHit(source, frame);
      if (!hit) continue;
      menuNormal.set(0, 0, 1).applyQuaternion(menu.quaternion);
      cursor.position.copy(hit.point).addScaledVector(menuNormal, 0.003);
      cursor.quaternion.copy(menu.quaternion);
      cursor.visible = true;
      break;
    }
  }
  function onSelectStart(event) {
    if (xrTest) return;
    if (!menu.visible) {
      menu.visible = true;
      recordXR('VR menu opened');
    } else if (backHit(event.inputSource, event.frame)) {
      recordXR('VR menu: return to library');
      const activeSession = session;
      activeSession?.end().catch(() => {}).finally(() => location.assign(config.returnUrl || '/'));
    } else {
      menu.visible = false;
      cursor.visible = false;
      recordXR('VR menu closed');
    }
  }
  const gripPosition = new THREE.Vector3();
  const gripAtGrab = new THREE.Vector3();
  const contentAtGrab = new THREE.Vector3();
  const headForward = new THREE.Vector3();
  let grabbedSource = null;
  let gripAnchored = false;
  let grabFromButtonPoll = false;
  let grabDepth = 0;
  function readGrip(source, frame) {
    const referenceSpace = renderer.xr.getReferenceSpace();
    if (!frame || !referenceSpace || !source?.gripSpace) return false;
    const pose = frame.getPose(source.gripSpace, referenceSpace);
    if (!pose) return false;
    const position = pose.transform.position;
    gripPosition.set(position.x, position.y, position.z);
    return true;
  }
  function beginGrab(source, frame, fromButtonPoll = false) {
    if (grabbedSource || !source.gripSpace) return;
    grabbedSource = source;
    gripAnchored = false;
    grabFromButtonPoll = fromButtonPoll;
    grabDepth = 0;
    anchorGrab(frame);
    recordXR(`Grip started · ${source.handedness || 'unknown'} controller`);
  }
  function anchorGrab(frame) {
    if (!grabbedSource || !readGrip(grabbedSource, frame)) return;
    contentAtGrab.copy(content.position);
    gripAtGrab.copy(gripPosition);
    gripAnchored = true;
  }
  function endGrab(source) {
    if (source !== grabbedSource) return;
    grabbedSource = null;
    gripAnchored = false;
    grabFromButtonPoll = false;
    grabDepth = 0;
    recordXR('Grip ended');
  }
  function updateGrab(frame, deltaSeconds, head) {
    // The xr-standard squeeze button is a fallback for browsers that omit squeeze events.
    if (!grabbedSource) {
      const pressedSource = Array.from(session?.inputSources ?? []).find(source =>
        source.gamepad?.mapping === 'xr-standard' && source.gamepad.buttons[1]?.pressed);
      if (pressedSource) beginGrab(pressedSource, frame, true);
    }
    if (!grabbedSource) return;
    if (!Array.from(session?.inputSources ?? []).includes(grabbedSource)) {
      endGrab(grabbedSource);
      return;
    }
    if (grabFromButtonPoll && !grabbedSource.gamepad?.buttons[1]?.pressed) {
      endGrab(grabbedSource);
      return;
    }
    if (!gripAnchored) { anchorGrab(frame); return; }
    if (!readGrip(grabbedSource, frame)) return;
    // Follow the controller's position only. Wrist rotation must not tilt the photo.
    content.position.copy(contentAtGrab).add(gripPosition).sub(gripAtGrab);
    const axes = grabbedSource.gamepad?.axes;
    const stickY = axes?.length >= 4 ? axes[3] : axes?.length === 2 ? axes[1] : 0;
    if (Math.abs(stickY) > 0.2) {
      const amount = (Math.abs(stickY) - 0.2) / 0.8;
      grabDepth = Math.max(-4, Math.min(4,
        grabDepth - Math.sign(stickY) * amount * 0.8 * deltaSeconds));
    }
    headForward.set(0, 0, -1).applyQuaternion(head.quaternion);
    // Moving splats toward the viewer changes the viewpoint; scaling about the
    // original photo camera would leave the image almost unchanged.
    content.position.addScaledVector(headForward, -grabDepth);
  }

  let mesh;
  if (xrTest) {
    const cube = new THREE.Mesh(new THREE.BoxGeometry(0.25, 0.25, 0.25),
      new THREE.MeshBasicMaterial({ color: 0xff2020 }));
    cube.position.set(0, 0, -1.2);
    content.add(cube);
  } else {
    // Splat edge smoothing runs in Spark's shader; WebGL MSAA is costly for splats.
    const spark = new SparkRenderer({ renderer, maxStdDev: Math.sqrt(5), blurAmount: 0.3 });
    scene.add(spark);
    const assetUrl = new URL(config.plyUrl, location.href);
    assetUrl.searchParams.set('budget', String(fraction));
    mesh = new SplatMesh({
      url: assetUrl.pathname + assetUrl.search,
      onProgress: event => {
        if (event.lengthComputable && event.total) {
          setStatus(`Loading PLY · ${Math.round(100 * event.loaded / event.total)}%`);
        }
      }
    });
    // SHARP uses OpenCV coordinates. Other PLYs keep their own orientation.
    if (config.coordinateSystem === 'opencv-x-right-y-down-z-forward') {
      mesh.rotation.x = Math.PI;
    }
    content.add(mesh);
  }

  function reset() {
    content.position.set(0, 0, 0);
    content.quaternion.identity();
    camera.position.set(0, 0, 0);
    camera.rotation.set(0, 0, 0);
  }
  window.addEventListener('resize', () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
  });

  let session = null, xrFrames = 0, aligned = false, autoExitTimer = null;
  let lastFrameTime = performance.now(), lastXRTime = null, frames = 0;
  renderer.setAnimationLoop((time, xrFrame) => {
    // Do not draw splats onto the ordinary browser page.
    if (!renderer.xr.isPresenting) return;
    const head = renderer.xr.getCamera(camera);
    if (!aligned) {
      // Place the original photo viewpoint at the first tracked head pose.
      content.position.copy(head.position);
      content.quaternion.copy(head.quaternion);
      aligned = true;
      recordXR('First tracked pose aligned');
    }
    const deltaSeconds = lastXRTime === null ? 0 : Math.max(0, Math.min(0.05, (time - lastXRTime) / 1000));
    lastXRTime = time;
    updateGrab(xrFrame, deltaSeconds, head);
    updateMenu(head, xrFrame);
    renderer.render(scene, camera);
    frames++;
    xrFrames++;
    if (debug && (xrFrames === 1 || xrFrames === 30)) {
      const layer = session?.renderState.baseLayer;
      recordXR(`XR render ${xrFrames}; layer ${layer?.framebufferWidth || '?'}x${layer?.framebufferHeight || '?'}; GL error ${renderer.getContext().getError()}`);
    }
    if (time - lastFrameTime > 1000) {
      fps.textContent = `${Math.round(frames * 1000 / (time - lastFrameTime))} FPS · Spark / WebGL2`;
      frames = 0;
      lastFrameTime = time;
    }
  });

  vrButton.addEventListener('click', async () => {
    vrButton.disabled = true;
    reset();
    aligned = false;
    xrFrames = 0;
    frames = 0;
    lastFrameTime = performance.now();
    lastXRTime = null;
    setStatus('Entering VR…');
    recordXR(`Enter VR requested · budget ${fraction}${xrTest ? ' · cube test' : ''}`);
    try {
      session = await navigator.xr.requestSession('immersive-vr', { requiredFeatures: ['local-floor'] });
      session.addEventListener('selectstart', onSelectStart);
      session.addEventListener('squeezestart', event => beginGrab(event.inputSource, event.frame));
      session.addEventListener('squeezeend', event => endGrab(event.inputSource));
      session.addEventListener('end', () => {
        clearTimeout(autoExitTimer);
        recordXR(`VR ended after ${xrFrames} rendered frames`);
        session = null;
        aligned = false;
        reset();
        menu.visible = false;
        cursor.visible = false;
        grabbedSource = null;
        gripAnchored = false;
        grabFromButtonPoll = false;
        grabDepth = 0;
        lastXRTime = null;
        preview.hidden = false;
        fps.textContent = 'Static preview';
        vrButton.disabled = !supported;
        setStatus('VR ended');
      }, { once: true });
      await renderer.xr.setSession(session);
      preview.hidden = renderer.xr.isPresenting;
      recordXR('VR session started through Three.js');
      setStatus('VR active');
      if (xrAutoExitSeconds) autoExitTimer = setTimeout(() => {
        if (session) {
          recordXR(`Timeout: ending VR after ${xrAutoExitSeconds} seconds`);
          session.end().catch(error => recordXR(`VR end failed: ${error.message || error}`));
        }
      }, xrAutoExitSeconds * 1000);
    } catch (error) {
      recordXR(`VR start failed: ${error.message || error}`);
      setStatus(`VR start failed: ${error.message || error}`);
      if (session) session.end().catch(() => {});
      vrButton.disabled = !supported;
    }
  });

  if (mesh) {
    setStatus(`Loading PLY · Spark / WebGL2 · ${Math.round(fraction * 100)}% splats`);
    await mesh.initialized;
    if (config.coordinateSystem !== 'opencv-x-right-y-down-z-forward') {
      // Generic PLYs have no saved photo camera. Put their bounds in front of
      // the initial head pose so the user can see the model and then grab it.
      const bounds = mesh.getBoundingBox(true);
      if (!bounds.isEmpty()) {
        const center = bounds.getCenter(new THREE.Vector3());
        const size = bounds.getSize(new THREE.Vector3());
        const longest = Math.max(size.x, size.y, size.z);
        if (Number.isFinite(longest) && longest > 0) {
          const scale = Math.min(1, 1.8 / longest);
          mesh.scale.setScalar(scale);
          mesh.position.copy(center).multiplyScalar(-scale);
          mesh.position.z -= 2.4;
        }
      }
    }
    vrButton.disabled = !supported;
    setStatus(`Static preview ready · ${Math.round(fraction * 100)}% splats loaded for VR`);
  } else {
    setStatus('Three.js XR cube ready · no PLY loaded · VR auto-exits after 10 s');
  }
} catch (error) {
  console.error(error);
  recordXR(`Viewer error: ${error.message || error}`);
  setStatus(`Viewer error: ${error.message || error}`);
}
