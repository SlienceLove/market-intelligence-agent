#!/usr/bin/env python3
"""Restricted local TTS HTTP sidecar for the media API."""

from __future__ import annotations

import argparse
import hmac
import json
import logging
import math
import threading
import wave
from array import array
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlsplit


LOGGER = logging.getLogger("media.tts_sidecar")


class RequestError(Exception):
    def __init__(self, status: HTTPStatus, code: str) -> None:
        super().__init__(code)
        self.status = status
        self.code = code


@dataclass(frozen=True)
class Settings:
    allowed_root: Path
    backend: str
    model_dir: Path | None
    api_key: str | None
    api_key_header: str
    max_request_bytes: int
    max_text_length: int
    max_segment_length: int
    max_segments: int
    max_total_duration_seconds: float
    segment_timeout_seconds: float
    sample_rate: int
    output_format: str


def resolve_temp_media_path(uri: Any, allowed_root: Path, require_exists: bool) -> Path:
    if not isinstance(uri, str) or not uri:
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

    parsed = urlsplit(uri)
    if (
        parsed.scheme != "temp"
        or parsed.netloc != "media"
        or parsed.query
        or parsed.fragment
        or parsed.username
        or parsed.password
    ):
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

    relative = unquote(parsed.path).lstrip("/")
    if (
        not relative
        or "\\" in relative
        or ":" in relative
        or relative.startswith("../")
        or "/../" in f"/{relative}/"
        or "/./" in f"/{relative}/"
    ):
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

    root = allowed_root.resolve(strict=True)
    candidate = (root / relative).resolve(strict=False)
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input") from error

    if require_exists and not candidate.is_file():
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

    return candidate


def estimate_duration_seconds(text: str) -> float:
    return max(0.5, min(8.0, len(text.strip()) / 12.0))


def synthesize_placeholder(text: str, output_path: Path, sample_rate: int) -> tuple[float, int]:
    duration_seconds = estimate_duration_seconds(text)
    frame_count = max(1, int(round(duration_seconds * sample_rate)))
    frequency = 220 + (sum(ord(character) for character in text) % 220)
    amplitude = 2_000
    samples = array(
        "h",
        (
            int(amplitude * math.sin((2.0 * math.pi * frequency * index) / sample_rate))
            for index in range(frame_count)
        ),
    )
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(output_path), "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(sample_rate)
        writer.writeframes(samples.tobytes())
    return duration_seconds, output_path.stat().st_size


class PlaceholderBackend:
    backend = "placeholder"

    def synthesize(self, text: str, output_path: Path, settings: Settings) -> dict[str, Any]:
        if len(text) > settings.max_text_length:
            raise RequestError(HTTPStatus.BAD_REQUEST, "text_too_long")
        duration_seconds = estimate_duration_seconds(text)
        if duration_seconds > settings.segment_timeout_seconds:
            raise RequestError(HTTPStatus.REQUEST_TIMEOUT, "timeout")
        duration_seconds, byte_count = synthesize_placeholder(text, output_path, settings.sample_rate)
        return {
            "backend": self.backend,
            "durationSeconds": round(duration_seconds, 3),
            "sampleRate": settings.sample_rate,
            "bytes": byte_count,
        }


class SherpaBackend:
    backend = "sherpa"

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._model: Any = None
        self._lock = threading.Lock()
        if settings.model_dir is None:
            raise RequestError(HTTPStatus.INTERNAL_SERVER_ERROR, "backend_not_configured")

    def synthesize(self, text: str, output_path: Path, settings: Settings) -> dict[str, Any]:
        raise RequestError(HTTPStatus.INTERNAL_SERVER_ERROR, "backend_not_configured")


