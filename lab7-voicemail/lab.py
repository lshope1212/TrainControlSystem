"""Command-line launcher and test runner."""
import argparse
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parent
PROGRAM = [sys.executable, "-B", str(ROOT / "voicemail.py")]
SCENARIOS = {
    "demo-save": ("Save a new greeting with #", "test_main_flow.txt"),
    "demo-cancel": ("Hang up before # and keep the old greeting", "test_hang_up.txt"),
}


def execute(action):
    if action in ("guide-save", "guide-cancel"):
        from guided import run_guided
        return run_guided(action)
    if action == "test":
        print("Checking both greeting interactions and cancellation after a prior save...", flush=True)
        result = subprocess.run(
            [sys.executable, "-B", "-m", "unittest", "discover",
             "-s", str(ROOT / "tests"), "-p", "test_*.py", "-v"],
            cwd=ROOT,
            check=False,
        )
        print("\nPASS: all tests passed." if result.returncode == 0 else "\nFAIL: see the test results above.", flush=True)
        return result.returncode
    if action in SCENARIOS:
        title, filename = SCENARIOS[action]
        events = (ROOT / "scenarios" / filename).read_text(encoding="utf-8")
        print("\n" + title)
        print("Input events (one per line):\n" + events.rstrip())
        print("\nSystem output:", flush=True)
        return subprocess.run(PROGRAM, input=events, text=True, cwd=ROOT,
                              check=False, timeout=10).returncode
    print("\nInteractive mode: enter one key or line of speech, then press Enter.")
    print("Use H to hang up; Q to end this session.")
    print("Enter each mailbox digit separately, then #. After the greeting, enter the passcode the same way.\n", flush=True)
    return subprocess.run(PROGRAM, cwd=ROOT, check=False).returncode


def menu():
    actions = {"1": "guide-save", "2": "guide-cancel", "3": "test", "4": "run"}
    while True:
        print("\nLab 7 - Voice Mail: Change the Greeting")
        print("1. Test saving a greeting yourself - guided step by step")
        print("2. Test canceling a greeting yourself - guided step by step")
        print("3. Run automatic checks - no typing needed")
        print("4. Manual keypad input")
        print("0. Exit")
        choice = input("Choose 0-4: ").strip()
        if choice == "0":
            return 0
        if choice in actions:
            execute(actions[choice])
        else:
            print("Please choose a number from 0 to 4.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", nargs="?", choices=["guide-save", "guide-cancel", "test", "demo-save", "demo-cancel", "run"],
                        help="Omit this argument to open the menu.")
    args = parser.parse_args()
    try:
        return execute(args.action) if args.action else menu()
    except (EOFError, KeyboardInterrupt):
        print("\nSession ended.")
        return 0
    except (OSError, subprocess.TimeoutExpired) as error:
        print("Could not run the lab: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
