"""The bridge's transcription on a real voice message, when this machine has the models and a clip: the text comes
back and the bridge itself does not grow, because the model runs in a child process (2026-11-10: in-process Whisper
left Claude's bridge at 673 MB after one clip, and Arden's at 2.8 GB, OOM-killed). Run with the bridge's venv python;
ROOM_TEST_CLIP names a clip, else the newest saved voice message is used."""
import glob
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge
import roomcore as rc


def a_clip():
    if os.environ.get("ROOM_TEST_CLIP"):
        return os.environ["ROOM_TEST_CLIP"]
    found = sorted(glob.glob(str(rc.CONFIG_DIR / "state" / "attachments" / "*" / "voice-message.ogg")), key=os.path.getmtime)
    return found[-1] if found else None


CLIP = a_clip()
HAVE_PARAKEET = Path(rc.DEFAULTS["parakeet_dir"], "encoder-model.int8.onnx").is_file()


@unittest.skipUnless(CLIP and HAVE_PARAKEET, "no voice clip or no Parakeet model on this machine")
class TranscribeInAChild(unittest.TestCase):
    def test_parakeet_transcribes_and_the_bridge_does_not_grow(self):
        bridge.STATE = Path(os.environ.get("TMPDIR", "/tmp"))      # timings to a scratch file, not the live log
        config = dict(rc.DEFAULTS, transcriber="parakeet", transcribe_threads=4)
        before = bridge.own_rss_mb()
        text = bridge.transcribe(CLIP, config)
        grew = bridge.own_rss_mb() - before
        self.assertTrue(text and len(text.split()) > 3, text)
        self.assertLess(grew, 100, f"the bridge grew {grew} MB transcribing one clip")


if __name__ == "__main__":
    unittest.main()
