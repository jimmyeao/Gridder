"""Dump the raw SERATO_BEATGRID vorbis comment from a FLAC file."""
import sys
import os
import re
import base64

path = sys.argv[1] if len(sys.argv) > 1 else None
if not path:
    # Find first FLAC in E:\music\80s with "Hangin" in name
    for f in os.listdir(r"E:\music\80s"):
        if "Hangin" in f and f.endswith(".flac"):
            path = os.path.join(r"E:\music\80s", f)
            break

if not path or not os.path.exists(path):
    print(f"File not found: {path}")
    sys.exit(1)

print(f"Reading: {path}")

from mutagen.flac import FLAC
f = FLAC(path)

if "SERATO_BEATGRID" in f.tags:
    val = f.tags["SERATO_BEATGRID"][0]
    print(f"Tag length: {len(val)} chars")
    print(f"First 300 chars:\n{repr(val[:300])}")
    print(f"Last 50 chars: {repr(val[-50:])}")

    clean = val.replace("\n", "").replace("\r", "")
    print(f"Cleaned length: {len(clean)}")

    bad = re.findall(r'[^A-Za-z0-9+/=]', clean)
    if bad:
        print(f"NON-BASE64 chars found: {sorted(set(bad))}")
        # Show positions
        for ch in set(bad):
            positions = [i for i, c in enumerate(clean) if c == ch]
            print(f"  '{ch}' (0x{ord(ch):02x}) at positions: {positions[:10]}")
    else:
        print("All chars are valid base64")
        try:
            decoded = base64.b64decode(clean)
            print(f"Decoded OK: {len(decoded)} bytes")
            print(f"First 40 bytes hex: {decoded[:40].hex()}")
            mime_end = decoded.find(b'\x00')
            if mime_end > 0:
                print(f"MIME type: {decoded[:mime_end].decode('ascii', errors='replace')}")
        except Exception as e:
            print(f"Decode FAILED: {e}")
else:
    print("No SERATO_BEATGRID tag found")
    print(f"Available tags: {list(f.tags.keys())}")
