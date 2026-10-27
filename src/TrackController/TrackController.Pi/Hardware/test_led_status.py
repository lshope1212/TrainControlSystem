from datetime import datetime, timedelta, timezone
import unittest
from led_status import indication


class IndicationTests(unittest.TestCase):
    def setUp(self):
        self.now = datetime.now(timezone.utc)
        self.snapshot = {"messageType": "WaysideSnapshotMessage", "payload": {
            "evaluatedAtUtc": self.now.isoformat(), "validUntilUtc": (self.now + timedelta(seconds=1)).isoformat(),
            "paused": False, "maintenanceMode": False, "blocks": [{"fresh": True, "faulted": False}],
            "authorizations": [{"authorizedAuthorityMeters": 100, "authorizedSpeedMetersPerSecond": 5}]}}

    def test_positive_current_output(self):
        self.assertEqual((False, True, False), indication(self.snapshot, self.now))

    def test_stale_missing_and_malformed_never_show_green(self):
        for value, now in [(self.snapshot, self.now + timedelta(seconds=2)), ({}, self.now), (None, self.now)]:
            self.assertEqual((True, False, True), indication(value, now))

    def test_paused_or_faulted(self):
        self.snapshot["payload"]["paused"] = True
        self.assertEqual((True, False, False), indication(self.snapshot, self.now))
        self.snapshot["payload"]["blocks"][0]["faulted"] = True
        self.assertEqual((True, False, True), indication(self.snapshot, self.now))


if __name__ == "__main__":
    unittest.main()
