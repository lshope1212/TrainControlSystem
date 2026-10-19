"""Guided manual tests for greeting changes."""
import contextlib
import io

from voicemail import Connection, MAILBOX_MENU, MailSystem, Telephone


class Cancelled(Exception):
    pass


def ask(prompt, default=None, allowed=None):
    while True:
        value = input(prompt).strip()
        if value.upper() == "Q":
            raise Cancelled()
        if not value and default is not None:
            value = default
        if value and (allowed is None or value.upper() in allowed):
            return value
        if allowed:
            print("For this walkthrough, enter " + " or ".join(sorted(allowed)) + ", or Q to stop.")
        else:
            print("Enter a value, or Q to stop.")


def show_program(output):
    for line in output.splitlines():
        print("  Program: " + line)


def run_guided(action):
    saving = action == "guide-save"
    title = "SAVE A NEW GREETING" if saving else "CANCEL A NEW GREETING"
    print("\nMANUAL TEST: " + title)
    print("Follow one prompt at a time. You choose the greeting and perform the action.")
    print("Press Enter after each answer. Q stops the walkthrough.")
    print("This starts a fresh voicemail session. Mailbox 13's initial passcode is 13.")
    phone = Telephone(io.StringIO())
    system = MailSystem(20)
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        connection = Connection(system, phone)

    def send(events):
        recorded = io.StringIO()
        phone.scanner = io.StringIO("\n".join(events) + "\n")
        with contextlib.redirect_stdout(recorded):
            phone.run(connection)
        text = recorded.getvalue()
        show_program(text)
        return text.rstrip("\r\n")

    try:
        print("\nSTEP 1 - Call a mailbox")
        show_program(output.getvalue())
        while True:
            extension = ask("Mailbox number [13] - enter the whole number here: ", default="13")
            if extension.isascii() and extension.isdigit() and 1 <= int(extension) <= 20:
                break
            print("Choose a mailbox number from 1 to 20.")
        original = system.findMailbox(extension).getGreeting()
        send(list(extension) + ["#"])

        print("\nSTEP 2 - Log in as the mailbox owner")
        print("You have selected the mailbox; now enter its passcode to edit the greeting.")
        while connection.state != MAILBOX_MENU:
            password = ask("Passcode [" + str(int(extension)) + "]: ", default=str(int(extension)))
            if not (password.isascii() and password.isdigit()):
                print("The passcode contains digits only.")
                continue
            send(list(password) + ["#"])

        print("\nSTEP 3 - Choose change greeting")
        print("This example implements menu option 3.")
        choice = ask("Menu choice [3]: ", default="3", allowed={"3"})
        send([choice])

        print("\nSTEP 4 - Record your replacement greeting")
        while True:
            greeting = ask("Type a sentence for your new greeting: ")
            if greeting.upper() == "H" or (len(greeting) == 1 and greeting in "0123456789#"):
                print("That is a phone command. Type a sentence, such as Hello, this is Alex.")
            elif greeting == original:
                print("Choose a different greeting so the result of this test is visible.")
            else:
                break
        send([greeting])
        print("Your recording is pending. It has NOT been saved yet.")

        print("\nSTEP 5 - " + ("Save with #" if saving else "Hang up without saving"))
        print("Old greeting: " + original)
        print("Pending recording: " + greeting)
        command = "#" if saving else "H"
        prompt = "Type # to save the new greeting: " if saving else "Type H to discard the recording and hang up: "
        entered = ask(prompt, allowed={command})
        send([entered])

        print("\nSTEP 6 - Call back and check what actually plays")
        expected = greeting if saving else original
        print("Expected greeting: " + expected)
        prompt = "Press Enter to hang up and call the same mailbox again (Q stops): " if saving else "Press Enter to call the same mailbox again (Q stops): "
        while True:
            reply = input(prompt).strip()
            if reply.upper() == "Q":
                raise Cancelled()
            if not reply:
                break
            print("Just press Enter to call back.")
        if saving:
            send(["H"])
        actual = send(list(extension) + ["#"])
        print("\nExpected: " + expected)
        print("Actual:   " + actual)
        if actual != expected:
            print("FAIL: the greeting played after reconnecting does not match the expected result.")
            return 1
        if saving:
            print("PASS: pressing # saved your greeting, and it played after reconnecting.")
        else:
            print("PASS: hanging up before # kept the old greeting and discarded your recording.")
        return 0
    except Cancelled:
        print("Walkthrough stopped. No test result was recorded.")
        return 0
