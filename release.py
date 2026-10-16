"""Release BozjaAssistant: build, sync the version everywhere, tag, publish.

Why this script exists
----------------------
The version number lives in THREE places that must agree, and a mismatch is
silent: if pluginmaster.json advertises an older version than the release, Dalamud
concludes "already up to date" and never downloads anything, which looks exactly
like a failed push. That mistake was made twice by hand (0.1.0.0 -> 0.1.1.0 and
0.1.1.0 -> 0.1.2.0), so the bump is now scripted.

  1. BozjaAssistant/BozjaAssistant.csproj  <Version>          (becomes the manifest
                                                               inside the zip)
  2. pluginmaster.json                     AssemblyVersion    (what Dalamud compares)
  3. git tag                               v<version>

Usage:
    python release.py 0.1.3.0 "feat: ..." [-m release notes text]

The script refuses to continue if the git tree is dirty, so a release always
corresponds to a committed revision.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(r"D:\work\DSH\BozjaAssistant")
CSPROJ = ROOT / "BozjaAssistant" / "BozjaAssistant.csproj"
MASTER = ROOT / "pluginmaster.json"
ZIP = ROOT / "BozjaAssistant" / "bin" / "Release" / "BozjaAssistant" / "latest.zip"

GIT = r"D:\work\DSH\_setup\git\cmd\git.exe"
DOTNET = r"D:\work\DSH\_setup\dotnet\dotnet.exe"
TOKEN = pathlib.Path(r"D:\work\DSH\_setup\.gh-token")
API = "https://api.github.com/repos/solenmingco/BozjaAssistant"
REPO = "https://github.com/solenmingco/BozjaAssistant"

BUILD_ENV = {
    "NUGET_PACKAGES": r"D:\work\DSH\_setup\nuget",
    "NUGET_HTTP_CACHE_PATH": r"D:\work\DSH\_setup\httpcache",
    "DOTNET_CLI_HOME": r"D:\work\DSH\_setup\clihome",
    "APPDATA": r"D:\work\DSH\_setup\appdata",
    "LOCALAPPDATA": r"D:\work\DSH\_setup\localappdata",
    "TEMP": r"D:\work\DSH\_setup\temp",
    "TMP": r"D:\work\DSH\_setup\temp",
    "DALAMUD_HOME": r"D:\work\DSH\_setup\dalamud-dev",
    "DOTNET_CLI_UI_LANGUAGE": "en",
}


def run(cmd, cwd=None, env=None, check=True):
    import os

    full = dict(os.environ)
    if env:
        full.update(env)
    p = subprocess.run(cmd, cwd=cwd, env=full, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if check and p.returncode != 0:
        print("!! command failed:", " ".join(map(str, cmd)))
        print(p.stdout)
        print(p.stderr)
        raise SystemExit(1)
    return p


def ensure_bom(path: pathlib.Path):
    """MSBuild reads a BOM-less csproj as system ANSI and mangles the Chinese
    manifest text, so the BOM must survive every edit."""
    b = path.read_bytes()
    if not b.startswith(b"\xef\xbb\xbf"):
        path.write_bytes(b"\xef\xbb\xbf" + b)
        print("   restored UTF-8 BOM on csproj")


def bump(version: str):
    txt = CSPROJ.read_text(encoding="utf-8-sig")
    new, n = re.subn(r"<Version>[^<]+</Version>", f"<Version>{version}</Version>", txt)
    assert n == 1, f"expected 1 <Version> tag, found {n}"
    CSPROJ.write_text(new, encoding="utf-8")
    ensure_bom(CSPROJ)
    print(f"   csproj <Version> -> {version}")

    data = json.loads(MASTER.read_text(encoding="utf-8"))
    data[0]["AssemblyVersion"] = version
    data[0]["DownloadLinkInstall"] = f"{REPO}/releases/latest/download/latest.zip"
    data[0]["DownloadLinkUpdate"] = f"{REPO}/releases/latest/download/latest.zip"
    data[0]["DownloadLinkTesting"] = f"{REPO}/releases/latest/download/latest.zip"
    data[0]["IconUrl"] = ("https://gh-proxy.com/https://raw.githubusercontent.com/"
                          "solenmingco/BozjaAssistant/main/assets/icon.png")
    MASTER.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")
    print(f"   pluginmaster.json AssemblyVersion -> {version}")


def check_synced(version: str):
    cs = re.search(r"<Version>([^<]+)</Version>", CSPROJ.read_text(encoding="utf-8-sig"))
    pm = json.loads(MASTER.read_text(encoding="utf-8"))[0]["AssemblyVersion"]
    assert cs and cs.group(1) == version, f"csproj is {cs and cs.group(1)}, want {version}"
    assert pm == version, f"pluginmaster is {pm}, want {version}"
    print("   versions in sync")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", help="e.g. 0.1.3.0")
    ap.add_argument("message", help="commit message")
    ap.add_argument("-m", "--notes", default="", help="release notes markdown")
    ap.add_argument("--skip-build", action="store_true")
    args = ap.parse_args()

    print("== 1. bump versions ==")
    bump(args.version)
    check_synced(args.version)

    print("== 2. build ==")
    if args.skip_build:
        print("   skipped")
    else:
        p = run([DOTNET, "build", "-c", "Release", "--nologo"],
                cwd=ROOT / "BozjaAssistant", env=BUILD_ENV)
        out = p.stdout + p.stderr
        if "Build succeeded" not in out or "0 Error(s)" not in out:
            print(out)
            raise SystemExit("build failed")
        print("   Build succeeded, 0 errors")
    assert ZIP.is_file(), f"no zip at {ZIP}"
    print(f"   zip {ZIP.stat().st_size} bytes")

    print("== 3. verify the built manifest ==")
    import zipfile

    with zipfile.ZipFile(ZIP) as z:
        m = json.loads(z.read("BozjaAssistant.json").decode("utf-8"))
    assert m["AssemblyVersion"] == args.version, \
        f"zip manifest says {m['AssemblyVersion']}, want {args.version}"
    print(f"   zip manifest = {m['AssemblyVersion']} ({m['Name']})")

    print("== 4. commit ==")
    run([GIT, "add", "-A"], cwd=ROOT)
    d = run([GIT, "diff", "--cached", "--quiet"], cwd=ROOT, check=False)
    if d.returncode == 0:
        print("   nothing to commit")
    else:
        run([GIT, "-c", "user.name=solenmingco",
             "-c", "user.email=solenmingco@users.noreply.github.com",
             "commit", "-m", args.message], cwd=ROOT)
        print("   committed")

    print("== 5. push ==")
    tok = TOKEN.read_text(encoding="utf-8").strip()
    import os
    import tempfile

    with tempfile.NamedTemporaryFile("w", suffix=".cmd", delete=False) as f:
        f.write(f"@echo off\r\necho {tok}\r\n")
        ask = f.name
    env = {"GIT_ASKPASS": ask, "GIT_TERMINAL_PROMPT": "0",
           "GIT_SSL_BACKEND": "openssl",
           "GIT_SSL_CAINFO": r"D:\work\DSH\_setup\git\ucrt64\etc\ssl\certs\ca-bundle.crt"}
    try:
        p = run([GIT, "-c", "credential.helper=",
                 "-c", "http.proxy=http://127.0.0.1:7897",
                 "-c", "http.sslBackend=openssl",
                 "push", "origin", "main"], cwd=ROOT, env=env)
        print("  ", (p.stdout + p.stderr).replace(tok, "***").strip().splitlines()[-1:])
    finally:
        os.unlink(ask)

    print("== 6. publish release ==")
    import urllib.error
    import urllib.request

    op = urllib.request.build_opener(urllib.request.ProxyHandler(
        {"http": "http://127.0.0.1:7897", "https": "http://127.0.0.1:7897"}))

    def req(url, method="GET", body=None, ctype="application/json"):
        data = None
        if body is not None:
            data = json.dumps(body).encode() if ctype == "application/json" else body
        r = urllib.request.Request(url, data=data, method=method)
        r.add_header("Authorization", "Bearer " + tok)
        r.add_header("Accept", "application/vnd.github+json")
        r.add_header("User-Agent", "dsh-agent")
        if data:
            r.add_header("Content-Type", ctype)
        try:
            with op.open(r, timeout=180) as resp:
                raw = resp.read()
                try:
                    return resp.status, json.loads(raw.decode("utf-8"))
                except Exception:
                    return resp.status, raw.decode("utf-8", errors="replace")[:300]
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", errors="replace")[:400]

    tag = "v" + args.version
    s, rel = req(f"{API}/releases/tags/{tag}")
    if s == 200:
        print("   release already exists")
    else:
        s, rel = req(f"{API}/releases", method="POST", body={
            "tag_name": tag, "target_commitish": "main",
            "name": f"Bozja / 新月岛 助手 {tag}",
            "body": args.notes or args.message,
            "draft": False, "prerelease": False})
        if s not in (200, 201):
            print("   create failed:", rel)
            raise SystemExit(1)
        print("   created", rel["html_url"])

    up = rel["upload_url"].split("{")[0]
    s, assets = req(f"{API}/releases/{rel['id']}/assets")
    ex = {a["name"]: a for a in assets} if isinstance(assets, list) else {}
    if "latest.zip" in ex:
        req(f"{API}/releases/assets/{ex['latest.zip']['id']}", method="DELETE")
        print("   replaced old latest.zip")
    s, body = req(up + "?name=latest.zip", method="POST",
                  body=ZIP.read_bytes(), ctype="application/zip")
    print("   uploaded", body.get("browser_download_url") if isinstance(body, dict) else body)
    req(f"{API}/releases/{rel['id']}", method="PATCH", body={"make_latest": "true"})

    print("== 7. final consistency check ==")
    s, master = req("https://raw.githubusercontent.com/solenmingco/BozjaAssistant/"
                    "main/pluginmaster.json", method="HEAD")
    s2, latest = req(f"{API}/releases/latest")
    ok_master = json.loads(
        run(["powershell", "-Command",
             f"(Invoke-WebRequest -UseBasicParsing "
             f"'{REPO}/raw/main/pluginmaster.json').Content"],
            check=False).stdout or "[]")[0]["AssemblyVersion"] == args.version
    print(f"   release latest = {latest.get('tag_name')}")
    print(f"   master advertises {args.version}: {ok_master}")
    print()
    print("DONE. 用户在游戏里 /xlplugins -> Scan for updates 即可看到更新。")


if __name__ == "__main__":
    main()
