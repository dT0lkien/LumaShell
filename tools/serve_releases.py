#!/usr/bin/env python3
import argparse
import html
import ipaddress
import re
import shutil
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent.parent / "artifacts" / "releases"
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")
FILES = {
    "LumaShell-win-x64.exe": ("Windows x64 · Intel и AMD", "application/octet-stream"),
    "LumaShell-win-arm64.exe": ("Windows ARM64", "application/octet-stream"),
    "Luma-Wallpaper.png": ("Обои", "image/png"),
    "README.md": ("Инструкция", "text/plain; charset=utf-8"),
    "SHA256SUMS.txt": ("Контрольные суммы SHA-256", "text/plain; charset=utf-8"),
}


def public_file(version, name):
    if not VERSION.fullmatch(version) or name not in FILES:
        return None
    folder = ROOT / version
    path = folder / name
    if folder.is_symlink() or path.is_symlink() or not path.is_file():
        return None
    return path


def index():
    groups = []
    versions = (path.name for path in ROOT.iterdir() if VERSION.fullmatch(path.name))
    for version in sorted(versions, key=lambda value: tuple(map(int, value.split("."))), reverse=True):
        links = []
        for name, (title, _) in FILES.items():
            path = public_file(version, name)
            if path is not None:
                size = f" · {path.stat().st_size / (1024 ** 2):.0f} МБ" if name.endswith(".exe") else ""
                links.append(f'<li><a href="/{version}/{name}">{html.escape(title)}</a>{size}</li>')
        if links:
            groups.append(f"<section><h2>{version}</h2><ul>{''.join(links)}</ul></section>")
    return ("<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width'>"
            "<title>Luma Shell</title><style>body{max-width:40rem;margin:4rem auto;padding:0 1rem;"
            "font:16px/1.65 system-ui,sans-serif;color:#f7f7fb;background:#11131d}a{color:#b6c8ff}"
            "section{padding:.1rem 1.2rem;margin:1rem 0;background:#222638;border-radius:1rem}</style>"
            f"<h1>Luma Shell</h1><p>Сборки Windows в локальной сети</p>{''.join(groups)}</html>").encode()


class Downloads(BaseHTTPRequestHandler):
    def do_GET(self):
        self.serve(send_body=True)

    def do_HEAD(self):
        self.serve(send_body=False)

    def serve(self, send_body):
        if ipaddress.ip_address(self.client_address[0]) not in self.server.subnet:
            self.send_error(403)
            return
        route = urlsplit(self.path).path
        if route == "/":
            body = index()
            self.headers_for("text/html; charset=utf-8", len(body))
            if send_body:
                self.wfile.write(body)
            return
        parts = route.split("/")
        path = public_file(parts[1], parts[2]) if len(parts) == 3 and not parts[0] else None
        if path is None:
            self.send_error(404)
            return
        with path.open("rb") as source:
            self.headers_for(FILES[path.name][1], path.stat().st_size, path.name if path.suffix == ".exe" else None)
            if send_body:
                shutil.copyfileobj(source, self.wfile, length=256 * 1024)

    def headers_for(self, content_type, length, download=None):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(length))
        self.send_header("X-Content-Type-Options", "nosniff")
        if download:
            self.send_header("Content-Disposition", f'attachment; filename="{download}"')
        self.end_headers()


def main():
    parser = argparse.ArgumentParser(description="Serve only Luma releases to a private IPv4 subnet.")
    parser.add_argument("--bind", type=ipaddress.IPv4Address, required=True)
    parser.add_argument("--subnet", type=ipaddress.IPv4Network, required=True)
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    private = [ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")]
    if args.bind not in args.subnet or not any(args.subnet.subnet_of(network) for network in private):
        parser.error("Bind to an address on the private subnet.")
    with ThreadingHTTPServer((str(args.bind), args.port), Downloads) as server:
        server.subnet = args.subnet
        print(f"Serving {ROOT} at http://{args.bind}:{args.port}/", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
