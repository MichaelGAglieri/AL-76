#!/usr/bin/env python3
"""AL-76's icon for Linux: the same drawing as Windows (windows/packaging/make_icons.py, only read here), as the
PNG sizes of the hicolor theme (packaging/icons/<size>.png).
    python3 -I -B linux/packaging/make_icons.py
"""
import importlib.util, os, sys

sys.dont_write_bytecode = True   # never leave __pycache__ in windows/
HERE = os.path.dirname(os.path.abspath(__file__))
source = os.path.join(HERE, "..", "..", "windows", "packaging", "make_icons.py")
spec = importlib.util.spec_from_file_location("windows_icons", source)
icons = importlib.util.module_from_spec(spec)
spec.loader.exec_module(icons)

os.makedirs(os.path.join(HERE, "icons"), exist_ok=True)
for size in (16, 24, 32, 48, 64, 128, 256, 512):
    with open(os.path.join(HERE, "icons", f"{size}.png"), "wb") as f:
        f.write(icons.png(size, size))
print("icone scritte")
