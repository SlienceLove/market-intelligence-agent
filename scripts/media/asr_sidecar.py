#!/usr/bin/env python3
"""Restricted local faster-whisper HTTP sidecar for the media API."""

from __future__ import annotations

import argparse
import hmac
import json
import logging
import threading
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlsplit


LOGGER = logging.getLogger("media.asr_sidecar")


class RequestError(Exception):
    def __init__(self, status: HTTPStatus, code: str) -> None:
        super().__init__(code)
        self.status = status
        self.code = code


@dataclass(frozen=True)
class Settings:
    allowed_root: Path
    model: str
    model_dir: str | None
    device: str
    compute_type: str
    cpu_threads: int
    api_key: str | None
    api_key_header: str
    max_request_bytes: int
    max_input_bytes: int
    max_duration_seconds: float
    allowed_languages: frozenset[str]


def resolve_temp_asset(uri: Any, allowed_root: Path) -> Path:
    if not isinstance(uri, str) or not uri:
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input_uri")

    parsed = urlsplit(uri)
    if (
        parsed.scheme != "temp"
        or parsed.netloc != "media"
        or parsed.query
        or parsed.fragment
        or parsed.username
        or parsed.password
    ):
        raise RequestError(HTTPStatus.BAD_REQUEST, "unsupported_input_uri")

    relative = unquote(parsed.path).lstrip("/")
    if not relative or "\\" in relative:
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input_uri")

    root = allowed_root.resolve(strict=True)
    candidate = (root / relative).resolve(strict=False)
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input_uri") from error

    if not candidate.is_file():
        raise RequestError(HTTPStatus.NOT_FOUND, "input_not_found")

    return candidate


class FasterWhisperTranscriber:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._model: Any = None
        self._model_lock = threading.Lock()

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def transcribe(self, source: Path, language: str) -> list[dict[str, Any]]:
        model = self._get_model()
        segments, _ = model.transcribe(
            str(source),
            language=language,
            beam_size=5,
            vad_filter=False,
            condition_on_previous_text=False,
        )
        return [
            {
                "startSeconds": round(segment.start, 3),
                "endSeconds": round(segment.end, 3),
                "text": segment.text.strip(),
            }
            for segment in segments
            if segment.end > segment.start and segment.text.strip()
        ]

    def _get_model(self) -> Any:
        with self._model_lock:
            if self._model is None:
                from faster_whisper import WhisperModel

                self._model = WhisperModel(
                    self._settings.model_dir or self._settings.model,
                    device=self._settings.device,
                    compute_type=self._settings.compute_type,
                    cpu_threads=self._settings.cpu_threads,
                )
        return self._model


