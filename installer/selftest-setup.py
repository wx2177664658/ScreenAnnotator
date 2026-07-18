# -*- coding: utf-8 -*-
"""CR-009 §5 smoke test for the Inno installer."""
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(r"e:\cursor\cursor-workerspace\test4")
SETUP = ROOT / "dist" / "ScreenAnnotator-Setup-1.0.0.exe"
APP_DIR = Path(os.environ["LOCALAPPDATA"]) / "Programs" / "ScreenAnnotator"
EXE = APP_DIR / "ScreenAnnotator.exe"
SETTINGS_DIR = Path(os.environ["APPDATA"]) / "ScreenAnnotator"
MARKER = SETTINGS_DIR / "cr009-uninstall-keep-me.txt"
START_MENU = Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "屏幕标注白板"
START_LNK = START_MENU / "屏幕标注白板.lnk"
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7C3E9F2-4B1D-4E8A-9C6F-2D5A8B0E1F34}_is1"

results = []


def ok(name, cond, detail=""):
    results.append((bool(cond), name, detail))
    print(("PASS" if cond else "FAIL"), name, detail)


def run(cmd, timeout=180):
    print(">", " ".join(str(c) for c in cmd))
    return subprocess.run(cmd, timeout=timeout, capture_output=True, text=True, encoding="utf-8", errors="replace")


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "ScreenAnnotator.exe"], capture_output=True)


def uninstall_string():
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as k:
            val, _ = winreg.QueryValueEx(k, "UninstallString")
            return val
    except OSError:
        return None


def main():
    if not SETUP.exists():
        print("MISSING setup", SETUP)
        sys.exit(2)

    kill_app()

    # Ensure settings marker exists before uninstall test
    SETTINGS_DIR.mkdir(parents=True, exist_ok=True)
    MARKER.write_text("keep-on-uninstall\n", encoding="utf-8")

    # 1) Install
    r = run([str(SETUP), "/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/SP-"])
    ok("1 install exit 0", r.returncode == 0, f"code={r.returncode}")
    time.sleep(1)
    ok("1 exe exists", EXE.exists(), str(EXE))
    ok("1 start menu shortcut", START_LNK.exists(), str(START_LNK))

    # 3) Self-contained run (no .NET needed) via --self-test
    r = run([str(EXE), "--self-test"], timeout=60)
    ok("3 self-test", r.returncode == 0, f"code={r.returncode}")

    # 5 first half: reinstall / upgrade overwrite
    r = run([str(SETUP), "/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/SP-"])
    ok("5 upgrade reinstall", r.returncode == 0 and EXE.exists(), f"code={r.returncode}")
    ok("5 still one app dir", APP_DIR.is_dir(), str(APP_DIR))

    # 4) Uninstall — keep settings
    u = uninstall_string()
    ok("4 uninstall entry found", bool(u), str(u))
    if u:
        # UninstallString is often: "C:\...\unins000.exe"
        # Need /VERYSILENT
        cmd = [u.strip('"'), "/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES"]
        r = run(cmd)
        ok("4 uninstall exit 0", r.returncode == 0, f"code={r.returncode}")
        time.sleep(1)
        ok("4 program files removed", not EXE.exists(), str(EXE))
        ok("4 start menu removed", not START_LNK.exists(), str(START_LNK))
        ok("4 settings retained", MARKER.exists(), str(MARKER))

    # 5 again: reinstall after uninstall
    r = run([str(SETUP), "/VERYSILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/SP-"])
    ok("5 reinstall after uninstall", r.returncode == 0 and EXE.exists())
    r = run([str(EXE), "--self-test"], timeout=60)
    ok("5 post-reinstall self-test", r.returncode == 0)

    print("\n=== SUMMARY ===")
    fails = [x for x in results if not x[0]]
    for passed, name, detail in results:
        print(("OK" if passed else "XX"), name)
    print(f"{len(results) - len(fails)}/{len(results)} passed")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
