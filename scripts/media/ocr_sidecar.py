#!/usr/bin/env python3
"""Restricted local RapidOCR HTTP sidecar for the media API."""

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
from urllib.parse import urlsplit

from asr_sidecar import RequestError, resolve_temp_asset

LOGGER = logging.getLogger("media.ocr_sidecar")


@dataclass(frozen=True)
class Settings:
    allowed_root: Path
    api_key: str | None
    api_key_header: str
    max_request_bytes: int
    max_input_bytes: int


def box_to_bounds(box: Any) -> dict[str, float] | None:
    if not isinstance(box, list) or len(box) < 4:
        return None
    try:
        points = [(float(point[0]), float(point[1])) for point in box]
    except (TypeError, ValueError, IndexError):
        return None
    x_values = [point[0] for point in points]
    y_values = [point[1] for point in points]
    values = [min(x_values), min(y_values), max(x_values) - min(x_values), max(y_values) - min(y_values)]
    return None if not all(value >= 0 and value == value and abs(value) != float("inf") for value in values) else {
        "x": round(values[0], 2),
        "y": round(values[1], 2),
        "width": round(values[2], 2),
        "height": round(values[3], 2),
    }


class RapidOcrEngine:
    def __init__(self) -> None:
        self._engine: Any = None
        self._lock = threading.Lock()

    @property
    def loaded(self) -> bool:
        return self._engine is not None

    def recognize(self, source: Path, timestamp_seconds: float) -> list[dict[str, Any]]:
        with self._lock:
            if self._engine is None:
                from rapidocr_onnxruntime import RapidOCR

                self._engine = RapidOCR()
            result, _ = self._engine(str(source))

        frames: list[dict[str, Any]] = []
        for item in result or []:
            if not isinstance(item, list) or len(item) < 3:
                continue
            bounds = box_to_bounds(item[0])
            text = str(item[1]).strip()
            try:
                confidence = float(item[2])
            except (TypeError, ValueError):
                continue
            if bounds is None or not text or not 0 <= confidence <= 1:
                continue
            frames.append({
                "timestampSeconds": timestamp_seconds,
                "text": text,
                "bounds": bounds,
                "language": "zh",
                "confidence": round(confidence, 4),
            })
        return frames


