#!/usr/bin/env python3
"""Contract tests for the local TTS sidecar."""

from __future__ import annotations

import contextlib
import json
import tempfile
import threading
import time
import urllib.error
import urllib.request
import unittest
import wave
from dataclasses import replace
from http import HTTPStatus
from pathlib import Path

from tts_sidecar import PlaceholderBackend, Settings, TtsServer, RequestError, resolve_temp_media_path


class TtsSidecarTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_rejects_unauthorized_requests(self) -> None:
        with self.run_server(api_key="secret") as server:
            server.backend = RecordingBackend()
            status, body = self.post_json(server, self.valid_payload(), headers={})

        self.assertEqual(HTTPStatus.UNAUTHORIZED, status)
        self.assertEqual({"error": "unauthorized"}, json.loads(body))
        self.assertEqual(0, server.backend.calls)

    def test_rejects_path_traversal_output_uri(self) -> None:
        with self.run_server(api_key="secret") as server:
            server.backend = RecordingBackend()
            payload = self.valid_payload()
            payload["segments"][0]["outputUri"] = "temp://media/%2e%2e/secret.wav"
            status, body = self.post_json(server, payload, headers={"X-Agent-Api-Key": "secret"})

        self.assertEqual(HTTPStatus.BAD_REQUEST, status)
        self.assertEqual({"error": "invalid_input"}, json.loads(body))
        self.assertEqual(0, server.backend.calls)

    def test_rejects_overlong_text(self) -> None:
        with self.run_server(api_key="secret", max_text_length=8) as server:
            server.backend = RecordingBackend()
            payload = self.valid_payload(text="123456789")
            status, body = self.post_json(server, payload, headers={"X-Agent-Api-Key": "secret"})

        self.assertEqual(HTTPStatus.BAD_REQUEST, status)
        self.assertEqual({"error": "text_too_long"}, json.loads(body))
        self.assertEqual(0, server.backend.calls)

    def test_rejects_malformed_utf8_as_invalid_input(self) -> None:
        with self.run_server(api_key="secret") as server:
            server.backend = RecordingBackend()
            status, body = self.post_bytes(
                server,
                b'{"jobId":"\xff"}',
                headers={"X-Agent-Api-Key": "secret"},
            )

        self.assertEqual(HTTPStatus.BAD_REQUEST, status)
        self.assertEqual({"error": "invalid_input"}, json.loads(body))
        self.assertEqual(0, server.backend.calls)

    def test_placeholder_backend_emits_valid_wav_header(self) -> None:
        with self.run_server(api_key="secret") as server:
            payload = self.valid_payload(text="hello world")
            status, body = self.post_json(server, payload, headers={"X-Agent-Api-Key": "secret"})

        self.assertEqual(HTTPStatus.OK, status)
        response = json.loads(body)
        self.assertEqual("placeholder", response["backend"])
        output_uri = response["segments"][0]["outputUri"]
        output_path = resolve_temp_media_path(output_uri, self.root, require_exists=True)
        with wave.open(str(output_path), "rb") as reader:
            self.assertEqual(1, reader.getnchannels())
            self.assertEqual(2, reader.getsampwidth())
            self.assertEqual(16_000, reader.getframerate())
            self.assertGreater(reader.getnframes(), 0)

    @contextlib.contextmanager
    def run_server(self, *, api_key: str | None = None, max_text_length: int = 10_000):
        settings = Settings(
            allowed_root=self.root,
            backend="placeholder",
            model_dir=None,
            api_key=api_key,
            api_key_header="X-Agent-Api-Key",
            max_request_bytes=64 * 1024,
            max_text_length=max_text_length,
            max_segment_length=800,
            max_segments=64,
            max_total_duration_seconds=600,
            segment_timeout_seconds=30,
            sample_rate=16_000,
            output_format="wav",
        )
        server = TtsServer(("127.0.0.1", 0), settings)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            time.sleep(0.05)
            yield server
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=1)

    def valid_payload(self, *, text: str = "hello") -> dict[str, object]:
        return {
            "jobId": "job-tts-test",
            "outputFormat": "wav",
            "sampleRate": 16_000,
            "segments": [
                {
                    "index": 0,
                    "text": text,
                    "outputUri": "temp://media/media/job-tts-test/audio-0000.wav",
                }
            ],
        }

    def post_json(self, server: TtsServer, payload: dict[str, object], headers: dict[str, str] | None = None) -> tuple[HTTPStatus, str]:
        return self.post_bytes(server, json.dumps(payload).encode("utf-8"), headers)

    def post_bytes(self, server: TtsServer, body: bytes, headers: dict[str, str] | None = None) -> tuple[HTTPStatus, str]:
        url = f"http://{server.server_address[0]}:{server.server_address[1]}/v1/speech-synthesis"
        request = urllib.request.Request(
            url,
            data=body,
            method="POST",
            headers={"Content-Type": "application/json", **(headers or {})},
        )
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return HTTPStatus(response.status), response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            return HTTPStatus(error.code), error.read().decode("utf-8")


class RecordingBackend:
    backend = "placeholder"

    def __init__(self) -> None:
        self.calls = 0

    def synthesize(self, text: str, output_path: Path, settings: Settings) -> dict[str, object]:
        self.calls += 1
        raise RequestError(HTTPStatus.INTERNAL_SERVER_ERROR, "synthesis_failed")


if __name__ == "__main__":
    unittest.main()
