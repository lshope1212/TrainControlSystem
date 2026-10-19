"""Voice Mail - Change the Greeting.
Input, one per line: 0-9 or # = key, H = hang up, Q = quit, other text = voice.
Run interactively: python voicemail.py
Replay the supplied scenario: python lab.py demo-save
"""
import sys

CONNECTED, RECORDING, MAILBOX_MENU, CHANGE_GREETING = 1, 2, 3, 4
INITIAL_PROMPT = "Enter mailbox number followed by #"
MAILBOX_MENU_TEXT = ("Enter 1 to listen to your messages\n"
                     "Enter 2 to change your passcode\n"
                     "Enter 3 to change your greeting")
GREETING_PROMPT = "Record your greeting, then press the # key"


class Mailbox:
    def __init__(self, passcode: str, greeting: str):
        self.passcode = passcode
        self.greeting = greeting

    def checkPasscode(self, passcode: str) -> bool:
        return self.passcode == passcode

    def getGreeting(self) -> str:
        return self.greeting

    def setGreeting(self, newGreeting: str):
        self.greeting = newGreeting


class MailSystem:
    def __init__(self, count: int):
        self.mailboxes = [Mailbox(str(i), f"You have reached mailbox {i}. Please leave a message now.")
                          for i in range(1, count + 1)]

    def findMailbox(self, ext: str) -> Mailbox:
        return self.mailboxes[int(ext) - 1] if ext.isdigit() and 1 <= int(ext) <= len(self.mailboxes) else None


class Telephone:
    def __init__(self, scanner):
        self.scanner = scanner

    def speak(self, output: str):
        print(output)

    def run(self, c: "Connection"):
        for line in self.scanner:
            s = line.strip()
            if not s:
                continue
            if s.upper() == "Q":
                return
            if s.upper() == "H":
                c.hangup()
            elif len(s) == 1 and s in "0123456789#":
                c.dial(s)
            else:
                c.record(s)


class Connection:
    def __init__(self, system: MailSystem, phone: Telephone):
        self.system, self.phone = system, phone
        self.resetConnection()
        phone.speak(INITIAL_PROMPT)

    def dial(self, key: str):
        {CONNECTED: self.connect, RECORDING: self.login,
         MAILBOX_MENU: self.mailboxMenu, CHANGE_GREETING: self.changeGreeting}[self.state](key)

    def record(self, voice: str):
        if self.state in (RECORDING, CHANGE_GREETING):
            self.currentRecording += voice

    def hangup(self):
        self.resetConnection()
        self.phone.speak(INITIAL_PROMPT)

    def resetConnection(self):
        self.currentMailbox, self.currentRecording, self.accumulatedKeys, self.state = None, "", "", CONNECTED

    def connect(self, key: str):
        if key != "#":
            self.accumulatedKeys += key
            return
        self.currentMailbox = self.system.findMailbox(self.accumulatedKeys)
        self.accumulatedKeys = ""
        if self.currentMailbox:
            self.state = RECORDING
            self.phone.speak(self.currentMailbox.getGreeting())
        else:
            self.phone.speak("Incorrect mailbox number. Try again!")

    def login(self, key: str):
        if key != "#":
            self.accumulatedKeys += key
            return
        if self.currentMailbox.checkPasscode(self.accumulatedKeys):
            self.state = MAILBOX_MENU
            self.phone.speak(MAILBOX_MENU_TEXT)
        else:
            self.phone.speak("Incorrect passcode. Try again!")
        self.accumulatedKeys = ""

    def mailboxMenu(self, key: str):
        if key == "3":
            self.state, self.currentRecording = CHANGE_GREETING, ""
            self.phone.speak(GREETING_PROMPT)
        else:
            self.phone.speak("Only option 3 (change greeting) is in this prototype.\n" + MAILBOX_MENU_TEXT)

    def changeGreeting(self, key: str):
        if key == "#":
            self.currentMailbox.setGreeting(self.currentRecording)
            self.currentRecording, self.state = "", MAILBOX_MENU
            self.phone.speak(MAILBOX_MENU_TEXT)


if __name__ == "__main__":
    phone = Telephone(sys.stdin)
    phone.run(Connection(MailSystem(20), phone))