class OcrRequestHandler(BaseHTTPRequestHandler):
    server: "OcrServer"
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/health" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return
        self._write_json(HTTPStatus.OK, {"status": "ready", "modelLoaded": self.server.engine.loaded})

    def do_POST(self) -> None:  # noqa: N802
        request = urlsplit(self.path)
        if request.path != "/v1/ocr" or request.query:
            self._write_json(HTTPStatus.NOT_FOUND, {"error": "not_found"})
            return
        try:
            self._authorize()
            payload = self._read_payload()
            source, timestamp = self._validate_payload(payload)
            if not self.server.capacity.acquire(blocking=False):
                raise RequestError(HTTPStatus.TOO_MANY_REQUESTS, "ocr_busy")
            try:
                frames = self.server.engine.recognize(source, timestamp)
            finally:
                self.server.capacity.release()
            LOGGER.info("ocr completed frame_count=%s", len(frames))
            self._write_json(HTTPStatus.OK, {"frames": frames})
        except RequestError as error:
            self._write_json(error.status, {"error": error.code})
        except Exception:
            LOGGER.exception("ocr failed")
            self._write_json(HTTPStatus.SERVICE_UNAVAILABLE, {"error": "ocr_unavailable"})

    def log_message(self, _: str, *args: Any) -> None:
        LOGGER.info("http status=%s", args[1] if len(args) > 1 else "unknown")

    def _authorize(self) -> None:
        expected = self.server.settings.api_key
        if expected and not hmac.compare_digest(self.headers.get(self.server.settings.api_key_header, ""), expected):
            raise RequestError(HTTPStatus.UNAUTHORIZED, "unauthorized")

    def _read_payload(self) -> dict[str, Any]:
        if not self.headers.get("Content-Type", "").lower().startswith("application/json"):
            raise RequestError(HTTPStatus.UNSUPPORTED_MEDIA_TYPE, "unsupported_content_type")
        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower().strip()
        if transfer_encoding:
            if transfer_encoding != "chunked" or "Content-Length" in self.headers:
                raise RequestError(HTTPStatus.BAD_REQUEST, "ambiguous_transfer_encoding")
            raw = self._read_chunked()
        else:
            try:
                length = int(self.headers["Content-Length"])
            except (KeyError, ValueError) as error:
                raise RequestError(HTTPStatus.LENGTH_REQUIRED, "content_length_required") from error
            if length <= 0 or length > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large")
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise RequestError(HTTPStatus.BAD_REQUEST, "incomplete_request_body")
        try:
            payload = json.loads(raw)
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_json") from error
        if not isinstance(payload, dict):
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_request")
        return payload

    def _read_chunked(self) -> bytes:
        chunks: list[bytes] = []
        total = 0
        while True:
            line = self.rfile.readline(self.server.settings.max_request_bytes + 1)
            try:
                size = int(line.split(b";", 1)[0].strip(), 16)
            except (ValueError, IndexError) as error:
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body") from error
            if size < 0 or total + size > self.server.settings.max_request_bytes:
                raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large")
            if size == 0:
                while self.rfile.readline(self.server.settings.max_request_bytes + 1) not in (b"\r\n", b"\n"):
                    pass
                break
            chunk = self.rfile.read(size)
            if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_chunked_body")
            chunks.append(chunk)
            total += size
        if not total:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_request")
        return b"".join(chunks)

    def _validate_payload(self, payload: dict[str, Any]) -> tuple[Path, float]:
        input_payload = payload.get("input")
        if not isinstance(input_payload, dict) or not str(input_payload.get("mediaType", "")).lower().startswith("image/"):
            raise RequestError(HTTPStatus.BAD_REQUEST, "unsupported_media_type")
        declared_size = input_payload.get("sizeBytes")
        if isinstance(declared_size, int) and (declared_size <= 0 or declared_size > self.server.settings.max_input_bytes):
            raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "input_too_large")
        source = resolve_temp_asset(input_payload.get("uri"), self.server.settings.allowed_root)
        if source.stat().st_size <= 0 or source.stat().st_size > self.server.settings.max_input_bytes:
            raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "input_too_large")
        parameters = payload.get("parameters") or {}
        timestamp = parameters.get("timestampSeconds", 0) if isinstance(parameters, dict) else 0
        if isinstance(timestamp, str):
            try:
                timestamp = float(timestamp)
            except ValueError as error:
                raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_timestamp") from error
        if isinstance(timestamp, bool) or not isinstance(timestamp, (int, float)) or timestamp < 0:
            raise RequestError(HTTPStatus.BAD_REQUEST, "invalid_timestamp")
        return source, float(timestamp)

    def _write_json(self, status: HTTPStatus, payload: dict[str, Any]) -> None:
        content = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(content)


class OcrServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: tuple[str, int], settings: Settings) -> None:
        super().__init__(address, OcrRequestHandler)
        self.settings = settings
        self.engine = RapidOcrEngine()
        self.capacity = threading.BoundedSemaphore(1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allowed-root", required=True)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8092)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--api-key-header", default="X-Agent-Api-Key")
    parser.add_argument("--max-request-bytes", type=int, default=64 * 1024)
    parser.add_argument("--max-input-bytes", type=int, default=50 * 1024 * 1024)
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "::1", "localhost"}:
        raise SystemExit("OCR sidecar only supports loopback hosts")
    root = Path(args.allowed_root)
    if not root.is_dir():
        raise SystemExit("allowed root must be an existing directory")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    server = OcrServer((args.host, args.port), Settings(root.resolve(), args.api_key or None, args.api_key_header, args.max_request_bytes, args.max_input_bytes))
    LOGGER.info("OCR sidecar listening host=%s port=%s", args.host, args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