class AsrRequestHandler(BaseHTTPRequestHandler):
    server: "AsrServer"
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/health" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return

        self._write_json(
            HTTPStatus.OK,
            {"status": "ready", "modelLoaded": self.server.transcriber.loaded},
        )

    def do_POST(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/v1/transcriptions" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return

        try:
            self._authorize()
            payload = self._read_payload()
            source, language, job_id = self._validate_payload(payload)
            if not self.server.capacity.acquire(blocking=False):
                raise RequestError(HTTPStatus.TOO_MANY_REQUESTS, "asr_busy")

            try:
                segments = self.server.transcriber.transcribe(source, language)
            finally:
                self.server.capacity.release()

            LOGGER.info("transcription completed job_id=%s segments=%s", job_id, len(segments))
            self._write_json(HTTPStatus.OK, {"segments": segments})
        except RequestError as error:
            self._write_json(error.status, {"error": error.code})
        except Exception:  # Never return provider details or file paths to callers.
            LOGGER.exception("transcription failed")
            self._write_json(HTTPStatus.SERVICE_UNAVAILABLE, {"error": "asr_unavailable"})

    def log_message(self, _: str, *args: Any) -> None:
        # Do not emit request paths, asset references, or request text.
        LOGGER.info("http status=%s", args[1] if len(args) > 1 else "unknown")

    def _authorize(self) -> None:
        expected = self.server.settings.api_key
        if not expected:
            return

        supplied = self.headers.get(self.server.settings.api_key_header, "")
        if not supplied or not hmac.compare_digest(supplied, expected):
            raise RequestError(HTTPStatus.UNAUTHORIZED, "unauthorized")

    def _read_payload(self) -> dict[str, Any]:
        content_type = self.headers.get("Content-Type", "")
        if not content_type.lower().startswith("application/json"):
            raise RequestError(HTTPStatus.UNSUPPORTED_MEDIA_TYPE, "unsupported_content_type")

        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower().strip()
        has_content_length = "Content-Length" in self.headers
        if transfer_encoding:
            if transfer_encoding != "chunked" or has_content_length:
                raise RequestError(HTTPStatus.BAD_REQUEST, "ambiguous_transfer_encoding")
            raw_payload = self._read_chunked_body()
        else:
            try:
                length = int(self.headers["Content-Length"])
            except (KeyError, TypeError, ValueError) as error:
                raise RequestError(HTTPStatus.LENGTH_REQUIRED, "content_length_required") from error

            if length <= 0 or length > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large")
            raw_payload = self.rfile.read(length)
            if len(raw_payload) != length:
                raise RequestError(HTTPStatus.BAD_REQUEST, "incomplete_request_body")

        try:
            payload = json.loads(raw_payload)
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_json") from error

        if not isinstance(payload, dict):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_request")
        return payload

    def _read_chunked_body(self) -> bytes:
        chunks: list[bytes] = []
        total = 0
        while True:
            line = self.rfile.readline(self.server.settings.max_request_bytes + 1)
            if not line or len(line) > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body")

            try:
                size = int(line.split(b";", 1)[0].strip(), 16)
            except ValueError as error:
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body") from error

            if size < 0 or total + size > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large")
            if size == 0:
                self._consume_trailers()
                break

            chunk = self.rfile.read(size)
            if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body")
            chunks.append(chunk)
            total += size

        if total == 0:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_request")
        return b"".join(chunks)

    def _consume_trailers(self) -> None:
        while True:
            trailer = self.rfile.readline(self.server.settings.max_request_bytes + 1)
            if trailer in (b"\r\n", b"\n"):
                return
            if not trailer or len(trailer) > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body")

    def _validate_payload(self, payload: dict[str, Any]) -> tuple[Path, str, str]:
        input_payload = payload.get("input")
        if not isinstance(input_payload, dict):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input")

        media_type = input_payload.get("mediaType")
        if not isinstance(media_type, str) or not media_type.lower().startswith(("audio/", "video/")):
            raise RequestError(HTTPStatus.BAD_REQUEST, "unsupported_media_type")

        declared_size = input_payload.get("sizeBytes")
        if declared_size is not None and (isinstance(declared_size, bool) or not isinstance(declared_size, int)):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input_size")
        if declared_size is not None and (declared_size <= 0 or declared_size > self.server.settings.max_input_bytes):
            raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "input_too_large")

        declared_duration = input_payload.get("durationSeconds")
        if declared_duration is not None and (
            isinstance(declared_duration, bool)
            or not isinstance(declared_duration, (int, float))
            or declared_duration <= 0
            or declared_duration > self.server.settings.max_duration_seconds
        ):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_input_duration")

        source = resolve_temp_asset(input_payload.get("uri"), self.server.settings.allowed_root)
        if source.stat().st_size <= 0 or source.stat().st_size > self.server.settings.max_input_bytes:
            raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "input_too_large")

        parameters = payload.get("parameters") or {}
        if not isinstance(parameters, dict):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_parameters")
        language = parameters.get("language", "zh")
        if not isinstance(language, str) or language not in self.server.settings.allowed_languages:
            raise RequestError(HTTPStatus.BAD_REQUEST, "unsupported_language")

        job_id = payload.get("jobId", "unknown")
        return source, language, job_id if isinstance(job_id, str) and job_id else "unknown"

    def _write_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        content = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(content)


class AsrServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: tuple[str, int], settings: Settings, max_concurrent: int) -> None:
        super().__init__(address, AsrRequestHandler)
        self.settings = settings
        self.transcriber = FasterWhisperTranscriber(settings)
        self.capacity = threading.BoundedSemaphore(max_concurrent)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allowed-root", required=True, help="Filesystem root mapped to temp://media/.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--model", default="base")
    parser.add_argument("--model-dir", default=None)
    parser.add_argument("--device", default="cpu", choices=("cpu", "cuda", "auto"))
    parser.add_argument("--compute-type", default="int8")
    parser.add_argument("--cpu-threads", type=int, default=8)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--api-key-header", default="X-Agent-Api-Key")
    parser.add_argument("--max-request-bytes", type=int, default=64 * 1024)
    parser.add_argument("--max-input-bytes", type=int, default=100 * 1024 * 1024)
    parser.add_argument("--max-duration-seconds", type=float, default=20 * 60)
    parser.add_argument("--max-concurrent", type=int, default=1)
    parser.add_argument("--allowed-language", action="append", default=["zh", "en"])
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.cpu_threads <= 0 or args.max_concurrent <= 0:
        raise SystemExit("cpu threads and max concurrent must be positive")
    if args.host not in {"127.0.0.1", "::1", "localhost"}:
        raise SystemExit("ASR sidecar only supports loopback hosts")

    root = Path(args.allowed_root)
    if not root.is_dir():
        raise SystemExit("allowed root must be an existing directory")

    settings = Settings(
        allowed_root=root.resolve(),
        model=args.model,
        model_dir=args.model_dir,
        device=args.device,
        compute_type=args.compute_type,
        cpu_threads=args.cpu_threads,
        api_key=args.api_key or None,
        api_key_header=args.api_key_header,
        max_request_bytes=args.max_request_bytes,
        max_input_bytes=args.max_input_bytes,
        max_duration_seconds=args.max_duration_seconds,
        allowed_languages=frozenset(args.allowed_language),
    )
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    server = AsrServer((args.host, args.port), settings, args.max_concurrent)
    LOGGER.info("ASR sidecar listening host=%s port=%s", args.host, args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
