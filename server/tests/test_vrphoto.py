from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from io import BytesIO
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from PIL import Image
import pytest

from vrphoto.importer import add_ply, process
from vrphoto.library import scene_valid
from vrphoto.ply import reduced_ply


def make_ply(path: Path, count: int = 20) -> None:
    import struct
    header = (f"ply\nformat binary_little_endian 1.0\nelement vertex {count}\n"
              "property float x\nproperty float y\nproperty float z\n"
              "element image_size 2\nproperty uint image_size\nend_header\n")
    with path.open("wb") as out:
        out.write(header.encode())
        for i in range(count):
            out.write(struct.pack("<fff", float(i), 0, 1))
        out.write(struct.pack("<II", 640, 480))


@pytest.fixture
def library(tmp_path):
    library = tmp_path / "library"
    photo = tmp_path / "étoile.jpg"
    Image.new("RGB", (800, 400), "orange").save(photo)
    ply = tmp_path / "original.ply"
    make_ply(ply)
    add_ply(str(ply), str(photo), str(library))
    return library


@pytest.fixture
def server(library, tmp_path, request):
    state = tmp_path / "state"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    command = [sys.executable, "-m", "vrphoto.cli", "serve", str(library),
               "--host", "127.0.0.1", "--port", str(port), "--state-dir", str(state)]
    if getattr(request, "param", True):
        command.append("--debug")
    proc = subprocess.Popen(command,
                            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    for _ in range(100):
        if (state / "local-ca.crt").exists():
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.1):
                    break
            except OSError:
                pass
        if proc.poll() is not None:
            raise RuntimeError(proc.stderr.read())
        time.sleep(0.05)
    else:
        proc.kill()
        raise RuntimeError("Server did not start")
    ctx = ssl.create_default_context(cafile=str(state / "local-ca.crt"))
    yield f"https://localhost:{port}", port, ctx, proc
    if proc.poll() is None:
        proc.terminate()
        proc.wait(timeout=8)
    proc.stderr.close()


def get(url, ctx, headers=None):
    return urlopen(Request(url, headers=headers or {}), context=ctx, timeout=8)


