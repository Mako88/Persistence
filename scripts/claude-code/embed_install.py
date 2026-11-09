"""Sets up the embedder Persistence recall uses by meaning, the same one on every machine: bge-small-en-v1.5 (q8_0
GGUF) served by llama.cpp's llama-server on 127.0.0.1:8081. Repeatable where it was a hand setup (the work Claude,
2026-11-09): the model is downloaded and checked against its SHA-256, llama-server is found or built from llama.cpp
at a pinned commit, and a launcher bound to localhost is written. Nothing is installed system-wide.

    python embed_install.py [--dir <folder>] [--server <path to llama-server>]

Then set, in settings.json "env" (the script prints them): PERSISTENCE_EMBEDDING_ENABLED, _APIBASEURL, _MODEL, and
PERSISTENCE_EMBEDDING_SERVER (the launcher, which hook.py starts when recall finds the embedder down). The store's
vectors were made with this model: a different one makes recall by meaning compare unlike things.

Standard library only. Building llama.cpp needs git, cmake and a C++ compiler; pass --server to skip that.
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import urllib.request
from pathlib import Path

MODEL_URL = "https://huggingface.co/CompendiumLabs/bge-small-en-v1.5-gguf/resolve/main/bge-small-en-v1.5-q8_0.gguf"
MODEL_SHA256 = "ec38e8da142596baa913124ae50550de284b6916bf59577ef2f0cb9660c2f514"  # checked 2026-11-09, both machines
LLAMA_REPO = "https://github.com/ggml-org/llama.cpp"
LLAMA_COMMIT = "f1cee99"  # the build serving John's desktop and the "home" server, 2026-11-09
PORT = 8081
FLAGS = ["--embedding", "--pooling", "cls", "-ngl", "0", "-c", "512", "-b", "512", "-ub", "512", "--parallel", "1",
         "--threads", "2", "--host", "127.0.0.1", "--port", str(PORT)]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def model(folder):
    path = folder / "bge-small-en-v1.5-q8_0.gguf"
    if path.exists() and sha256(path) == MODEL_SHA256:
        print(f"model: {path} (checksum matches)")
        return path
    partial = path.with_suffix(".part")
    print(f"model: downloading {MODEL_URL}")
    urllib.request.urlretrieve(MODEL_URL, partial)
    if sha256(partial) != MODEL_SHA256:
        partial.unlink(missing_ok=True)
        sys.exit("model: the download's checksum does not match; nothing was kept.")
    partial.replace(path)
    print(f"model: {path} (checksum matches)")
    return path


def server(folder, given):
    if given:
        return Path(given)
    exe = "llama-server.exe" if os.name == "nt" else "llama-server"
    if found := shutil.which("llama-server"):
        print(f"llama-server: {found} (on PATH)")
        return Path(found)
    src = folder / "llama.cpp"
    for built in (src / "build" / "bin" / "Release" / exe, src / "build" / "bin" / exe):
        if built.exists():
            print(f"llama-server: {built}")
            return built
    for tool in ("git", "cmake"):
        if not shutil.which(tool):
            sys.exit(f"llama-server: not found, and building it needs {tool}. Install it, or pass --server <path>.")
    if not src.exists():
        subprocess.run(["git", "clone", "--quiet", LLAMA_REPO, str(src)], check=True)
    subprocess.run(["git", "-C", str(src), "checkout", "--quiet", LLAMA_COMMIT], check=True)
    # CPU only: the embedder must never take a GPU from training or a game on a shared machine.
    subprocess.run(["cmake", "-S", str(src), "-B", str(src / "build"), "-DGGML_CUDA=OFF", "-DLLAMA_CURL=OFF"], check=True)
    subprocess.run(["cmake", "--build", str(src / "build"), "--config", "Release", "--target", "llama-server", "-j"], check=True)
    return server(folder, None)


def launcher(folder, exe, gguf):
    if os.name == "nt":
        path = folder / "embedding-server.bat"
        path.write_text("@echo off\r\nrem Persistence recall's embedder: CPU only, localhost only.\r\nset CUDA_VISIBLE_DEVICES=-1\r\n"
                        f'"{exe}" -m "{gguf}" {" ".join(FLAGS)} > "{folder / "embedding-server.log"}" 2>&1\r\n', encoding="utf-8")
    else:
        path = folder / "embedding-server.sh"
        path.write_text("#!/bin/sh\n# Persistence recall's embedder: CPU only, localhost only.\nexport CUDA_VISIBLE_DEVICES=-1\n"
                        f'exec "{exe}" -m "{gguf}" {" ".join(FLAGS)} >> "{folder / "embedding-server.log"}" 2>&1\n', encoding="utf-8")
        path.chmod(0o755)
    print(f"launcher: {path}")
    return path


def main():
    parser = argparse.ArgumentParser(description="Set up Persistence recall's embedder (bge-small on llama-server).")
    parser.add_argument("--dir", default=str(Path.home() / ".claude" / "persistence" / "embed"))
    parser.add_argument("--server", help="an existing llama-server to use instead of building one")
    args = parser.parse_args()
    folder = Path(args.dir)
    folder.mkdir(parents=True, exist_ok=True)
    gguf = model(folder)
    run = launcher(folder, server(folder, args.server), gguf)
    print("\nAdd to settings.json \"env\":")
    for name, value in (("PERSISTENCE_EMBEDDING_ENABLED", "true"), ("PERSISTENCE_EMBEDDING_APIBASEURL", f"http://127.0.0.1:{PORT}/v1"),
                        ("PERSISTENCE_EMBEDDING_MODEL", "bge-small-en-v1.5"), ("PERSISTENCE_EMBEDDING_SERVER", run.as_posix())):
        print(f'  "{name}": "{value}",')


if __name__ == "__main__":
    main()
