from __future__ import annotations

import asyncio
from collections import deque
from datetime import datetime, timezone
import hashlib
import html
import json
import logging
import os
from pathlib import Path
import signal
import socket
from urllib.parse import quote, urlsplit

# asyncio's SSL sendfile fallback can fail while restoring a transport after a
# headset aborts a large PLY transfer on Windows. aiohttp's chunked path keeps
# FileResponse's Range/HEAD handling without entering that asyncio path.
if os.name == "nt":
    os.environ.setdefault("AIOHTTP_NOSENDFILE", "1")

from aiohttp import web
from cryptography import x509
from cryptography.hazmat.primitives import serialization

from . import __version__
from .library import contents, scene_valid, within
from .ply import reduced_ply
from .thumbnails import thumbnail
from .tls import context, ensure_certificates

LOG = logging.getLogger(__name__)
STATIC = Path(__file__).parent / "static"
PLAYCANVAS_VERSION = "2.22.6"
SPARK_VERSION = "2.2.0"
THREE_VERSION = "0.180.0"
FRONTEND_REVISION = "2026-09-29-19"


def _url(path: Path, root: Path, kind: str) -> str:
    relative = path.relative_to(root).parts
    return f"/{kind}/" + "/".join(quote(part, safe="") for part in relative)


def _page(title: str, body: str, script: str = "") -> web.Response:
    markup = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><link rel="icon" href="data:,"><title>{html.escape(title)}</title><link rel="stylesheet" href="/static/style.css"></head><body>{body}{script}</body></html>'''
    return web.Response(text=markup, content_type="text/html", headers={"Cache-Control": "no-store"})


def _get(root: Path, raw: str) -> Path:
    try:
        return within(root, raw)
    except ValueError:
        raise web.HTTPNotFound()


def _metadata(path: Path) -> dict:
    metadata_path = path / "metadata.json"
    if metadata_path.is_symlink():
        return {}
    try:
        data = json.loads(metadata_path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _relative(path: Path, root: Path) -> str:
    return "/".join(path.relative_to(root).parts)


def _catalog_return_url(raw: str | None) -> str | None:
    if not raw or "\\" in raw or any(ord(char) < 32 for char in raw):
        return None
    parts = urlsplit(raw)
    if parts.scheme or parts.netloc or parts.fragment:
        return None
    if parts.path != "/" and not parts.path.startswith("/browse/"):
        return None
    return raw


def create_app(library_name: str, budget: str = "full", renderer: str = "auto",
               cache_dir: str | None = None, debug: bool = False) -> web.Application:
    root = Path(library_name).expanduser().resolve()
    if not root.is_dir():
        raise FileNotFoundError(root)
    app = web.Application(client_max_size=1024 * 1024)
    app["root"] = root
    app["debug"] = debug
    xr_events: deque[dict[str, str]] = deque(maxlen=40)
    budget_cache = Path(cache_dir) if cache_dir else root.parent / ".vrphoto-cache"
    thumbnail_cache = budget_cache.parent / "thumb-cache"
    xr_log = budget_cache.parent / "xr-events.jsonl"

    def save_xr_event(scene: str, message: str, source: str, client_time: str = "") -> None:
        event = {
            "time": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "client_time": client_time[:40],
            "scene": scene[:180],
            "message": message[:300],
            "source": source,
        }
        xr_events.append(event)
        xr_log.parent.mkdir(parents=True, exist_ok=True)
        with xr_log.open("a", encoding="utf-8") as log_file:
            log_file.write(json.dumps(event, ensure_ascii=False) + "\n")
        LOG.info("XR %s %s: %s", source, event["scene"], event["message"])

    async def catalog(request: web.Request) -> web.Response:
        folder = _get(root, request.match_info.get("path", ""))
        if not folder.is_dir() or scene_valid(folder):
            raise web.HTTPNotFound()
        directories, scenes = contents(root, folder)
        try:
            page = int(request.query.get("page", "1"))
            if page < 1:
                raise ValueError()
        except ValueError:
            raise web.HTTPBadRequest(text="Invalid page")
        page_size = 48
        scene_count = len(scenes)
        scenes = scenes[(page - 1) * page_size:page * page_size]
        crumbs = ['<a href="/">Library</a>']
        cursor = root
        for part in folder.relative_to(root).parts:
            cursor /= part
            crumbs.append(f'<a href="{_url(cursor, root, "browse")}">{html.escape(part)}</a>')
        cards = ['<section><h2>Folders</h2><div class="grid">']
        cards += [f'<a class="card folder" href="{_url(p, root, "browse")}"><span class="folder-icon">▣</span><strong>{html.escape(p.name)}</strong></a>' for p in directories]
        cards.append('</div></section><section><h2>Scenes</h2><div class="grid">')
        source = quote(str(request.rel_url), safe="")
        for p in scenes:
            cards.append(f'<a class="card" href="{_url(p, root, "scene")}?from={source}"><img loading="lazy" decoding="async" fetchpriority="low" width="320" height="213" src="{_url(p, root, "media")}/preview.jpg?thumb=1" alt=""><strong>{html.escape(p.name)}</strong></a>')
        cards.append('</div></section>')
        base = "/" if folder == root else _url(folder, root, "browse")
        pages = (scene_count + page_size - 1) // page_size
        if pages > 1:
            cards.append('<nav class="pages">')
            if page > 1:
                cards.append(f'<a href="{base}?page={page - 1}">← Previous</a>')
            cards.append(f'<span>Page {page} of {pages}</span>')
            if page < pages:
                cards.append(f'<a href="{base}?page={page + 1}">Next →</a>')
            cards.append('</nav>')
        debug_links = ('<span><a href="/debug/catalog-lite">Text view</a> · '
                       '<a href="/debug/capabilities">Capabilities</a> · '
                       '<a href="/debug/plain">Plain test</a></span>') if debug else ''
        return _page("VRPhoto Library", f'<header><h1>VRPhoto Library</h1>{debug_links}</header><main><nav class="crumbs">{" / ".join(crumbs)}</nav>{"".join(cards)}</main>')

    async def library_api(request: web.Request) -> web.Response:
        folder = _get(root, request.query.get("path", ""))
        if not folder.is_dir() or scene_valid(folder):
            raise web.HTTPNotFound()
        try:
            page = int(request.query.get("page", "1"))
            if page < 1:
                raise ValueError()
        except ValueError:
            raise web.HTTPBadRequest(text="Invalid page")
        directories, scenes = contents(root, folder)
        page_size = 48
        pages = max(1, (len(scenes) + page_size - 1) // page_size)
        scenes = scenes[(page - 1) * page_size:page * page_size]
        items = []
        for path in scenes:
            base = _url(path, root, "media")
            ply_stat = (path / "scene.ply").stat()
            items.append({
                "name": path.name,
                "path": _relative(path, root),
                "preview_url": base + "/preview.jpg?thumb=1",
                "ply_url": base + "/scene.ply",
                "revision": f"{ply_stat.st_size}-{ply_stat.st_mtime_ns}",
                "coordinate_system": _metadata(path).get("coordinate_system"),
            })
        return web.json_response({
            "path": _relative(folder, root),
            "parent": None if folder == root else _relative(folder.parent, root),
            "folders": [{"name": path.name, "path": _relative(path, root)} for path in directories],
            "scenes": items,
            "page": page,
            "pages": pages,
        }, headers={"Cache-Control": "no-store"})

    async def scene(request: web.Request) -> web.Response:
        path = _get(root, request.match_info["path"])
        if not scene_valid(path):
            raise web.HTTPNotFound()
        parent_url = (_catalog_return_url(request.query.get("from")) or
                      ("/" if path.parent == root else _url(path.parent, root, "browse")))
        metadata = _metadata(path)
        config = {
            "plyUrl": _url(path, root, "media") + "/scene.ply",
            "returnUrl": parent_url,
            "budget": request.query.get("budget", budget),
            "renderer": request.query.get("renderer", renderer),
            "version": PLAYCANVAS_VERSION,
            "sourceWidth": metadata.get("source_width"),
            "sourceHeight": metadata.get("source_height"),
            "coordinateSystem": metadata.get("coordinate_system"),
        }
        config_json = json.dumps(config).replace("<", "\\u003c")
        use_playcanvas = debug and request.query.get("engine") == "playcanvas"
        viewer_script = "viewer.js" if use_playcanvas else "spark-viewer.js"
        import_map = ('' if use_playcanvas else
                      '<script type="importmap">{"imports":{"three":"/static/three.module.js",'
                      '"three/addons/postprocessing/Pass.js":"/static/three-pass.js"}}</script>')
        script = (f'<script id="viewer-config" type="application/json">{config_json}</script>'
                  f'{import_map}<script type="module" src="/static/{viewer_script}?v={FRONTEND_REVISION}"></script>')
        preview = ("" if use_playcanvas else
                   f'<img id="preview" src="{_url(path, root, "media")}/preview.jpg" '
                   'alt="Static photo preview" fetchpriority="high">')
        debug_buttons = ('<button id="send-xr-log">Send XR log</button><span id="send-log-status"></span>'
                         '<a href="/debug/xr-raw">Raw XR test</a>'
                         '<a href="/debug/capabilities">Capabilities</a>') if debug else ''
        reset_button = '<button id="reset">Reset camera</button>' if use_playcanvas else ''
        body = f'''<div id="viewer"><canvas id="canvas"></canvas>{preview}<div class="toolbar"><a href="{html.escape(parent_url, quote=True)}">← Library</a><strong>{html.escape(path.name)}</strong><button id="enter-vr" disabled>Enter VR</button>{reset_button}{debug_buttons}<label>Budget <select id="budget"><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="full">Full</option></select></label><span id="fps"></span></div><div id="status">Loading…</div></div>'''
        return _page(path.name, body, script)

    async def runtime(request: web.Request) -> web.Response:
        return web.json_response({"debug": debug}, headers={"Cache-Control": "no-store"})

    async def media(request: web.Request) -> web.StreamResponse:
        path = _get(root, request.match_info["path"])
        if (path.name not in ("scene.ply", "preview.jpg", "metadata.json") or
                not scene_valid(path.parent) or not path.is_file() or path.is_symlink()):
            raise web.HTTPNotFound()
        content_type = {"scene.ply": "application/octet-stream", "preview.jpg": "image/jpeg", "metadata.json": "application/json"}[path.name]
        if path.name == "scene.ply" and "budget" in request.query:
            try:
                fraction = float(request.query["budget"])
                if not 0 < fraction <= 1:
                    raise ValueError()
                path = await asyncio.to_thread(reduced_ply, path, fraction, budget_cache)
            except ValueError:
                raise web.HTTPBadRequest(text="Invalid splat budget or unsupported PLY")
        if path.name == "preview.jpg" and request.query.get("thumb") == "1":
            # Two bounded sizes: compact web cards and high-density headset UI.
            size = request.query.get("size", "320")
            if size not in ("320", "768"):
                raise web.HTTPBadRequest(text="Unsupported thumbnail size")
            try:
                path = await asyncio.to_thread(thumbnail, path, thumbnail_cache, int(size))
            except OSError:
                LOG.warning("Thumbnail generation failed for %s; serving preview", path, exc_info=True)
        response = web.FileResponse(path, headers={"Cache-Control": "public, max-age=3600", "X-Content-Type-Options": "nosniff"})
        response.content_type = content_type
        return response

    async def static(request: web.Request) -> web.StreamResponse:
        name = request.match_info["name"]
        if not debug and name in ("viewer.js", "capabilities.js", "raw-xr.js", "playcanvas.min.mjs"):
            raise web.HTTPNotFound()
        if name not in ("viewer.js", "spark-viewer.js", "capabilities.js", "raw-xr.js", "style.css",
                        "playcanvas.min.mjs", "spark.module.js", "three.module.js", "three.core.js", "three-pass.js"):
            raise web.HTTPNotFound()
        path = STATIC / name
        if not path.is_file():
            raise web.HTTPNotFound()
        response = web.FileResponse(path, headers={"Cache-Control": "no-store"})
        if name.endswith(".mjs") or name.endswith(".js"):
            response.content_type = "text/javascript"
        return response

    async def capabilities(request: web.Request) -> web.Response:
        body = '<header><h1>Device capabilities</h1><a href="/">Library</a></header><main><p>Run this page in PICO Browser to see actual XR support. <a href="/debug/xr-events">Server XR log</a></p><p><button id="send-xr-log">Send XR log</button> <span id="send-log-status"></span></p><dl id="capabilities">Checking…</dl></main>'
        return _page("VRPhoto capabilities", body, f'<script type="module" src="/static/capabilities.js?v={FRONTEND_REVISION}"></script>')

    async def xr_event(request: web.Request) -> web.Response:
        try:
            payload = await request.json()
        except (ValueError, UnicodeDecodeError):
            raise web.HTTPBadRequest(text="Invalid XR event")
        if not isinstance(payload, dict) or not isinstance(payload.get("message"), str):
            raise web.HTTPBadRequest(text="Invalid XR event")
        save_xr_event(str(payload.get("scene", "")), payload["message"], "live",
                      str(payload.get("time", "")))
        return web.Response(status=204, headers={"Cache-Control": "no-store"})

    async def xr_upload(request: web.Request) -> web.Response:
        try:
            payload = await request.json()
        except (ValueError, UnicodeDecodeError):
            raise web.HTTPBadRequest(text="Invalid XR log")
        entries = payload.get("events") if isinstance(payload, dict) else None
        if not isinstance(entries, list) or len(entries) > 100:
            raise web.HTTPBadRequest(text="Invalid XR log")
        for entry in entries:
            if not isinstance(entry, dict) or not isinstance(entry.get("message"), str):
                raise web.HTTPBadRequest(text="Invalid XR log")
        for entry in entries:
            save_xr_event(str(entry.get("scene", "")), entry["message"], "upload",
                          str(entry.get("time", "")))
        return web.json_response({"saved": len(entries)}, headers={"Cache-Control": "no-store"})

    async def xr_events_page(request: web.Request) -> web.Response:
        return web.json_response({"events": list(xr_events)}, headers={"Cache-Control": "no-store"})

    async def xr_raw(request: web.Request) -> web.Response:
        body = ('<header><h1>Raw WebXR test</h1><a href="/">Library</a></header>'
                '<main><p>This test uses WebGL2 and WebXR directly, without PlayCanvas or a PLY. '
                'VR should show a solid red screen and exit automatically after 10 seconds.</p>'
                '<button id="enter-raw-vr" disabled>Enter 10-second red VR test</button>'
                '<p id="raw-status">Preparing WebGL2…</p><canvas id="raw-canvas" width="16" height="16"></canvas></main>')
        return _page("Raw WebXR test", body,
                     f'<script type="module" src="/static/raw-xr.js?v={FRONTEND_REVISION}"></script>')

    async def plain(request: web.Request) -> web.Response:
        body = ('<!doctype html><html><head><meta charset="utf-8"><title>VRPhoto plain test</title>'
                '</head><body><h1>Plain test</h1><p>This page has no CSS, JavaScript, canvas, or images.</p>'
                '<p><a href="/debug/catalog-lite">Text-only catalog</a> · <a href="/">Normal catalog</a></p>'
                '</body></html>')
        return web.Response(text=body, content_type="text/html", headers={"Cache-Control": "no-store"})

    async def catalog_lite(request: web.Request) -> web.Response:
        directories, scenes = contents(root, root)
        links = [f'<li>Folder: {html.escape(path.name)}</li>' for path in directories]
        links += [f'<li><a href="{_url(path, root, "scene")}">{html.escape(path.name)}</a></li>' for path in scenes]
        body = ('<!doctype html><html><head><meta charset="utf-8"><title>VRPhoto text catalog</title>'
                '</head><body><h1>Text-only catalog</h1><p>No CSS, JavaScript, canvas, or preview images.</p>'
                '<ul>' + ''.join(links) + '</ul><a href="/">Normal catalog</a></body></html>')
        return web.Response(text=body, content_type="text/html", headers={"Cache-Control": "no-store"})

    app.router.add_get("/", catalog)
    app.router.add_get("/browse/{path:.*}", catalog)
    app.router.add_get("/scene/{path:.*}", scene)
    app.router.add_get("/media/{path:.*}", media)
    app.router.add_get("/static/{name}", static)
    app.router.add_get("/api/runtime", runtime)
    app.router.add_get("/api/v1/library", library_api)
    if debug:
        app.router.add_get("/debug/capabilities", capabilities)
        app.router.add_post("/debug/xr-event", xr_event)
        app.router.add_post("/debug/xr-upload", xr_upload)
        app.router.add_get("/debug/xr-events", xr_events_page)
        app.router.add_get("/debug/xr-raw", xr_raw)
        app.router.add_get("/debug/plain", plain)
        app.router.add_get("/debug/catalog-lite", catalog_lite)
    return app


def _local_addresses(host: str) -> list[str]:
    if host not in ("0.0.0.0", "::"):
        return [host]
    addresses = {"127.0.0.1"}
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.connect(("192.0.2.1", 1))
            addresses.add(sock.getsockname()[0])
    except OSError:
        pass
    return sorted(addresses)


async def run_server(library: str, host: str, port: int, state_dir: str, budget: str,
                     renderer: str, redirect_port: int | None = None,
                     certificate_file: str | None = None, private_key_file: str | None = None,
                     ca_certificate_file: str | None = None, debug: bool = False) -> None:
    addresses = _local_addresses(host)
    if bool(certificate_file) != bool(private_key_file):
        raise ValueError("Certificate and private key paths must be supplied together")
    if certificate_file:
        cert, key = Path(certificate_file).expanduser(), Path(private_key_file).expanduser()
        ca = Path(ca_certificate_file).expanduser() if ca_certificate_file else None
    else:
        ca, cert, key = ensure_certificates(Path(state_dir).expanduser(), addresses)
    certificate = x509.load_pem_x509_certificate(cert.read_bytes())
    fingerprint = hashlib.sha256(certificate.public_bytes(serialization.Encoding.DER)).hexdigest().upper()
    app = create_app(library, budget, renderer, str(Path(state_dir).expanduser() / "budget-cache"), debug)
    runner = web.AppRunner(app, access_log=None, keepalive_timeout=15, shutdown_timeout=5)
    await runner.setup()
    loop = asyncio.get_running_loop()
    listener = await loop.create_server(runner.server, host, port, ssl=context(cert, key),
                                        ssl_handshake_timeout=3, ssl_shutdown_timeout=2)
    redirect_runner = None
    try:
        if redirect_port is not None:
            async def redirect(request: web.Request) -> web.Response:
                authority = request.host.split(":")[0]
                raise web.HTTPPermanentRedirect(f"https://{authority}:{port}{request.rel_url}")
            redirect_app = web.Application()
            redirect_app.router.add_route("*", "/{path:.*}", redirect)
            redirect_runner = web.AppRunner(redirect_app, access_log=None, keepalive_timeout=5)
            await redirect_runner.setup()
            await web.TCPSite(redirect_runner, host, redirect_port).start()
        LOG.info("Library: %s", app["root"])
        for address in addresses:
            LOG.info("HTTPS: https://%s:%d/", address, port)
        LOG.info("CA to trust on PICO: %s", ca if ca else "Use the issuing CA of the supplied server certificate")
        LOG.info("Server certificate SHA-256: %s", fingerprint)
        LOG.info("Viewer Spark %s / Three.js %s (PlayCanvas %s fallback); TLS handshake timeout 3s",
                 SPARK_VERSION, THREE_VERSION, PLAYCANVAS_VERSION)
        LOG.info("Debug diagnostics: %s", "enabled" if debug else "disabled")
        stopping = asyncio.Event()
        if hasattr(loop, "add_signal_handler"):
            try:
                loop.add_signal_handler(signal.SIGINT, stopping.set)
                loop.add_signal_handler(signal.SIGTERM, stopping.set)
            except NotImplementedError:
                pass
        await stopping.wait()
    finally:
        LOG.info("Shutting down")
        listener.close()
        await listener.wait_closed()
        if redirect_runner is not None:
            await redirect_runner.cleanup()
        await runner.cleanup()
