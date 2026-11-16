"""Every test file's `if __name__ == "__main__":` guard is its last top-level statement. A class written below the
guard is skipped when the file is run as a script, though discovery still runs it, so the miss is easy to overlook
(Arden's nit on #76: ResumeKeepsTheWindow sat below the guard in test_recall_fallback.py)."""
import ast
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)

TESTS = Path(__file__).resolve().parent


def is_main_guard(node):
    return (isinstance(node, ast.If) and isinstance(node.test, ast.Compare)
            and isinstance(node.test.left, ast.Name) and node.test.left.id == "__name__"
            and any(isinstance(c, ast.Constant) and c.value == "__main__" for c in node.test.comparators))


def after_guard(source):
    """The line numbers of top-level statements that come after the main guard."""
    body = ast.parse(source).body
    guards = [i for i, node in enumerate(body) if is_main_guard(node)]
    return [node.lineno for node in body[guards[0] + 1:]] if guards else []


class MainGuardLast(unittest.TestCase):
    def test_nothing_follows_the_main_guard(self):
        late = {p.name: after_guard(p.read_text(encoding="utf-8")) for p in sorted(TESTS.glob("test_*.py"))}
        self.assertEqual({name: lines for name, lines in late.items() if lines}, {})

    def test_the_check_sees_a_class_below_the_guard(self):
        self.assertEqual(after_guard('if __name__ == "__main__":\n    pass\n\n\nclass Late:\n    pass\n'), [5])
        self.assertEqual(after_guard('class Early:\n    pass\n\n\nif __name__ == "__main__":\n    pass\n'), [])


if __name__ == "__main__":
    unittest.main()