def test_https_catalog_preview_range_unicode_and_large_stream(server, library):
    base, port, ctx, proc = server
    assert json.loads(get(base + "/api/runtime", ctx).read()) == {"debug": True}
    assert get(base + "/", ctx).status == 200
    preview = get(base + "/media/%C3%A9toile/preview.jpg", ctx)
    assert preview.status == 200 and preview.headers["Content-Type"] == "image/jpeg"
    assert preview.read(2) == b"\xff\xd8"
    thumb = get(base + "/media/%C3%A9toile/preview.jpg?thumb=1", ctx)
    assert max(Image.open(BytesIO(thumb.read())).size) <= 320
    sharp_thumb = get(base + "/media/%C3%A9toile/preview.jpg?thumb=1&size=768", ctx)
    assert Image.open(BytesIO(sharp_thumb.read())).size == (600, 300)  # No enlargement of the 600px source.
    Image.new("RGB", (1200, 600), "orange").save(library / "étoile" / "preview.jpg")
    larger = get(base + "/media/%C3%A9toile/preview.jpg?thumb=1&size=768", ctx)
    assert Image.open(BytesIO(larger.read())).size == (768, 384)
    with pytest.raises(HTTPError) as invalid_size:
        get(base + "/media/%C3%A9toile/preview.jpg?thumb=1&size=99999", ctx)
    assert invalid_size.value.code == 400
    response = get(base + "/media/%C3%A9toile/scene.ply", ctx, {"Range": "bytes=0-31"})
    assert response.status == 206 and len(response.read()) == 32
    reduced = get(base + "/media/%C3%A9toile/scene.ply?budget=0.5", ctx)
    assert reduced.status == 200 and b"element vertex 10\n" in reduced.read(180)
    scene_page = get(base + "/scene/%C3%A9toile", ctx)
    scene_markup = scene_page.read()
    assert scene_page.status == 200 and b"spark-viewer.js?v=" in scene_markup
    assert b"three.module.js" in scene_markup
    assert b'id="preview"' in scene_markup and b'/media/%C3%A9toile/preview.jpg' in scene_markup
    fallback_markup = get(base + "/scene/%C3%A9toile?engine=playcanvas", ctx).read()
    assert b"/static/viewer.js?v=" in fallback_markup and b'id="preview"' not in fallback_markup
    for module in ("spark-viewer.js", "spark.module.js", "three.module.js", "three.core.js", "three-pass.js"):
        request = Request(base + "/static/" + module, method="HEAD")
        with urlopen(request, context=ctx, timeout=8) as asset:
            assert asset.status == 200 and asset.headers["Content-Type"].startswith("text/javascript")
    plain = get(base + "/debug/plain", ctx).read()
    assert b"Plain test" in plain and b"/static/" not in plain and b"<img" not in plain
    lite = get(base + "/debug/catalog-lite", ctx).read()
    assert b"Text-only catalog" in lite and b"<img" not in lite
    assert get(base + "/static/viewer.js", ctx).headers["Cache-Control"] == "no-store"
    assert get(base + "/debug/capabilities", ctx).status == 200
    event = Request(base + "/debug/xr-event", data=b'{"scene":"test","message":"XR started"}',
                    headers={"Content-Type": "application/json"}, method="POST")
    assert urlopen(event, context=ctx).status == 204
    assert b"XR started" in get(base + "/debug/xr-events", ctx).read()
    upload = Request(base + "/debug/xr-upload", data=b'{"events":[{"scene":"test","message":"manual resend"}]}',
                     headers={"Content-Type": "application/json"}, method="POST")
    assert json.loads(urlopen(upload, context=ctx).read())["saved"] == 1
    assert b"manual resend" in (library.parent / "state/xr-events.jsonl").read_bytes()
    raw = get(base + "/debug/xr-raw", ctx).read()
    assert b"raw-xr.js?v=" in raw and b"playcanvas" not in raw
    large = library / "étoile" / "scene.ply"
    with large.open("ab") as out:
        out.write(b"x" * (16 * 1024 * 1024))
    response = get(base + "/media/%C3%A9toile/scene.ply", ctx, {"Range": "bytes=16000000-16000031"})
    assert response.status == 206 and response.read() == b"x" * 32
    # aiohttp FileResponse is the handler, so PLY bytes are never assembled in a Python response body.


def test_catalog_limits_preview_cards(server, library):
    base, port, ctx, proc = server
    for index in range(49):
        shutil.copytree(library / "étoile", library / f"copy-{index:02d}")
    first = get(base + "/", ctx).read()
    second = get(base + "/?page=2", ctx).read()
    assert first.count(b"<img") == 48 and b"Next" in first and b"<script" not in first
    assert second.count(b"<img") == 2
    assert b"?from=%2F%3Fpage%3D2" in second
    scene = get(base + "/scene/copy-48?from=%2F%3Fpage%3D2", ctx).read()
    assert b'href="/?page=2"' in scene and b'"returnUrl": "/?page=2"' in scene
    unsafe = get(base + "/scene/copy-48?from=https%3A%2F%2Fexample.com", ctx).read()
    assert b'href="/"' in unsafe and b'"returnUrl": "/"' in unsafe


