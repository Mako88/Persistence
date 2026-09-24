"""Run a disposable copy of a peer store against the local model, for experiments.

A Fork copies a backup, makes the copy inert (pending wakes cancelled), writes a single-profile config
pointing at the local llama.cpp server, and launches Persistence.Api on the copy with command execution
disabled. Nothing here touches a live peer, its volume, or a paid provider:

- the store is a copy under experiments/runs/, archived afterwards rather than deleted;
- PERSISTENCE_CONFIGPATH points at the fork's own config (never the repo's persistence.json, which holds a
  real key and an enabled container executor), and inherited PERSISTENCE_* variables are cleared;
- Container.Enabled/Local are false, so exec/shell/read_file/write_file refuse to run;
- the config has exactly one profile, so a peer's set_model can't switch to a paid one;
- SharedDirectory is absent, so snapshot_db has nowhere to write.
"""
import json
import os
import shutil
import sqlite3
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
API_DLL = ROOT / "src" / "Persistence.Api" / "bin" / "Debug" / "net10.0" / "Persistence.Api.dll"
MODEL_BASE = "http://127.0.0.1:8085/v1"
MODEL_ID = "gemma-4-12B-it-q4_k_m.gguf"


def _http(method, url, body=None, headers=None, timeout=30):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        text = resp.read().decode("utf-8")
        return resp.status, (json.loads(text) if text else None)


def complete(system, user, max_tokens=300, timeout=900):
    """One direct chat completion against the local model, outside any peer's pipeline."""
    _, body = _http("POST", f"{MODEL_BASE}/chat/completions", {
        "model": MODEL_ID,
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
        "max_tokens": max_tokens,
    }, timeout=timeout)
    return body["choices"][0]["message"]["content"]


