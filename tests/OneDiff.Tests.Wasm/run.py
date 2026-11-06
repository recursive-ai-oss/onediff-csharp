#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# dependencies = ["playwright==1.57.0"]
# ///
"""Runs the OneDiff tests built for the browser in headless Chrome or Chromium and fails on any test failure.

    uv run run.py <published wwwroot folder>

The folder is the `wwwroot` of `dotnet publish tests/OneDiff.Tests.Wasm -c Release`. It is served from a throwaway local
HTTP server; the page runs the test runner and sets window.__oneDiffExitCode when Main returns. The browser is the installed
Google Chrome unless ONEDIFF_CHROME names an executable; set ONEDIFF_CHROME=bundled to use Playwright's own Chromium.
Exit code: 0 when the runner printed a RESULT line with no failures and exited 0, 1 otherwise.
"""
import functools
import http.server
import os
import re
import sys
import threading
import time

from playwright.sync_api import sync_playwright

TIMEOUT_MS = int(os.environ.get("ONEDIFF_WASM_TIMEOUT_S", "1200")) * 1000
FILTER = os.environ.get("ONEDIFF_WASM_FILTER", "")  # optional test-name filter, passed to Main as its first argument


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".wasm": "application/wasm",
        ".js": "text/javascript",
        ".mjs": "text/javascript",
    }

    def log_message(self, format, *args):  # keep the job log to test output
        pass


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    root = os.path.abspath(sys.argv[1])
    if not os.path.isfile(os.path.join(root, "index.html")):
        print(f"no index.html under {root}")
        return 2

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=root))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{server.server_address[1]}/index.html" + (f"?arg={FILTER}" if FILTER else "")

    lines: list[str] = []
    with sync_playwright() as p:
        chrome = os.environ.get("ONEDIFF_CHROME", "")
        if chrome == "bundled":
            browser = p.chromium.launch(headless=True)
        elif chrome:
            browser = p.chromium.launch(headless=True, executable_path=chrome)
        else:
            browser = p.chromium.launch(headless=True, channel="chrome")
        print(f"browser: {browser.browser_type.name} {browser.version}", flush=True)
        page = browser.new_page()
        page.on("console", lambda m: (lines.append(m.text), print(m.text, flush=True)))
        page.on("pageerror", lambda e: (lines.append(f"PAGE ERROR: {e}"), print(f"PAGE ERROR: {e}", flush=True)))
        page.goto(url)
        # Polls with evaluate rather than wait_for_function: after a scan has been recognized, the page publishes its exit code
        # but wait_for_function never notices (evaluate still sees it), so the wait would run to its timeout.
        exit_code = -1
        deadline = time.monotonic() + TIMEOUT_MS / 1000
        while True:
            try:
                value = page.evaluate("window.__oneDiffExitCode")
            except Exception as e:  # crashed or closed page
                print(f"no exit code from the page: {e}")
                break
            if value is not None:
                exit_code = value
                break
            if time.monotonic() > deadline:
                print(f"no exit code from the page after {TIMEOUT_MS // 1000} s")
                break
            time.sleep(0.2)
        browser.close()
    server.shutdown()

    result = next((l for l in lines if l.startswith("RESULT ")), None)
    if result is None:
        print("the runner printed no RESULT line")
        return 1
    failed = int(re.search(r"failed=(\d+)", result).group(1))
    total = int(re.search(r"total=(\d+)", result).group(1))
    print(f"exit code {exit_code}; {result}")
    return 0 if exit_code == 0 and failed == 0 and total > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