def test_native_library_api(server, library):
    base, _, ctx, _ = server
    nested = library / "Семья"
    nested.mkdir()
    shutil.copytree(library / "étoile", nested / "праздник")
    root = json.loads(get(base + "/api/v1/library", ctx).read())
    assert root["path"] == "" and root["parent"] is None
    assert {item["name"] for item in root["folders"]} == {"Семья"}
    assert root["scenes"][0]["path"] == "étoile"
    assert root["scenes"][0]["revision"]
    assert root["scenes"][0]["ply_url"] == "/media/%C3%A9toile/scene.ply"
    assert root["scenes"][0]["coordinate_system"] == "opencv-x-right-y-down-z-forward"
    folder = json.loads(get(base + "/api/v1/library?path=%D0%A1%D0%B5%D0%BC%D1%8C%D1%8F", ctx).read())
    assert folder["parent"] == "" and folder["scenes"][0]["name"] == "праздник"
    assert folder["scenes"][0]["preview_url"].endswith("/preview.jpg?thumb=1")
    for suffix, status in (("?path=..%2F", 404), ("?path=%D0%A1%D0%B5%D0%BC%D1%8C%D1%8F%2F%D0%BF%D1%80%D0%B0%D0%B7%D0%B4%D0%BD%D0%B8%D0%BA", 404), ("?page=0", 400)):
        with pytest.raises(HTTPError) as error:
            get(base + "/api/v1/library" + suffix, ctx)
        assert error.value.code == status


def test_native_library_api_pagination_and_budget(server, library):
    base, _, ctx, _ = server
    for index in range(49):
        shutil.copytree(library / "étoile", library / f"copy-{index:02d}")
    first = json.loads(get(base + "/api/v1/library", ctx).read())
    second = json.loads(get(base + "/api/v1/library?page=2", ctx).read())
    assert first["page"] == 1 and first["pages"] == 2 and len(first["scenes"]) == 48
    assert second["page"] == 2 and len(second["scenes"]) == 2
    full = get(base + second["scenes"][0]["ply_url"] + "?budget=1", ctx).read()
    low = get(base + second["scenes"][0]["ply_url"] + "?budget=0.15", ctx).read()
    assert b"element vertex 20\n" in full and b"element vertex 3\n" in low


def test_external_ply_with_preview_needs_no_vrphoto_metadata(server, library):
    base, _, ctx, _ = server
    scene = library / "external"
    scene.mkdir()
    make_ply(scene / "scene.ply")
    Image.new("RGB", (128, 128), "blue").save(scene / "preview.jpg")
    assert scene_valid(scene)
    assert b"external" in get(base + "/", ctx).read()
    assert get(base + "/media/external/preview.jpg", ctx).read(2) == b"\xff\xd8"
    assert b'id="preview"' in get(base + "/scene/external", ctx).read()
    assert b'"coordinateSystem": null' in get(base + "/scene/external", ctx).read()
    assert b'"coordinateSystem": "opencv-x-right-y-down-z-forward"' in get(base + "/scene/%C3%A9toile", ctx).read()

    (scene / "metadata.json").write_text('{"frames": [], "w": 1024}', encoding="utf-8")
    assert scene_valid(scene)
    assert b'id="preview"' in get(base + "/scene/external", ctx).read()

    (scene / "metadata.json").unlink()
    with pytest.raises(HTTPError) as error:
        get(base + "/media/external/metadata.json", ctx)
    assert error.value.code == 404


@pytest.mark.parametrize("server", [False], indirect=True)
def test_debug_routes_and_controls_are_disabled_by_default(server):
    base, port, ctx, proc = server
    assert json.loads(get(base + "/api/runtime", ctx).read()) == {"debug": False}
    assert b"/debug/" not in get(base + "/", ctx).read()
    scene = get(base + "/scene/%C3%A9toile?xrtest=1&engine=playcanvas", ctx).read()
    assert b"spark-viewer.js" in scene and b"/static/viewer.js?v=" not in scene
    assert b"send-xr-log" not in scene and b"/debug/" not in scene and b'id="reset"' not in scene
    for path in ("/debug/capabilities", "/debug/xr-events", "/static/raw-xr.js"):
        with pytest.raises(HTTPError) as error:
            get(base + path, ctx)
        assert error.value.code == 404


