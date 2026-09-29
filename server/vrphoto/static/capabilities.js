import * as pc from './playcanvas.min.mjs';

export async function detect() {
  const result = {
    'User-Agent': navigator.userAgent,
    'Secure context': window.isSecureContext,
    'WebGPU available': Boolean(navigator.gpu),
    'WebGL2 available': Boolean(document.createElement('canvas').getContext('webgl2')),
    'Immersive VR available': false,
    'WebGPU + WebXR support': false,
    'WebGL2 + WebXR support': false,
    'Chosen renderer (auto VR)': 'none',
    'Last XR event': 'none',
    'PlayCanvas version': '2.22.6'
  };
  try {
    result['Immersive VR available'] = Boolean(await navigator.xr?.isSessionSupported('immersive-vr'));
  } catch (error) { result['Immersive VR available'] = String(error); }
  if (result['Immersive VR available'] === true) {
    try { result['WebGPU + WebXR support'] = await pc.XrManager.isDeviceSupported(pc.DEVICETYPE_WEBGPU, pc.XRTYPE_VR); }
    catch (error) { result['WebGPU + WebXR support'] = String(error); }
    try { result['WebGL2 + WebXR support'] = await pc.XrManager.isDeviceSupported(pc.DEVICETYPE_WEBGL2, pc.XRTYPE_VR); }
    catch (error) { result['WebGL2 + WebXR support'] = String(error); }
  }
  result['Chosen renderer (auto VR)'] = result['WebGL2 available'] ? 'WebGL2' : 'none';
  try { result['Last XR event'] = localStorage.getItem('vrphotoLastXR') || 'none'; } catch (_) { /* optional diagnostics */ }
  return result;
}

const list = document.getElementById('capabilities');
if (list) {
  detect().then(result => {
    list.replaceChildren();
    for (const [label, value] of Object.entries(result)) {
      const key = document.createElement('dt'); key.textContent = label;
      const answer = document.createElement('dd'); answer.textContent = String(value);
      list.append(key, answer);
    }
  }).catch(error => { list.textContent = String(error); });
}

const sendLogButton = document.getElementById('send-xr-log');
if (sendLogButton) {
  sendLogButton.addEventListener('click', async () => {
    const label = document.getElementById('send-log-status');
    sendLogButton.disabled = true;
    try {
      const events = JSON.parse(localStorage.getItem('vrphotoXRHistory') || '[]');
      if (!Array.isArray(events) || events.length === 0) throw new Error('No XR events saved in this browser');
      const response = await fetch('/debug/xr-upload', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ events })
      });
      if (!response.ok) throw new Error(`Server returned ${response.status}`);
      const result = await response.json();
      sendLogButton.textContent = `Sent ${result.saved} events`;
      if (label) label.textContent = 'Saved on server';
    } catch (error) {
      sendLogButton.textContent = 'Retry XR log';
      if (label) label.textContent = error.message;
    } finally {
      sendLogButton.disabled = false;
    }
  });
}
