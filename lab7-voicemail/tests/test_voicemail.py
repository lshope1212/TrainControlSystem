"""End-to-end tests for greeting changes."""
from pathlib import Path
import subprocess
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_GREETING = "You have reached mailbox 13. Please leave a message now."
SAVED_GREETING = "Hi, this is Owen. Leave a message after the beep."
INITIAL_PROMPT = "Enter mailbox number followed by #"
GREETING_PROMPT = "Record your greeting, then press the # key"


class GreetingInteractions(unittest.TestCase):
    def run_session(self, events):
        result = subprocess.run(
            [sys.executable, "-B", str(ROOT / "voicemail.py")],
            input=events, text=True, capture_output=True, timeout=10, cwd=ROOT,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, "")
        return result.stdout.splitlines()

    def test_save_with_hash_replays_new_greeting(self):
        """Interaction 1: save with #, hang up, reconnect, hear the new greeting."""
        events = (ROOT / "scenarios" / "test_main_flow.txt").read_text(encoding="utf-8")
        lines = self.run_session(events)
        self.assertIn(GREETING_PROMPT, lines)
        self.assertEqual(lines.count(DEFAULT_GREETING), 1)
        self.assertEqual(lines[-2:], [INITIAL_PROMPT, SAVED_GREETING])
        self.assertEqual(lines.count("Enter 3 to change your greeting"), 2)

    def test_hangup_before_hash_keeps_original_greeting(self):
        """Interaction 2: hang up before #, reconnect, hear the old greeting."""
        events = (ROOT / "scenarios" / "test_hang_up.txt").read_text(encoding="utf-8")
        lines = self.run_session(events)
        self.assertIn(GREETING_PROMPT, lines)
        self.assertEqual(lines.count(DEFAULT_GREETING), 2)
        self.assertEqual(lines[-2:], [INITIAL_PROMPT, DEFAULT_GREETING])
        self.assertNotIn("This greeting should never be saved.", lines)

    def test_cancel_replacement_preserves_previously_saved_greeting(self):
        """Cancellation must preserve a custom greeting, not reset to the default."""
        events = "\n".join([
            "1", "3", "#", "1", "3", "#", "3", "Previously saved greeting.", "#",
            "3", "Discard this replacement.", "H", "1", "3", "#", "Q", "",
        ])
        lines = self.run_session(events)
        self.assertEqual(lines.count(GREETING_PROMPT), 2)
        self.assertEqual(lines[-2:], [INITIAL_PROMPT, "Previously saved greeting."])
        self.assertNotIn("Discard this replacement.", lines)


class GuidedManualChecks(unittest.TestCase):
    def run_guide(self, action, answers):
        result = subprocess.run(
            [sys.executable, "-B", str(ROOT / "lab.py"), action],
            input=answers, text=True, capture_output=True, timeout=10, cwd=ROOT.parent,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("STEP 2 - Log in as the mailbox owner", result.stdout)
        self.assertIn("STEP 6 - Call back and check what actually plays", result.stdout)
        self.assertIn("PASS:", result.stdout)
        return result.stdout

    def test_guided_save_checks_the_users_actual_greeting(self):
        """The manual save guide replays the custom text entered by its user."""
        output = self.run_guide("guide-save", "13\n13\n3\nMy manually entered greeting.\n#\n\n")
        self.assertIn("Actual:   My manually entered greeting.", output)

    def test_guided_cancel_checks_that_the_old_greeting_plays(self):
        """The manual cancellation guide replays the original greeting."""
        output = self.run_guide("guide-cancel", "\n\n\nDiscard this manually entered greeting.\nH\n\n")
        self.assertIn("Actual:   " + DEFAULT_GREETING, output)
        self.assertNotIn("Actual:   Discard this manually entered greeting.", output)


if __name__ == "__main__":
    unittest.main(verbosity=2)
