#!/usr/bin/env python3
"""Prepare checked, version-pinned ASF reference assemblies outside the source tree."""

from __future__ import annotations

import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
import zipfile


VERSION = "6.3.10.6"
ARCHIVE = "ASF-generic.zip"
BASE_URL = f"https://github.com/JustArchiNET/ArchiSteamFarm/releases/download/{VERSION}"


def diagnostic(message: str) -> None:
    print(message, file=sys.stderr)


def cache_directory() -> Path:
    if sys.platform == "darwin":
        base = Path.home() / "Library" / "Caches"
    else:
        base = Path(os.environ.get("XDG_CACHE_HOME", str(Path.home() / ".cache")))
    return base / "asf-smart-idle" / VERSION


def sha512(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, destination: Path) -> None:
    diagnostic(f"下载 {url}")
    temporary = destination.with_name(destination.name + ".download")
    temporary.unlink(missing_ok=True)
    try:
        subprocess.run(
            [
                "curl", "--fail", "--location", "--silent", "--show-error",
                "--connect-timeout", "15", "--max-time", "120",
                "--proto", "=https", "--proto-redir", "=https",
                url, "--output", str(temporary),
            ],
            check=True,
            timeout=130,
        )
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def expected_checksum(manifest: Path) -> str:
    for line in manifest.read_text(encoding="utf-8-sig").splitlines():
        columns = line.split(maxsplit=1)
        if len(columns) != 2:
            continue
        digest, name = columns
        if name.lstrip("*").strip() == ARCHIVE:
            if len(digest) != 128 or any(c not in "0123456789abcdefABCDEF" for c in digest):
                raise ValueError("SHA512SUMS 中的摘要格式不正确")
            return digest.lower()
    raise ValueError(f"SHA512SUMS 没有包含 {ARCHIVE}")


def validate_version(reference: Path) -> None:
    for filename in ("ArchiSteamFarm.dll", "ArchiSteamFarm.deps.json", "SteamKit2.dll"):
        if not (reference / filename).is_file():
            raise ValueError(f"参考目录缺少 {filename}：{reference}")
    dependencies = json.loads((reference / "ArchiSteamFarm.deps.json").read_text(encoding="utf-8"))
    actual = [name for name in dependencies.get("libraries", {}) if name.startswith("ArchiSteamFarm/")]
    if actual != [f"ArchiSteamFarm/{VERSION}"]:
        raise ValueError(f"要求 ASF {VERSION}，参考目录声明了 {actual!r}")


def archive_files(archive: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            if item.is_dir():
                continue
            path = PurePosixPath(item.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in item.filename:
                raise ValueError(f"压缩包包含不安全路径：{item.filename}")
            if item.filename in result:
                raise ValueError(f"压缩包包含重复文件：{item.filename}")
            result[item.filename] = hashlib.sha512(source.read(item)).hexdigest()
    return result


def matches_archive(reference: Path, files: dict[str, str]) -> bool:
    # Check only the explicit archive entries; do not recursively scan arbitrary directories.
    return reference.is_dir() and all(
        (reference / name).is_file()
        and not (reference / name).is_symlink()
        and sha512(reference / name) == digest
        for name, digest in files.items()
    )


def main() -> None:
    cache = cache_directory()
    cache.mkdir(parents=True, exist_ok=True)
    with (cache / ".prepare.lock").open("a+") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        manifest = cache / "SHA512SUMS"
        if not manifest.is_file():
            download(f"{BASE_URL}/SHA512SUMS", manifest)
        expected = expected_checksum(manifest)
        archive = cache / ARCHIVE
        if not archive.is_file() or sha512(archive) != expected:
            download(f"{BASE_URL}/{ARCHIVE}", archive)
        if sha512(archive) != expected:
            archive.unlink(missing_ok=True)
            raise ValueError("ASF 压缩包 SHA512 校验失败，已丢弃下载文件")

        files = archive_files(archive)
        override = os.environ.get("ASF_REFERENCE_DIR")
        reference = Path(override).expanduser().resolve() if override else cache / "reference"
        if override:
            validate_version(reference)
            # Only host reference DLLs and dependency metadata are relevant in an installed ASF.
            required = {name: digest for name, digest in files.items()
                        if "/" not in name and (name.endswith(".dll") or name == "ArchiSteamFarm.deps.json")}
            if not matches_archive(reference, required):
                raise ValueError("ASF_REFERENCE_DIR 的 DLL 或依赖元数据与官方固定版本不一致")
        elif not matches_archive(reference, files):
            diagnostic(f"解压已验证的 ASF {VERSION} 到 {reference}")
            temporary = Path(tempfile.mkdtemp(prefix="reference-", dir=cache))
            try:
                with zipfile.ZipFile(archive) as source:
                    source.extractall(temporary)
                validate_version(temporary)
                if not matches_archive(temporary, files):
                    raise ValueError("ASF 解压文件校验失败")
                if reference.exists():
                    shutil.rmtree(reference)
                temporary.replace(reference)
            finally:
                if temporary.exists():
                    shutil.rmtree(temporary)
        validate_version(reference)
        diagnostic(f"ASF {VERSION} 参考文件校验通过")
        print(reference)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError, zipfile.BadZipFile) as error:
        diagnostic(f"准备 ASF 参考文件失败：{error}")
        sys.exit(1)
