"""Entry point:  python -m pdf_qa_web [--host 0.0.0.0] [--port 8765] [--no-browser]"""
import argparse
import os
import sys
import threading
import webbrowser

# allow running from the project root without installing
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

def main():
    env_port = os.environ.get("PORT")
    default_port = int(env_port) if env_port else 8765
    default_host = os.environ.get("HOST", "0.0.0.0" if env_port else "127.0.0.1")

    ap = argparse.ArgumentParser(description="JNN PDF — web")
    ap.add_argument("--host", default=default_host,
                    help="127.0.0.1 = chỉ máy này; 0.0.0.0 = cho máy khác trong mạng LAN/Cloud truy cập")
    ap.add_argument("--port", type=int, default=default_port)
    ap.add_argument("--no-browser", action="store_true", default=bool(env_port))
    a = ap.parse_args()
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    import uvicorn
    url = f"http://{'localhost' if a.host in ('127.0.0.1', '0.0.0.0') else a.host}:{a.port}/"
    print(f"\n  JNN PDF Web đang chạy tại: {url}\n  (Ctrl+C để dừng)\n")
    if not a.no_browser:
        threading.Timer(1.2, lambda: webbrowser.open(url)).start()
    uvicorn.run("pdf_qa_web.server:app", host=a.host, port=a.port, log_level="warning")


if __name__ == "__main__":
    main()
