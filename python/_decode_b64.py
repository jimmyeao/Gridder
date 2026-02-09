import base64
raw = "YXBwbGljYXRpb24vb2N0ZXQtc3RyZWFtAABTZXJhdG8gQmVhdEdyaWQAAQAAAAAAAA"
padded = raw + "=="
d = base64.b64decode(padded)
print(f"Decoded {len(d)} bytes")
print(f"Hex: {d.hex()}")
print(f"Text start: {d[:30]}")
mime_end = d.find(b'\x00')
print(f"MIME: {d[:mime_end]}")
print(f"After MIME+nulls: {d[mime_end+2:].hex()}")
