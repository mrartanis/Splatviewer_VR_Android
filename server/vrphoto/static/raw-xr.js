const canvas = document.getElementById('raw-canvas');
const button = document.getElementById('enter-raw-vr');
const status = document.getElementById('raw-status');
const gl = canvas.getContext('webgl2', { xrCompatible: true, alpha: true, antialias: false });

function log(message) {
  status.textContent = message;
  fetch('/debug/xr-event', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ time: new Date().toISOString(), scene: 'raw-xr-test', message }),
    keepalive: true
  }).catch(() => {});
}

if (!gl || !navigator.xr) {
  log('WebGL2 or WebXR is unavailable in this browser');
} else {
  try {
    await gl.makeXRCompatible();
    status.textContent = 'Ready: enter VR to see a solid red screen for 10 seconds';
  } catch (error) {
    log(`WebGL XR compatibility probe failed; session can still be tried: ${error.message || error}`);
  }
  button.disabled = false;
}

button.addEventListener('click', async () => {
  button.disabled = true;
  let session;
  let frames = 0;
  let timer;
  try {
    log('Raw XR session requested');
    session = await navigator.xr.requestSession('immersive-vr', { requiredFeatures: ['local-floor'] });
    session.addEventListener('end', () => {
      clearTimeout(timer);
      log(`Raw XR ended after ${frames} frames`);
      button.disabled = false;
    });
    const layer = new XRWebGLLayer(session, gl, { alpha: true, antialias: false });
    session.updateRenderState({ baseLayer: layer });
    const space = await session.requestReferenceSpace('local-floor');
    log(`Raw XR started; layer ${layer.framebufferWidth}x${layer.framebufferHeight}`);
    timer = setTimeout(() => {
      log('Raw XR timeout: ending after 10 seconds');
      session.end().catch(error => log(`Raw XR end failed: ${error.message || error}`));
    }, 10000);
    function draw(_time, frame) {
      if (!session) return;
      const pose = frame.getViewerPose(space);
      gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
      gl.disable(gl.SCISSOR_TEST);
      gl.viewport(0, 0, layer.framebufferWidth, layer.framebufferHeight);
      gl.clearColor(1, 0, 0, 1);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      frames++;
      if (frames === 1 || frames === 30) {
        log(`Raw XR frame ${frames}; views ${pose?.views.length ?? 0}; GL error ${gl.getError()}`);
      }
      session.requestAnimationFrame(draw);
    }
    session.requestAnimationFrame(draw);
  } catch (error) {
    log(`Raw XR failed: ${error.message || error}`);
    if (session) session.end().catch(() => {});
    button.disabled = false;
  }
});
