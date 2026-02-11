"""
Utilities for reading MP3 metadata and existing Serato BeatGrid tags.

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
import sys


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


def read_serato_beatgrid(filepath):
    """Read existing Serato BeatGrid markers from an audio file.

    For MP3: parses the ID3v2 GEOB frame with description "Serato BeatGrid".
    Returns a list of marker dicts, or None if no grid found.

    Each non-terminal marker has: {'position': float, 'beats_until_next': int}
    Terminal marker (last) has:   {'position': float, 'bpm': float}
    """
    if filepath.lower().endswith('.mp3'):
        return _read_mp3_serato_beatgrid(filepath)
    # FLAC has no codec delay so calibration is less important
    return None


def _read_mp3_serato_beatgrid(filepath):
    """Parse ID3v2 GEOB frame 'Serato BeatGrid' from MP3 file."""
    try:
        with open(filepath, 'rb') as f:
            header = f.read(10)
            if len(header) < 10 or header[:3] != b'ID3':
                return None

            version_major = header[3]
            tag_size = ((header[6] & 0x7F) << 21 |
                        (header[7] & 0x7F) << 14 |
                        (header[8] & 0x7F) << 7 |
                        (header[9] & 0x7F))

            # Read full ID3v2 tag
            tag_data = f.read(tag_size)
    except IOError:
        return None

    if len(tag_data) < tag_size:
        return None

    pos = 0
    end = tag_size

    while pos < end - 10:
        frame_id = tag_data[pos:pos + 4]
        if frame_id[0] == 0:  # Padding
            break

        if version_major == 4:
            # ID3v2.4: synchsafe frame size
            frame_size = ((tag_data[pos + 4] & 0x7F) << 21 |
                          (tag_data[pos + 5] & 0x7F) << 14 |
                          (tag_data[pos + 6] & 0x7F) << 7 |
                          (tag_data[pos + 7] & 0x7F))
        else:
            # ID3v2.3: regular big-endian frame size
            frame_size = (tag_data[pos + 4] << 24 |
                          tag_data[pos + 5] << 16 |
                          tag_data[pos + 6] << 8 |
                          tag_data[pos + 7])

        pos += 10  # Skip frame header (4 ID + 4 size + 2 flags)

        if frame_size <= 0 or pos + frame_size > end:
            break

        if frame_id == b'GEOB':
            frame_data = tag_data[pos:pos + frame_size]
            result = _parse_geob_serato_beatgrid(frame_data)
            if result is not None:
                return result

        pos += frame_size

    return None


def _parse_geob_serato_beatgrid(frame_data):
    """Parse a GEOB frame; returns marker list if it's Serato BeatGrid."""
    if len(frame_data) < 4:
        return None

    encoding = frame_data[0]
    pos = 1

    # Read null-terminated strings (MIME, filename, description)
    # MIME type is always Latin1 regardless of encoding byte
    def find_null(data, start):
        idx = data.find(b'\x00', start)
        return idx if idx >= 0 else len(data)

    # MIME type
    null_pos = find_null(frame_data, pos)
    pos = null_pos + 1

    # Filename (encoding-dependent null terminator)
    if encoding in (1, 2):
        # UTF-16: look for double-null
        while pos < len(frame_data) - 1:
            if frame_data[pos] == 0 and frame_data[pos + 1] == 0:
                pos += 2
                break
            pos += 1
        else:
            return None
    else:
        null_pos = find_null(frame_data, pos)
        pos = null_pos + 1

    # Description
    if encoding in (1, 2):
        desc_start = pos
        while pos < len(frame_data) - 1:
            if frame_data[pos] == 0 and frame_data[pos + 1] == 0:
                desc_bytes = frame_data[desc_start:pos]
                pos += 2
                break
            pos += 1
        else:
            return None
        desc_str = desc_bytes.decode('utf-16-le', errors='replace')
    else:
        null_pos = find_null(frame_data, pos)
        desc_str = frame_data[pos:null_pos].decode('latin1', errors='replace')
        pos = null_pos + 1

    if desc_str != 'Serato BeatGrid':
        return None

    # Binary data starts here; strip optional "Serato BeatGrid\0" prefix
    grid_data = frame_data[pos:]
    prefix = b'Serato BeatGrid\x00'
    if grid_data.startswith(prefix):
        grid_data = grid_data[len(prefix):]

    return _parse_beatgrid_binary(grid_data)


def _parse_beatgrid_binary(data):
    """Parse Serato BeatGrid binary format into marker list.

    Format: [0x01, 0x00] header + [uint32 BE] count + markers + [0x00] footer
    Non-terminal marker: [float32 BE position] + [uint32 BE beats_until_next]
    Terminal marker:     [float32 BE position] + [float32 BE bpm]
    """
    if len(data) < 6:
        return None

    if data[0] != 0x01 or data[1] != 0x00:
        return None

    marker_count = struct.unpack('>I', data[2:6])[0]
    if marker_count == 0:
        return None

    markers = []
    pos = 6

    for i in range(marker_count):
        if pos + 8 > len(data):
            break

        position = struct.unpack('>f', data[pos:pos + 4])[0]

        if i < marker_count - 1:
            beats_until_next = struct.unpack('>I', data[pos + 4:pos + 8])[0]
            markers.append({
                'position': float(position),
                'beats_until_next': int(beats_until_next),
            })
        else:
            bpm = struct.unpack('>f', data[pos + 4:pos + 8])[0]
            markers.append({
                'position': float(position),
                'bpm': float(bpm),
            })

        pos += 8

    return markers if len(markers) == marker_count else None


def reconstruct_serato_beats(markers, max_beats=500, duration=None):
    """Reconstruct individual beat times from Serato BeatGrid markers.

    Uses Serato's interpolation: beats are evenly spaced between markers.
    For the terminal marker, generates beats using its BPM.
    Returns a numpy-compatible list of beat positions in seconds.
    """
    if not markers or len(markers) < 1:
        return []

    beats = []
    for i, marker in enumerate(markers):
        pos = marker['position']
        beats.append(pos)

        if i < len(markers) - 1:
            # Non-terminal: interpolate to next marker
            next_pos = markers[i + 1]['position']
            n_beats = marker.get('beats_until_next', 0)
            if n_beats > 0:
                interval = (next_pos - pos) / n_beats
                for k in range(1, n_beats):
                    beats.append(pos + k * interval)
                    if len(beats) >= max_beats:
                        return beats
        else:
            # Terminal marker: generate beats using BPM
            bpm = marker.get('bpm', 0)
            if bpm > 0:
                interval = 60.0 / bpm
                max_time = duration if duration else pos + interval * max_beats
                t = pos + interval
                while t < max_time and len(beats) < max_beats:
                    beats.append(t)
                    t += interval

    return beats
