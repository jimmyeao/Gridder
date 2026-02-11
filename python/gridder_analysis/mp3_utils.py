"""
Utilities for reading MP3 LAME/Xing header metadata.

MP3 encoders (LAME, etc.) add padding samples at the start of the file.
Different decoders handle this differently:
  - ffmpeg/madmom: strips the encoder delay (gapless-aware)
  - librosa/soundfile: strips the encoder delay (via mpg123)
  - Serato DJ: preserves the raw decoded frames (includes padding)

Since our beat detection uses decoders that strip the delay, but Serato
preserves it, beat positions need to be shifted forward by the encoder
delay when writing Serato tags for MP3 files.
"""

import struct


def get_mp3_encoder_delay(filepath):
    """Read encoder delay (in samples) from an MP3 file's LAME/Xing header.

    Returns the encoder delay in samples, or 576 (LAME default) if the
    LAME header cannot be found. Returns 0 for non-MP3 files.
    """
    if not filepath.lower().endswith('.mp3'):
        return 0

    try:
        with open(filepath, 'rb') as f:
            data = f.read(16384)
    except IOError:
        return 576

    if len(data) < 128:
        return 576

    # Skip ID3v2 tag if present
    offset = 0
    if data[:3] == b'ID3':
        if len(data) < 10:
            return 576
        # ID3v2 size is a synchsafe integer (7 bits per byte)
        size = ((data[6] & 0x7F) << 21 |
                (data[7] & 0x7F) << 14 |
                (data[8] & 0x7F) << 7 |
                (data[9] & 0x7F))
        offset = 10 + size

        # Re-read if ID3 tag extends past our initial buffer
        if offset + 4096 > len(data):
            try:
                with open(filepath, 'rb') as f:
                    f.seek(offset)
                    data = f.read(4096)
                    offset = 0  # reset since we re-read from the new position
            except IOError:
                return 576

    # Find first MPEG audio frame sync (0xFFE0+)
    max_search = min(len(data) - 4, offset + 8192)
    while offset < max_search:
        if data[offset] == 0xFF and (data[offset + 1] & 0xE0) == 0xE0:
            # Verify it looks like a valid MPEG audio frame
            b1 = data[offset + 1]
            mpeg_ver = (b1 >> 3) & 3
            layer = (b1 >> 1) & 3
            if mpeg_ver != 1 and layer != 0:  # not reserved values
                break
        offset += 1
    else:
        return 576

    frame_start = offset

    if frame_start + 4 > len(data):
        return 576

    header = struct.unpack('>I', data[frame_start:frame_start + 4])[0]
    mpeg_version = (header >> 19) & 3   # 3=MPEG1, 2=MPEG2, 0=MPEG2.5
    channel_mode = (header >> 6) & 3    # 3=mono, 0-2=stereo variants

    # Side information size depends on MPEG version and channel mode
    if mpeg_version == 3:  # MPEG1
        side_info_size = 32 if channel_mode != 3 else 17
    else:  # MPEG2 or MPEG2.5
        side_info_size = 17 if channel_mode != 3 else 9

    # Xing/Info tag follows the frame header (4 bytes) + side information
    xing_offset = frame_start + 4 + side_info_size

    if xing_offset + 8 > len(data):
        return 576

    tag = data[xing_offset:xing_offset + 4]
    if tag not in (b'Xing', b'Info'):
        return 576

    # Parse Xing flags to skip variable-length fields
    xing_flags = struct.unpack('>I', data[xing_offset + 4:xing_offset + 8])[0]
    pos = xing_offset + 8

    if xing_flags & 0x01:  # Frames count present
        pos += 4
    if xing_flags & 0x02:  # Bytes count present
        pos += 4
    if xing_flags & 0x04:  # TOC present
        pos += 100
    if xing_flags & 0x08:  # Quality indicator present
        pos += 4

    # Now at the encoder version string (9 bytes, e.g. "LAME3.100")
    # Encoder delay is at offset +21 from here: 3 bytes holding
    # 12-bit delay (upper) and 12-bit padding (lower)
    delay_offset = pos + 21

    if delay_offset + 3 > len(data):
        return 576

    # Verify this looks like a LAME/encoder tag (version string is ASCII)
    version_bytes = data[pos:pos + 9]
    if not all(32 <= b < 127 for b in version_bytes if b != 0):
        return 576

    b0 = data[delay_offset]
    b1 = data[delay_offset + 1]
    b2 = data[delay_offset + 2]

    encoder_delay = (b0 << 4) | (b1 >> 4)
    # encoder_padding = ((b1 & 0x0F) << 8) | b2  # not needed

    # Sanity check: typical encoder delay is 0-5000 samples
    if 0 < encoder_delay < 5000:
        return encoder_delay

    return 576  # LAME default
