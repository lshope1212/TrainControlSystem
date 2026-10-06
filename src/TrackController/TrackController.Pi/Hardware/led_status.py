#!/usr/bin/env python3
"""Optional Pi 5 indication LEDs. These indicate controller state; they are not track actuators."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import time


def indication(envelope, now):
    """Return (red, green, amber). Invalid/missing/expired output never lights green."""
    try:
        if envelope["messageType"] != "WaysideSnapshotMessage":
            return True, False, True
        snapshot = envelope["payload"]
        valid_until = datetime.fromisoformat(snapshot["validUntilUtc"].replace("Z", "+00:00"))
        evaluated = datetime.fromisoformat(snapshot["evaluatedAtUtc"].replace("Z", "+00:00"))
        if not evaluated <= now < valid_until or (valid_until - evaluated).total_seconds() > 1.5:
            return True, False, True
        blocks = snapshot["blocks"]
        attention = not blocks or any(not b["fresh"] or b["faulted"] for b in blocks)
        permitted = (not snapshot["paused"] and not snapshot["maintenanceMode"] and not attention
                     and any(a["authorizedAuthorityMeters"] > 0 and a["authorizedSpeedMetersPerSecond"] > 0
                             for a in snapshot["authorizations"]))
        return not permitted, permitted, attention
    except (KeyError, ValueError, TypeError, OverflowError):
        return True, False, True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--status-file", default="status.json")
    parser.add_argument("--console", action="store_true", help="Print LED states without requiring GPIO")
    parser.add_argument("--chip", type=int, default=None, help="Optional lgpio gpiochip override; normally auto-detected")
    args = parser.parse_args()
    leds = []
    factory = None
    if not args.console:
        from gpiozero import LED
        from gpiozero.pins.lgpio import LGPIOFactory
        factory = LGPIOFactory() if args.chip is None else LGPIOFactory(chip=args.chip)
        # BCM numbering: physical header pins 11, 13, 15. Each LED needs its own series resistor.
        leds = [LED(pin, pin_factory=factory, initial_value=(pin == 17)) for pin in (17, 27, 22)]
    previous = None
    try:
        while True:
            try:
                envelope = json.loads(Path(args.status_file).read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError, UnicodeError):
                envelope = {}
            state = indication(envelope, datetime.now(timezone.utc))
            for led, enabled in zip(leds, state):
                led.value = enabled
            if state != previous:
                print(f"Red/stop={int(state[0])} Green/permitted={int(state[1])} Amber/attention={int(state[2])}", flush=True)
                previous = state
            time.sleep(0.1)
    except KeyboardInterrupt:
        pass
    finally:
        for led in leds:
            led.close()
        if factory is not None:
            factory.close()


if __name__ == "__main__":
    main()
