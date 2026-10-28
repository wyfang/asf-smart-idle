#!/usr/bin/env python3
"""Package only plugin-owned binaries and installation material, with stable ZIP metadata."""

from __future__ import annotations

import os
from pathlib import Path
import sys
import tempfile
import zipfile


def main() -> None:
    project = Path(__file__).resolve().parent.parent
    binaries = project / "src" / "SmartIdle.Plugin" / "bin" / "Release" / "net10.0"
    files = {
        "SmartIdle/SmartIdle.dll": binaries / "SmartIdle.dll",
        "SmartIdle/SmartIdle.Core.dll": binaries / "SmartIdle.Core.dll",
    }
    for name, path in files.items():
        if not path.is_file():
            raise ValueError(f"缺少构建产物 {path}；请先运行 scripts/build.sh")
    for filename in ("README.md", "README.en.md", "LICENSE", "LICENSE.txt", "NOTICE"):
        path = project / filename
        if path.is_file():
            files[f"SmartIdle/{filename}"] = path
    # Examples are direct children only; never include user config, state, or credentials.
    for path in sorted((project / "examples").glob("*.json")):
        if path.is_file() and not path.is_symlink():
            files[f"SmartIdle/examples/{path.name}"] = path

    default_output = Path.home() / "Documents" / "Codex" / "outputs" / "asf-smart-idle" / "v0.3.0"
    output = Path(os.environ.get("OUTPUT_DIR", str(default_output))).expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True)
    destination = output / "SmartIdle.zip"
    handle, temporary_name = tempfile.mkstemp(prefix="SmartIdle-", suffix=".zip", dir=output)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, path in sorted(files.items()):
                info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, path.read_bytes(), compresslevel=9)
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    print(f"安装包：{destination}")
    print(f"压缩后：{destination.stat().st_size:,} 字节")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print(f"打包失败：{error}", file=sys.stderr)
        sys.exit(1)
