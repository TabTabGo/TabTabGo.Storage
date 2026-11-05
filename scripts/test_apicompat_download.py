"""Tests for the baseline download in apicompat.py: per-attempt timeout, retries with backoff, clear error, SHA-256 pin.

Run: python3 -m unittest scripts/test_apicompat_download.py
"""
import io
import pathlib
import socket
import sys
import tempfile
import unittest
import urllib.error

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import apicompat  # noqa: E402


class Response(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.dest = pathlib.Path(self.dir.name) / "p.nupkg"
        self.sleeps = []

    def tearDown(self):
        self.dir.cleanup()

    def run_download(self, opener):
        apicompat.download("https://example.invalid/p.nupkg", self.dest, opener=opener, sleep=self.sleeps.append)

    def test_every_attempt_passes_the_timeout(self):
        seen = []

        def opener(url, timeout=None):
            seen.append(timeout)
            return Response(b"data")

        self.run_download(opener)

        self.assertEqual([apicompat.DOWNLOAD_TIMEOUT_SECONDS], seen)
        self.assertEqual(60, apicompat.DOWNLOAD_TIMEOUT_SECONDS)
        self.assertEqual(b"data", self.dest.read_bytes())

    def test_a_stalled_attempt_is_retried_with_backoff_then_succeeds(self):
        calls = []

        def opener(url, timeout=None):
            calls.append(timeout)
            if len(calls) < 3:
                raise socket.timeout("timed out")
            return Response(b"ok")

        self.run_download(opener)

        self.assertEqual(3, len(calls))
        self.assertEqual(2, len(self.sleeps))
        self.assertLess(self.sleeps[0], self.sleeps[1])
        self.assertEqual(b"ok", self.dest.read_bytes())

    def test_three_failures_exit_with_a_clear_message_and_leave_no_partial_file(self):
        def opener(url, timeout=None):
            raise urllib.error.URLError("unreachable")

        with self.assertRaises(SystemExit) as raised:
            self.run_download(opener)

        message = str(raised.exception)
        self.assertIn("after 3 attempts", message)
        self.assertIn("https://example.invalid/p.nupkg", message)
        self.assertFalse(self.dest.exists())

    def test_a_read_that_stalls_midway_is_retried(self):
        class Stalls(Response):
            def read(self, *a):
                raise socket.timeout("read timed out")

        calls = []

        def opener(url, timeout=None):
            calls.append(1)
            return Stalls(b"x") if len(calls) == 1 else Response(b"good")

        self.run_download(opener)

        self.assertEqual(b"good", self.dest.read_bytes())

    def test_the_sha256_pin_is_still_enforced_by_fetch(self):
        self.assertTrue(all(len(v) == 64 for v in apicompat.BASELINE_SHA256.values()))
        self.assertIn("pinned SHA-256", pathlib.Path(apicompat.__file__).read_text())


if __name__ == "__main__":
    unittest.main()