def test_bad_tls_and_concurrent_clients(server):
    base, port, ctx, proc = server
    started = time.monotonic()
    with socket.create_connection(("127.0.0.1", port), timeout=2) as sock:
        sock.sendall(b"GET / HTTP/1.1\r\nHost: localhost\r\n\r\n")
        sock.settimeout(2)
        try:
            assert not sock.recv(1024)
        except ConnectionResetError:
            pass
    assert time.monotonic() - started < 3
    assert get(base + "/", ctx).status == 200
    with socket.create_connection(("127.0.0.1", port), timeout=2) as sock:
        sock.sendall(b"\x16\x03\x01\x00")
        sock.settimeout(5)
        started = time.monotonic()
        try:
            assert not sock.recv(1024)
        except ConnectionResetError:
            pass
        assert time.monotonic() - started < 4.5
    with ThreadPoolExecutor(max_workers=8) as pool:
        assert all(pool.map(lambda _: get(base + "/", ctx).status == 200, range(16)))


@pytest.mark.skipif(os.name != "nt", reason="Windows SSL sendfile cancellation regression")
def test_aborted_large_ply_transfer_does_not_log_sendfile_error(server, library):
    base, port, ctx, proc = server
    with (library / "étoile" / "scene.ply").open("r+b") as out:
        out.truncate(32 * 1024 * 1024)
    with socket.create_connection(("127.0.0.1", port), timeout=5) as raw:
        with ctx.wrap_socket(raw, server_hostname="localhost") as stream:
            stream.sendall(b"GET /media/%C3%A9toile/scene.ply HTTP/1.1\r\nHost: localhost\r\n\r\n")
            assert b"200 OK" in stream.recv(4096)
    time.sleep(1)
    proc.terminate()
    _, errors = proc.communicate(timeout=8)
    assert "Unhandled exception" not in errors
    assert "_sendfile_fallback" not in errors


def test_traversal_is_blocked(server):
    base, port, ctx, proc = server
    for suffix in ("/media/%2e%2e/secret", "/media/%2e%2e%2fsecret", "/media/%2e%2e%5csecret"):
        try:
            get(base + suffix, ctx)
        except Exception:
            pass
        else:
            pytest.fail(f"Traversal unexpectedly succeeded: {suffix}")


def test_budget_preserves_sharp_trailer(tmp_path):
    source = tmp_path / "source.ply"
    make_ply(source, 20)
    result = reduced_ply(source, 0.5, tmp_path / "cache")
    data = result.read_bytes()
    assert b"element vertex 10\n" in data
    assert data.endswith(b"\x80\x02\x00\x00\xe0\x01\x00\x00")
    assert len(data) < source.stat().st_size


def test_process_batch_and_resume(tmp_path, monkeypatch):
    input_dir, output = tmp_path / "photos", tmp_path / "library"
    (input_dir / "nested").mkdir(parents=True)
    Image.new("RGB", (200, 100), "red").save(input_dir / "one.jpg")
    Image.new("RGB", (300, 150), "blue").save(input_dir / "nested" / "two.png")
    calls = []
    monkeypatch.setattr("vrphoto.importer.find_sharp", lambda _: Path("/fake/sharp"))
    monkeypatch.setattr("vrphoto.importer.check_sharp", lambda _: None)
    def fake_run(args, **kwargs):
        calls.append(args)
        inp, out = Path(args[args.index("-i") + 1]), Path(args[args.index("-o") + 1])
        for photo in inp.iterdir():
            make_ply(out / (photo.stem + ".ply"))
        return subprocess.CompletedProcess(args, 0, "", "")
    monkeypatch.setattr("vrphoto.importer.subprocess.run", fake_run)
    first = process(str(input_dir), str(output), "unused")
    assert first["processed"] == 2 and len(calls) == 1
    assert (output / "nested/two/preview.jpg").is_file()
    second = process(str(input_dir), str(output), "unused")
    assert second["skipped"] == 2 and len(calls) == 1


@pytest.mark.skipif(os.name == "nt", reason="Windows cannot send POSIX SIGINT to a detached test subprocess")
def test_sigint_shutdown(server):
    base, port, ctx, proc = server
    proc.send_signal(signal.SIGINT)
    assert proc.wait(timeout=8) == 0
    with pytest.raises(OSError):
        socket.create_connection(("127.0.0.1", port), timeout=0.5)