class Fork:
    def __init__(self, source_db, workdir, port, peer_name="Peer", max_iterations=6, max_input_tokens=28000,
                 provider="OpenAiChat"):
        # provider="LocalClaude" parks each assembled prompt at /api/peer/pending instead of calling a model,
        # which is how capture_prompt() gets the exact context a peer would see.
        self.provider = provider
        self.source_db = Path(source_db).resolve() if source_db else None
        self.workdir = Path(workdir).resolve()
        self.port = port
        self.peer_name = peer_name
        self.max_iterations = max_iterations
        self.max_input_tokens = max_input_tokens
        self.db = self.workdir / "store.db"
        self.base = f"http://127.0.0.1:{port}/api"
        self.proc = None
        self.log = None

    # --- lifecycle ---

    def start(self, timeout=120):
        self.workdir.mkdir(parents=True, exist_ok=True)
        if self.source_db:
            src = sqlite3.connect(f"file:{self.source_db.as_posix()}?immutable=1&mode=ro", uri=True)
            dst = sqlite3.connect(self.db)
            src.backup(dst)
            src.close()
            # Inert copy: nothing wakes it but us.
            dst.execute("UPDATE ScheduledEvents SET Status='2' WHERE Status='0'")
            dst.commit()
            dst.close()

        config = {
            "UiMode": "Api",
            "PeerName": self.peer_name,
            "MaxActionIterations": self.max_iterations,
            "SessionCostLimit": 0,
            "Container": {"Enabled": False, "Local": False},
            "Embedding": {"Enabled": False},
            "SelectedModel": "local",
            "Models": [{
                "Name": "local",
                "Provider": self.provider,
                "Model": MODEL_ID,
                "ApiBaseUrl": MODEL_BASE,
                "ApiKey": "local",
                "DatabasePath": str(self.db),
                "MaxInputTokens": self.max_input_tokens,
                "MaxOutputTokens": 2048,
                "Streaming": False,
                "RequestTimeoutSeconds": 900,
            }],
        }
        config_path = self.workdir / "config.json"
        config_path.write_text(json.dumps(config, indent=2), encoding="utf-8")

        env = {k: v for k, v in os.environ.items() if not k.upper().startswith("PERSISTENCE_")}
        env.update({
            "PERSISTENCE_CONFIGPATH": str(config_path),
            "PERSISTENCE_UIMODE": "Api",
            "PERSISTENCE_CONTAINER_ENABLED": "false",
            "PERSISTENCE_CONTAINER_LOCAL": "false",
            "ASPNETCORE_URLS": f"http://127.0.0.1:{self.port}",
            "ASPNETCORE_ENVIRONMENT": "Production",
        })
        self.log = open(self.workdir / "api.log", "w", encoding="utf-8")
        self.proc = subprocess.Popen(["dotnet", str(API_DLL)], cwd=str(self.workdir), env=env,
                                     stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.proc.poll() is not None:
                raise RuntimeError(f"API exited early; see {self.workdir / 'api.log'}")
            try:
                status, _ = _http("GET", f"{self.base}/conversation/snapshot", timeout=5)
                if status == 200:
                    self._await_startup_refresh(deadline)
                    return self
            except (urllib.error.URLError, ConnectionError, TimeoutError):
                pass
            time.sleep(1)
        self.stop()
        raise TimeoutError("API didn't come up")

    def _await_startup_refresh(self, deadline):
        # Startup emits the same scheduled/proposals refresh a finished turn does. Wait for it, so send()
        # can't mistake it for the end of its own turn.
        while time.time() < deadline:
            _, page = _http("GET", f"{self.base}/conversation/events?since=0")
            if any(e.get("kind") == "proposals" for e in page.get("events", [])):
                return
            time.sleep(1)
        raise TimeoutError("API never finished its startup refresh")

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.kill()
            self.proc.wait(timeout=30)
        if self.log:
            self.log.close()
            self.log = None

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.stop()

    # --- interaction ---

    def _latest_seq(self):
        _, snap = _http("GET", f"{self.base}/conversation/snapshot")
        return snap.get("latestSeq", 0)

    def _max_action_id(self):
        con = sqlite3.connect(f"file:{self.db.as_posix()}?mode=ro", uri=True)
        try:
            return con.execute("SELECT COALESCE(MAX(Id), 0) FROM ActionLogs").fetchone()[0]
        finally:
            con.close()

    def capture_prompt(self, text, sender="John", timeout=120):
        """LocalClaude forks only: sends a message and returns the assembled prompt the peer would get."""
        _http("POST", f"{self.base}/conversation/send", {"input": text}, headers={"X-Local-Peer": sender})
        deadline = time.time() + timeout
        while time.time() < deadline:
            status, body = _http("GET", f"{self.base}/peer/pending")
            if status == 200 and body:
                return body["prompt"]
            time.sleep(1)
        raise TimeoutError("no prompt was parked")

    def send(self, text, sender="John", addressed_to=None, from_peer=None, timeout=900):
        """Sends one message and waits for the turn to finish. Returns replies, events and logged actions."""
        seq = self._latest_seq()
        action_floor = self._max_action_id()
        body = {"input": text}
        if addressed_to:
            body["addressedTo"] = addressed_to
        if from_peer:
            body["fromPeer"] = from_peer
        _http("POST", f"{self.base}/conversation/send", body, headers={"X-Local-Peer": sender})

        events, started = [], time.time()
        # The orchestrator refreshes scheduled events, then proposals, after every turn; "proposals"
        # is the last thing a finished turn emits.
        while time.time() - started < timeout:
            _, page = _http("GET", f"{self.base}/conversation/events?since={seq}")
            for e in page.get("events", []):
                events.append(e)
                seq = max(seq, e.get("seq", seq))
            kinds = [e.get("kind") for e in events]
            if "thinking" in kinds and "proposals" in kinds[kinds.index("thinking"):]:
                break
            time.sleep(2)
        else:
            events.append({"kind": "harness-timeout"})

        con = sqlite3.connect(f"file:{self.db.as_posix()}?mode=ro", uri=True)
        try:
            actions = [
                {"id": r[0], "type": r[1], "payload": r[2], "result": r[3]}
                for r in con.execute(
                    "SELECT Id, ActionType, Payload, Result FROM ActionLogs WHERE Id > ? ORDER BY Id", (action_floor,))
            ]
        finally:
            con.close()
        replies = [e.get("text", "") for e in events if e.get("kind") == "reply"]
        return {"replies": replies, "events": events, "actions": actions,
                "seconds": round(time.time() - started, 1)}
