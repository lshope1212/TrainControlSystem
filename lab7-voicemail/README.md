# Lab 7: UML Telephone Example - Change the Greeting

A standalone command-line voicemail example for two interactions:

1. Record a new greeting and press `#` to save it.
2. Hang up before pressing `#`; the previous greeting stays unchanged.

The folder includes the original StarUML project, its two sequence previews,
the Python implementation, replay inputs, and automated tests. Python 3.8 or
newer is required. There are no third-party packages to install.

## Quick start on Windows

1. Extract the ZIP before running anything.
2. Open the extracted `lab7-voicemail` folder.
3. Double-click **`lab.cmd`**.
4. Choose **1** to manually test saving a greeting. The program explains each
   step, asks for your own greeting, and checks what plays when you call back.
5. Choose **2** to manually test canceling a greeting. It prompts you to hang
   up without saving, then checks that the original greeting still plays.
6. Choose **3** for automated checks, or **0** to exit.

**Start with options 1 and 2.** They are guided manual tests of the two sequence
diagrams. Follow the prompts in the window; you do not need to memorize the
keystrokes below. Option 4 is the original, unguided keypad interface.

In the guided tests, a bracketed value such as `[13]` is a default: press Enter
to use it. You can enter the whole mailbox number or passcode in one answer.
The guide sends the corresponding individual key events to the original
program. It uses the real `Telephone`, `Connection`, `MailSystem`, and `Mailbox`
objects, and compares the actual greeting played after reconnecting with the
expected greeting. It does not simulate a canned success result.

| Manual test | Action you perform | Expected result when calling back |
| --- | --- | --- |
| 1: Save | Record a greeting, then type `#` when prompted. | Your new greeting plays. |
| 2: Cancel | Record a greeting, then type `H` when prompted. | The previous greeting still plays. |

Each walkthrough starts a fresh session, so the cancellation walkthrough starts
with the default greeting. An additional automated check covers cancellation
after a custom greeting has already been saved.

The launcher automatically looks for Python. It also recognizes the bundled
Codex Python runtime when available on the machine; Codex is not required on
other machines with Python installed. If it cannot locate Python, install
Python 3 or set `VOICEMAIL_PYTHON` to the full path of your Python executable.

From PowerShell, opened in this folder:

```powershell
.\lab.cmd guide-save
.\lab.cmd guide-cancel
.\lab.cmd test
.\lab.cmd demo-save
.\lab.cmd demo-cancel
.\lab.cmd run
```

These commands also work when the folder is inside a larger repository. No
PowerShell execution-policy change or administrator access is needed.

## macOS, Linux, or an existing Python terminal

```sh
python3 lab.py guide-save
python3 lab.py guide-cancel
python3 lab.py test
python3 lab.py demo-save
python3 lab.py demo-cancel
python3 lab.py run
```

Use `python` instead of `python3` if that is your Python 3 command. Running
`python3 lab.py` with no argument opens the same menu as the Windows launcher.
Tests exit with code 0 on success and a nonzero code on failure.

## Optional: manually enter every key in the original interface

This section is for menu option 4. The guided options 1 and 2 are easier for a
first-time tester. The original interface has no password prompt after playing
the greeting, and it expects one keypad character per line.

### Save a greeting in original keypad mode

Start interactive mode with menu option 4 or `lab.cmd run`. Type each entry
below on its own line and press Enter after every line. A key such as `#` is
ordinary text here: type it, then press Enter.

```text
1
3
#
1
3
#
3
Hello, this is my new greeting.
#
H
1
3
#
Q
```

What happens:

| Inputs | Meaning | Expected result |
| --- | --- | --- |
| `1`, `3`, `#` | Select mailbox 13 | Original greeting plays. |
| `1`, `3`, `#` | Enter passcode 13 | Mailbox menu appears. |
| `3` | Select change greeting | Recording prompt appears. |
| `Hello, this is my new greeting.` | Speak the new greeting | Text is held until confirmation. |
| `#` | Confirm | New greeting is saved; mailbox menu returns. |
| `H` | Hang up | Initial mailbox prompt returns. |
| `1`, `3`, `#` | Reconnect to mailbox 13 | `Hello, this is my new greeting.` plays. |
| `Q` | Quit the session | Program ends, or returns to the launcher menu. |

