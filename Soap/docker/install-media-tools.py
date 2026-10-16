"""Install checksum-verified Linux tools; latest FFmpeg means its stable release line."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tarfile
import tempfile
import urllib.request


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Soap-image-build"})
    return urllib.request.urlopen(request, timeout=120)


def download_verified(base, name, manifest, destination):
    with fetch(base + "/" + manifest) as response:
        checksums = response.read().decode().splitlines()
    expected = next((line.split()[0] for line in checksums
                     if len(line.split()) == 2 and line.split()[1].lstrip("*") == name), None)
    if not expected:
        raise RuntimeError("No published checksum for " + name)
    digest = hashlib.sha256()
    with fetch(base + "/" + name) as response, destination.open("wb") as target:
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
            target.write(chunk)
    if digest.hexdigest() != expected:
        raise RuntimeError("Checksum mismatch for " + name)


arch = os.environ["TARGETARCH"]
architectures = {"amd64": ("yt-dlp_linux", "linux64"),
                 "arm64": ("yt-dlp_linux_aarch64", "linuxarm64")}
if arch not in architectures:
    raise RuntimeError("Unsupported architecture: " + arch)
yt_asset, ff_arch = architectures[arch]
output = Path("/tools")
output.mkdir()
yt_version = os.environ.get("YTDLP_VERSION", "latest")
yt_base = "https://github.com/yt-dlp/yt-dlp/releases/" + (
    "latest/download" if yt_version == "latest" else "download/" + yt_version)
download_verified(yt_base, yt_asset, "SHA2-256SUMS", output / "yt-dlp")

# BtbN provides Linux static builds linked from ffmpeg.org. Choose the highest
# stable nX.Y build, never a master/nightly build, and include libx264 (GPL).
with fetch("https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest") as response:
    release = json.load(response)
pattern = re.compile(r"ffmpeg-n(\d+(?:\.\d+)+)-(?:latest|\d+-g[0-9a-f]+)-" + ff_arch + r"-gpl-\d+(?:\.\d+)+\.tar\.xz")
candidates = [(tuple(map(int, match[1].split("."))), asset["name"])
              for asset in release["assets"] if (match := pattern.fullmatch(asset["name"]))]
ff_version = os.environ.get("FFMPEG_VERSION", "latest")
if ff_version != "latest":
    candidates = [item for item in candidates if item[0][:len(ff_version.split("."))] == tuple(map(int, ff_version.split("."))) ]
if not candidates:
    raise RuntimeError("No matching stable FFmpeg build for " + ff_version)
version, ff_asset = max(candidates)
ff_base = "https://github.com/BtbN/FFmpeg-Builds/releases/download/" + release["tag_name"]
with tempfile.TemporaryDirectory() as tmp:
    archive = Path(tmp) / ff_asset
    download_verified(ff_base, ff_asset, "checksums.sha256", archive)
    with tarfile.open(archive) as bundle:
        for tool in ("ffmpeg", "ffprobe"):
            member = next(m for m in bundle.getmembers() if m.isfile() and m.name.endswith("/bin/" + tool))
            with bundle.extractfile(member) as source, (output / tool).open("wb") as target:
                shutil.copyfileobj(source, target)
        # Keep the distributor's license alongside the static GPL executables.
        for member in bundle.getmembers():
            if member.isfile() and Path(member.name).name.startswith(("LICENSE", "COPYING")):
                with bundle.extractfile(member) as source, (output / Path(member.name).name).open("wb") as target:
                    shutil.copyfileobj(source, target)
for tool in ("yt-dlp", "ffmpeg", "ffprobe"):
    (output / tool).chmod(0o755)
print("Installed yt-dlp " + yt_version + "; FFmpeg stable " + ".".join(map(str, version)), flush=True)
