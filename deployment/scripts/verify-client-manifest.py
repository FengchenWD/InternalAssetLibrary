#!/usr/bin/env python3
"""Read-only pinned-key ECDSA validation, matching ClientReleaseSignature.Payload."""
import base64
import datetime
import json
import re
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

PUBLIC_KEY = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1JskSJck7qN7Qw/USkbWw6qCJFyCzzLfHpEZ6tRaq5j0anG1GpI84u0rv8kNEVL47NFqi1yFMRJeXZwnT42gag=="


def string(value):
    data = value.encode("utf-8")
    length = len(data)
    prefix = bytearray()
    while length >= 128:
        prefix.append((length & 127) | 128)
        length >>= 7
    prefix.append(length)
    return bytes(prefix) + data


def ticks(timestamp):
    # Python datetime is microsecond-based; preserve the seventh .NET fractional digit.
    match = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})", timestamp)
    if not match:
        raise ValueError("publishedAt must be an ISO timestamp with at most 7 fractional digits")
    instant = datetime.datetime.fromisoformat(match[1] + match[3].replace("Z", "+00:00"))
    delta = instant.astimezone(datetime.timezone.utc) - datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    return (delta.days * 86400 + delta.seconds) * 10000000 + int((match[2] or "").ljust(7, "0"))


def payload(manifest):
    return (string(manifest["version"]) + string(manifest["minimumCompatibleVersion"])
            + struct.pack("<q", ticks(manifest["publishedAt"])) + string(manifest["installerFileName"])
            + struct.pack("<q", manifest["installerSizeBytes"]) + string(manifest["installerSha256"].upper())
            + string("/api/client/releases/latest/download") + string((manifest.get("releaseNotes") or "").strip()))


def integer(data):
    data = data.lstrip(b"\0") or b"\0"
    if data[0] & 128:
        data = b"\0" + data
    return b"\x02" + bytes([len(data)]) + data


def verify(path):
    manifest = json.loads(Path(path).read_text(encoding="utf-8"))
    signature = base64.b64decode(manifest["signature"], validate=True)
    if len(signature) != 64:
        raise ValueError("Invalid P1363 signature size")
    pair = integer(signature[:32]) + integer(signature[32:])
    with tempfile.TemporaryDirectory(prefix="ial-signature-") as directory:
        root = Path(directory)
        (root / "public.pem").write_text("-----BEGIN PUBLIC KEY-----\n" + PUBLIC_KEY + "\n-----END PUBLIC KEY-----\n", encoding="ascii")
        (root / "signature.der").write_bytes(b"\x30" + bytes([len(pair)]) + pair)
        (root / "payload.bin").write_bytes(payload(manifest))
        subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(root / "public.pem"), "-signature",
                        str(root / "signature.der"), str(root / "payload.bin")], check=True, capture_output=True)
    print("SIGNATURE_VALID=" + manifest["version"])


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: verify-client-manifest.py latest.json")
        verify(sys.argv[1])
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        print("ERROR: client release signature validation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