Do not type `13` on a single line: each keypad digit is a separate input event.

### Cancel a greeting in original keypad mode

Start a fresh interactive session and enter:

```text
1
3
#
1
3
#
3
This greeting should never be saved.
H
1
3
#
Q
```

You should hear `You have reached mailbox 13. Please leave a message now.`
after reconnecting. There is no `#` between the replacement text and `H`, so
the new recording is discarded. The automated tests also check cancellation
after an earlier custom greeting has already been saved.

## Input rules and scope

- A single `0`-`9` or `#`, followed by Enter, is a keypad event.
- `H`, followed by Enter, hangs up; `Q`, followed by Enter, quits.
- Other nonempty lines are recorded as speech when recording is active.
- Mailboxes 1-20 exist. Each initial passcode equals its mailbox number.
- Greetings survive hang-ups within one running session. They are kept in
  memory and reset when you restart the program.
- This lab implements reaching a mailbox, logging in, and changing a greeting.
  Menu options 1 and 2 (message retrieval and passcode changes) are outside this
  example and display an explanatory message.

## Files and diagrams

```text
lab7-voicemail/
  README.md
  .gitignore
  lab.cmd                      Windows launcher
  lab.py                       Menu, demonstrations, and test runner
  guided.py                    Prompts for the two manual walkthroughs
  voicemail.py                 Four-class implementation
  scenarios/
    test_main_flow.txt
    test_hang_up.txt
  tests/
    test_voicemail.py          Three application checks and two guide checks
  uml/
    VoiceMail_ChangeGreeting.mdj
    sequence-1-main-flow-preview.png
    sequence-2-hang-up-preview.png
```

Open `uml/VoiceMail_ChangeGreeting.mdj` in StarUML to view or edit:

- `Class Diagram - Voice Mail`
- `Change Greeting - Main Flow`
- `Change Greeting - Hang Up Before #`

The class diagram models `Telephone`, `Connection`, `MailSystem`, and `Mailbox`.
Both sequence diagrams state that the owner has already reached an extension
and logged in. The implementation behavior is preserved from the supplied
OneDrive code; its introductory run instructions were updated for this layout.

## How this relates to the course example

The provided course handout, `Voice Mail vFc.pdf`, explains the input convention
on page 3, reaching an extension on page 4, logging in on page 5, and changing
a greeting with its cancellation variation on page 8. Those are high-level
use-case descriptions, not a complete on-screen testing guide.

The course archive's seven Java source files form a fuller reference program:
`MailSystemTester.java` starts it; `Telephone.java` handles terminal input and
output; `Connection.java` tracks the call and menus; `MailSystem.java` finds
mailboxes; `Mailbox.java` manages each mailbox; `Message.java` and
`MessageQueue.java` store voicemail messages. The `.java.html` files are HTML
views of the corresponding source files.

This Python submission implements the four classes needed for the greeting
scenarios. It does not need the Java files to run. The two sequence diagrams
show one use case's normal flow and its cancellation variation, matching page
8. Reaching a mailbox and logging in are shared prerequisites for both.

## Add to an existing repository

1. Copy the entire **`lab7-voicemail` folder** into the root of your repository.
   Keep the files together inside that folder.
2. From the repository root, run `.\lab7-voicemail\lab.cmd test` on Windows,
   or `python3 lab7-voicemail/lab.py test` on macOS/Linux.
3. Stage and commit only this folder:

```sh
git add lab7-voicemail/
git commit -m "Add standalone Lab 7 voicemail example"
```

4. Push your chosen branch using your repository's normal workflow. For the
   GitHub rubric item, submit the link to the uploaded folder/code.

Upload the extracted folder contents as repository files, rather than only
the ZIP, so the code and diagrams can be inspected directly. This folder does
not need imports, configuration, or dependency changes elsewhere in the repo.
