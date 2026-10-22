# -*- coding: utf-8 -*-
"""guardAI 发版打包脚本：Python zipfile 正斜杠 arcname，防 Linux unzip 反斜杠路径炸。"""
import os
import sys
import zipfile

DIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dist")

for rid in ("win-x64", "linux-x64"):
    src = os.path.join(DIST, rid)
    out = os.path.join(DIST, f"guardAI-{rid}.zip")
    if not os.path.isdir(src):
        sys.exit(f"缺少产物目录: {src}")
    if os.path.exists(out):
        os.remove(out)
    count = 0
    bad = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _dirs, files in os.walk(src):
            for name in files:
                full = os.path.join(root, name)
                rel = os.path.relpath(full, src).replace(os.sep, "/")  # 强制正斜杠
                if "\\" in rel:
                    bad += 1
                zf.write(full, rel)
                count += 1
    size_mb = os.path.getsize(out) / 1024 / 1024
    print(f"{rid}: {count} 个文件, {size_mb:.1f} MB, 反斜杠条目 {bad} (须为 0)")
    if bad:
        sys.exit(f"{rid} 打包出现反斜杠条目，中止")
print("打包完成")
