#!/usr/bin/env python3
"""Unit tests for ASR sidecar URI containment without model dependencies."""

import tempfile
import unittest
from http import HTTPStatus
from pathlib import Path

from asr_sidecar import RequestError, resolve_temp_asset


class ResolveTempAssetTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "clip.wav").write_bytes(b"fixture")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_resolves_media_uri_within_root(self) -> None:
        self.assertEqual(
            self.root / "clip.wav",
            resolve_temp_asset("temp://media/clip.wav", self.root),
        )

    def test_rejects_other_schemes_and_hosts(self) -> None:
        for uri in ("https://example.test/clip.wav", "temp://other/clip.wav", "file:///clip.wav"):
            with self.assertRaises(RequestError) as error:
                resolve_temp_asset(uri, self.root)
            self.assertEqual(HTTPStatus.BAD_REQUEST, error.exception.status)

    def test_rejects_encoded_traversal_and_backslashes(self) -> None:
        for uri in ("temp://media/%2e%2e/secret.wav", "temp://media/nested%5cclip.wav"):
            with self.assertRaises(RequestError) as error:
                resolve_temp_asset(uri, self.root)
            self.assertEqual(HTTPStatus.BAD_REQUEST, error.exception.status)


if __name__ == "__main__":
    unittest.main()
