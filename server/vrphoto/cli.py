from __future__ import annotations

import argparse
import asyncio
import json
import logging

from .config import read_config
from .importer import add_ply, process
from .server import run_server


def main() -> None:
    parser = argparse.ArgumentParser(prog="vrphoto")
    parser.add_argument("--config", help="YAML configuration file")
    parser.add_argument("--verbose", action="store_true")
    commands = parser.add_subparsers(dest="command", required=True)
    importing = commands.add_parser("process", help="Batch import photos via an existing SHARP installation")
    importing.add_argument("input")
    importing.add_argument("output")
    importing.add_argument("--sharp-path")
    importing.add_argument("--preview-max-size", type=int)
    importing.add_argument("--preview-quality", type=int)
    importing.add_argument("--force", action="store_true")
    demo = commands.add_parser("add-ply", help="Add an existing SHARP PLY for the first PICO proof of concept")
    demo.add_argument("ply")
    demo.add_argument("photo")
    demo.add_argument("library")
    demo.add_argument("--name")
    serving = commands.add_parser("serve", help="Run foreground HTTPS library server")
    serving.add_argument("library")
    serving.add_argument("--host")
    serving.add_argument("--port", type=int)
    serving.add_argument("--state-dir")
    serving.add_argument("--certificate-file")
    serving.add_argument("--private-key-file")
    serving.add_argument("--ca-certificate-file")
    serving.add_argument("--http-redirect-port", type=int)
    serving.add_argument("--budget")
    serving.add_argument("--renderer", choices=["auto", "webgpu", "webgl2"])
    serving.add_argument("--debug", action="store_true", help="Enable diagnostic pages and XR event logging")
    args = parser.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    config = read_config(args.config)
    if args.command == "process":
        result = process(args.input, args.output, args.sharp_path or config["sharp_path"],
                         args.preview_max_size or config["preview_max_size"],
                         args.preview_quality or config["preview_quality"], args.force)
        print(json.dumps(result, indent=2))
        if result["failed"]:
            raise SystemExit(1)
    elif args.command == "add-ply":
        print(add_ply(args.ply, args.photo, args.library, args.name))
    else:
        try:
            asyncio.run(run_server(args.library, args.host or config["https_host"],
                                   args.port or config["https_port"],
                                   args.state_dir or config["state_dir"],
                                   args.budget or config["default_splat_budget"],
                                   args.renderer or config["viewer_renderer_preference"],
                                   args.http_redirect_port if args.http_redirect_port is not None else config["http_redirect_port"],
                                   args.certificate_file or config["certificate_file"],
                                   args.private_key_file or config["private_key_file"],
                                   args.ca_certificate_file or config["ca_certificate_file"],
                                   args.debug))
        except KeyboardInterrupt:
            logging.info("Interrupted")


if __name__ == "__main__":
    main()
