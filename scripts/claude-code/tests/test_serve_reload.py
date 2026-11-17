"""The hook server re-execs when any module beside it changes, not only hook.py: #78 deployed observed.py alone on
2026-11-16 and the served per-prompt hooks kept the old code for hours (sleep pass #19923)."""
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook


class ServeReload(unittest.TestCase):
    def test_a_changed_sibling_module_changes_the_stamp(self):
        with tempfile.TemporaryDirectory() as folder:
            for name in ("hook.py", "observed.py"):
                (Path(folder) / name).write_text("x = 1\n", encoding="utf-8")
            before = hook.code_stamp(folder)
            later = os.stat(Path(folder) / "observed.py").st_mtime + 5
            os.utime(Path(folder) / "observed.py", (later, later))
            self.assertNotEqual(hook.code_stamp(folder), before)

    def test_a_new_module_changes_the_stamp(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "hook.py").write_text("x = 1\n", encoding="utf-8")
            before = hook.code_stamp(folder)
            (Path(folder) / "concepts.py").write_text("y = 2\n", encoding="utf-8")
            self.assertNotEqual(hook.code_stamp(folder), before)

    def test_serve_compares_the_whole_folder_not_its_own_file(self):
        source = Path(hook.__file__).read_text(encoding="utf-8")
        self.assertNotIn("script.stat().st_mtime != written", source)
        self.assertIn("code_stamp(script.parent) != written", source)


if __name__ == "__main__":
    unittest.main()