class TtsRequestHandler(BaseHTTPRequestHandler):
    server: "TtsServer"
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/health" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return

        self._write_json(
            HTTPStatus.OK,
            {"status": "ready", "backend": self.server.backend_name, "backendConfigured": self.server.backend_ready},
        )

    def do_POST(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/v1/speech-synthesis" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return

        try:
            self._authorize()
            payload = self._read_payload()
            job_id, output_format, sample_rate, segments = self._validate_payload(payload)
            if not self.server.capacity.acquire(blocking=False):
                raise RequestError(HTTPStatus.TOO_MANY_REQUESTS, "synthesis_failed")

            try:
                estimated_total_duration = sum(estimate_duration_seconds(segment["text"]) for segment in segments)
                if estimated_total_duration > self.server.settings.max_total_duration_seconds:
                    raise RequestError(HTTPStatus.BAD_REQUEST, "text_too_long")

                response_segments = []
                total_duration = 0.0
                for segment in segments:
                    result = self.server.backend.synthesize(segment["text"], segment["path"], self.server.settings)
                    total_duration += result["durationSeconds"]
                    response_segments.append({
                        "index": segment["index"],
                        "outputUri": segment["outputUri"],
                        "durationSeconds": result["durationSeconds"],
                        "sampleRate": result["sampleRate"],
                        "bytes": result["bytes"],
                        "backend": result["backend"],
                    })
            finally:
                self.server.capacity.release()

            backend = response_segments[0]["backend"] if response_segments else self.server.backend_name
            LOGGER.info(
                "tts completed job_id=%s segments=%s backend=%s",
                job_id,
                len(response_segments),
                backend,
            )
            self._write_json(
                HTTPStatus.OK,
                {
                    "jobId": job_id,
                    "status": "succeeded",
                    "backend": backend,
                    "outputFormat": output_format,
                    "sampleRate": sample_rate,
                    "totalDurationSeconds": round(total_duration, 3),
                    "segments": response_segments,
                },
            )
        except RequestError as error:
            self._write_json(error.status, {"error": error.code})
        except Exception:
            LOGGER.exception("tts failed")
            self._write_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": "synthesis_failed"})

    def _authorize(self) -> None:
        expected = self.server.settings.api_key
        if not expected:
            return

        provided = self.headers.get(self.server.settings.api_key_header)
        if provided is None or not hmac.compare_digest(provided, expected):
            raise RequestError(HTTPStatus.UNAUTHORIZED, "unauthorized")

    def _read_payload(self) -> dict[str, Any]:
        content_length = self.headers.get("Content-Length")
        if content_length is None:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        try:
            length = int(content_length)
        except ValueError as error:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input") from error

        if length <= 0 or length > self.server.settings.max_request_bytes:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        body = self.rfile.read(length)
        if len(body) != length:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        try:
            decoded_body = body.decode("utf-8")
        except UnicodeDecodeError as error:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input") from error

        try:
            payload = json.loads(decoded_body)
        except json.JSONDecodeError as error:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input") from error

        if not isinstance(payload, dict):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        return payload

    def _validate_payload(self, payload: dict[str, Any]) -> tuple[str, str, int, list[dict[str, Any]]]:
        job_id = payload.get("jobId")
        if not isinstance(job_id, str) or not job_id.strip() or len(job_id) > 128:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        output_format = payload.get("outputFormat")
        if not isinstance(output_format, str) or output_format.lower() != self.server.settings.output_format:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        sample_rate = payload.get("sampleRate")
        if sample_rate != self.server.settings.sample_rate:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        segments = payload.get("segments")
        if not isinstance(segments, list) or not segments:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        if len(segments) > self.server.settings.max_segments:
            raise RequestError(HTTPStatus.BAD_REQUEST, "text_too_long")

        parsed_segments: list[dict[str, Any]] = []
        total_text_length = 0
        expected_index = 0
        for segment in segments:
            if not isinstance(segment, dict):
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

            index = segment.get("index")
            text = segment.get("text")
            output_uri = segment.get("outputUri")
            if index != expected_index or not isinstance(text, str) or not text.strip() or not isinstance(output_uri, str):
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

            if len(text) > self.server.settings.max_segment_length:
                raise RequestError(HTTPStatus.BAD_REQUEST, "text_too_long")

            total_text_length += len(text)
            if total_text_length > self.server.settings.max_text_length:
                raise RequestError(HTTPStatus.BAD_REQUEST, "text_too_long")

            path = resolve_temp_media_path(output_uri, self.server.settings.allowed_root, require_exists=False)
            parsed_segments.append({"index": index, "text": text, "outputUri": output_uri, "path": path})
            expected_index += 1

        return job_id.strip(), output_format.lower(), int(sample_rate), parsed_segments

    def _write_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        content = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(content)


class TtsServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: tuple[str, int], settings: Settings) -> None:
        super().__init__(address, TtsRequestHandler)
        self.settings = settings
        self.backend = PlaceholderBackend() if settings.backend == "placeholder" else SherpaBackend(settings)
        self.backend_name = self.backend.backend
        self.backend_ready = settings.backend == "placeholder"
        self.capacity = threading.BoundedSemaphore(1)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allowed-root", required=True, help="Filesystem root mapped to temp://media/.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8093)
    parser.add_argument("--backend", default="placeholder", choices=("placeholder", "sherpa"))
    parser.add_argument("--model-dir", default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--api-key-header", default="X-Agent-Api-Key")
    parser.add_argument("--max-request-bytes", type=int, default=64 * 1024)
    parser.add_argument("--max-text-length", type=int, default=10_000)
    parser.add_argument("--max-segment-length", type=int, default=800)
    parser.add_argument("--max-segments", type=int, default=64)
    parser.add_argument("--max-total-duration-seconds", type=float, default=600)
    parser.add_argument("--segment-timeout-seconds", type=float, default=30)
    parser.add_argument("--sample-rate", type=int, default=16_000)
    parser.add_argument("--output-format", default="wav")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.host not in {"127.0.0.1", "::1", "localhost"}:
        raise SystemExit("TTS sidecar only supports loopback hosts")

    root = Path(args.allowed_root)
    if not root.is_dir():
        raise SystemExit("allowed root must be an existing directory")

    model_dir = Path(args.model_dir) if args.model_dir else None
    if args.backend == "sherpa" and model_dir is None:
        raise SystemExit("sherpa backend requires --model-dir")

    settings = Settings(
        allowed_root=root.resolve(),
        backend=args.backend,
        model_dir=model_dir.resolve() if model_dir is not None else None,
        api_key=args.api_key or None,
        api_key_header=args.api_key_header,
        max_request_bytes=args.max_request_bytes,
        max_text_length=args.max_text_length,
        max_segment_length=args.max_segment_length,
        max_segments=args.max_segments,
        max_total_duration_seconds=args.max_total_duration_seconds,
        segment_timeout_seconds=args.segment_timeout_seconds,
        sample_rate=args.sample_rate,
        output_format=args.output_format.lower(),
    )

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    server = TtsServer((args.host, args.port), settings)
    LOGGER.info("TTS sidecar listening host=%s port=%s backend=%s", args.host, args.port, server.backend_name)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
