Comprehensive Technical Analysis of Serato DJ Pro Metadata Architecture, Beatgrid Persistence, and Database Synchronization Protocols
1. Executive Summary and Architectural Overview
The development of third-party applications capable of interfacing with the Serato DJ Pro ecosystem requires a rigorous understanding of the proprietary data structures used for library management and audio analysis. For a developer aiming to implement superior beat detection algorithms and inject this data into Serato, the challenge lies not merely in the signal processing domain but in the precise serialization and persistence of the resulting metadata. This report provides an exhaustive technical reference on how Serato stores, reads, and synchronizes beatgrid information, cue points, and library metadata. The architecture operates on a dual-layer persistence model comprising the audio file’s embedded metadata tags—serving as the portable source of truth—and a central binary database acting as a high-performance index.
Serato's design philosophy prioritizes the portability of metadata, ensuring that critical performance data such as beatgrids, loops, and hot cues travel with the audio file itself. This is achieved through the encapsulation of proprietary binary blobs within standard metadata containers, specifically ID3v2 GEOB frames for MP3 and AIFF files, Vorbis Comments for FLAC and Ogg files, and custom freeform atoms for MP4/AAC containers. The central database, located in the _Serato_ directory, mirrors this information to facilitate rapid searching and crate organization but typically defers to the file-level tags upon loading or re-scanning. Consequently, the primary vector for an external application to update beatgrid information is the modification of these embedded tags, coupled with a mechanism to trigger Serato’s re-ingestion logic.
The following analysis dissects these storage mechanisms at the byte level, leveraging reverse-engineering documentation, open-source parser implementations, and technical support literature to construct a complete picture of the data flow. It addresses the binary serialization of dynamic and static beatgrids, the structural schema of the database V2 file, and the synchronization protocols required to ensure data consistency between an external beat detection engine and the Serato user interface.
2. Audio File Metadata Persistence Layer
The foundation of Serato’s metadata system is the embedding of analysis data directly into the audio files. This approach ensures that if a DJ moves a file to a different computer, the beatgrid, BPM, and cue points are preserved without needing to transport a separate database file. However, because standard metadata specifications like ID3 do not natively support "beatgrids" or "waveform overviews," Serato utilizes generic binary encapsulation fields to store this proprietary data.
2.1 ID3v2 Implementation (MP3 and AIFF)
For MPEG-1 Audio Layer III (MP3) and Audio Interchange File Format (AIFF) files, Serato utilizes the ID3v2 tagging standard. Specifically, it employs the General Encapsulated Object (GEOB) frame. The GEOB frame is designed to hold arbitrary binary data associated with a MIME type and a content description string. Serato uses distinct description strings to segregate different types of data, effectively creating a key-value store within the ID3 tag.1
The structure of a generic ID3v2 frame header varies slightly between ID3v2.3 and ID3v2.4, but Serato’s implementation generally adheres to strict conventions to ensure compatibility. The GEOB frame itself consists of a text encoding byte, a MIME type string (typically application/octet-stream), a content description string, and the actual binary object. The critical description strings identified in the Serato ecosystem include Serato BeatGrid for grid information, Serato Markers2 for cue points and loops, Serato Analysis for versioning, Serato Autotags for BPM and gain values, and Serato Overview for waveform display data.1
It is imperative to note that the binary data stored within these GEOB frames is often raw binary, but in certain contexts or file types, it may be Base64 encoded. In the context of MP3 files, the data inside the Serato BeatGrid and Serato Markers2 GEOB frames is typically raw binary data, whereas in MP4 and FLAC containers, an additional layer of Base64 encoding is applied to ensure the binary data survives text-based transport mechanisms.3
2.1.1 The Serato BeatGrid GEOB Frame
The Serato BeatGrid frame is the primary storage location for the beatgrid definition. Unlike a simple BPM tag which stores a single float value, the BeatGrid frame contains the serialized representation of the grid markers. This allows for complex grids that can handle tempo drift (dynamic grids) or multiple timing sections. The binary payload within this frame dictates the alignment of the grid in the Serato interface. For a static grid—which assumes a constant tempo throughout the track—the payload is relatively small, often around 15 bytes.1 This compact size suggests a structure containing a version header, a single "Downbeat" marker (defining the start time), and a tempo definition.
However, for dynamic grids, which are essential for tracks with live drummers or fluctuating tempos, the payload grows significantly. The structure expands to include a sequence of "non-terminal" markers, each defining a new tempo anchor point, followed by a "terminal" marker that defines the grid for the remainder of the track.5 An external app aiming to improve beat detection must be capable of writing this variable-length sequence. If the app detects tempo drift, it must serialize a sequence of markers rather than a single start point, effectively rewriting the Serato BeatGrid frame with a complex definition.
2.1.2 The Serato Markers2 GEOB Frame
While the Serato BeatGrid frame handles the temporal map, the Serato Markers2 frame is responsible for user-defined interaction points. This frame effectively replaced the older Serato Markers_ frame, offering a more extensible structure that supports named cue points and color coding.4 The Markers2 frame is critical for beat detection applications because it often contains a synchronization lock flag, known as BPMLOCK.
If an external application updates the beatgrid but fails to set the BPMLOCK flag within the Serato Markers2 tag, Serato's internal analysis engine might overwrite the custom grid upon the next re-scan. Therefore, writing a valid Markers2 payload is as important as writing the grid itself. The data within Markers2 typically begins with a protocol version header (e.g., 0x01 0x01) and follows a Tag-Length-Value (TLV) structure for each entry, allowing it to store heterogeneous data such as cues, loops, and colors in a single binary blob.2
2.2 MP4 and M4A Container Specifics
For files using the MP4 container format (AAC, ALAC), Serato cannot use ID3 frames directly. Instead, it utilizes the "freeform" atom structure provided by the Apple iTunes metadata specification. These atoms are identified by the generic type ----. To distinguish its data from other applications, Serato uses a "mean" string of com.serato.dj and specific "name" strings corresponding to the ID3 descriptions.3
The mapping of atoms includes ----:com.serato.dj:beatgrid, ----:com.serato.dj:markersv2, ----:com.serato.dj:analysisVersion, and ----:com.serato.dj:autgain. A crucial distinction in the MP4 implementation is the encoding of the payload. Unlike the raw binary found in some ID3 tags, the data stored in the data atom of these freeform fields is Base64 encoded. Furthermore, technical analysis reveals a quirk in Serato’s parser: it often expects or inserts newline characters (0x0A) after every 72 characters of the Base64 string.2
Developers must handle this padding and formatting rigorously. When reading the tag, the application must strip these newlines before decoding the Base64 string to obtain the usable binary object. Conversely, when writing the tag, the application should re-encode the binary data into Base64 and insert the newlines at the appropriate intervals to ensure compatibility with Serato’s parser. Failure to replicate this formatting can result in the tag being ignored or deemed corrupt by the software.4
2.3 FLAC and Vorbis Comments
The implementation for FLAC files leverages the Vorbis Comment standard. Since Vorbis Comments are inherently text-based key-value pairs, storing binary data requires encoding. Serato adopts the same Base64 encoding strategy used in MP4 files. The field names are uppercase transformations of the ID3 descriptions, such as SERATO_BEATGRID and SERATO_MARKERS2.6
As with MP4s, the payload is a Base64 string that decodes into the proprietary binary format. The consistency of the underlying binary structure across MP3, MP4, and FLAC—once the container-specific encoding (Base64 vs. Raw) is handled—simplifies the development of the external beat detection app. The core logic for serializing a beatgrid need only be implemented once, with an adaptation layer to handle the container-specific wrapping.
3. Detailed Binary Data Structures and Serialization
The capability to programmatically update the beatgrid relies on generating bit-perfect binary payloads. The Serato ecosystem utilizes proprietary serialization formats that differ significantly from standard open protocols. While the exact schemas are not publicly documented by Serato, reverse-engineering efforts by the open-source community, particularly within the triseratops and serato-tools projects, have illuminated the critical structures.7
3.1 Serialization of the Serato Markers2 Tag
The Serato Markers2 tag is a container for multiple types of metadata entries. Its binary structure, once decoded from any Base64 wrapper, is a sequence of entries prefixed by a global header. The global header is typically two bytes: 0x01 0x01, representing the format version.2 Following this header, the file consists of a series of entries, where each entry follows a specific structure composed of a Type String, a Length Integer, and the Payload.
3.1.1 Entry Header Structure
Each entry begins with a null-terminated ASCII string that identifies the entry type. Common types include COLOR, CUE, and BPMLOCK. Immediately following the null byte of the type string is a 4-byte integer representing the length of the payload. Crucially, technical analysis indicates that in the context of the Markers2 tag, this length integer is encoded in Little Endian format.2 This stands in contrast to the database files, which often use Big Endian, highlighting a potential pitfall for developers.
3.1.2 The CUE Entry
The CUE entry stores hot cue points. A standard unnamed cue point payload is typically 13 bytes long. The structure includes a 1-byte index (0-7), followed by a 4-byte position value representing the cue location in milliseconds (Little Endian), and 3 bytes representing the RGB color of the cue point.2 If the user has named the cue point, the payload length increases, and the UTF-8 encoded name string is appended to the binary structure.
For an external app, preserving existing cues while updating the beatgrid is essential. The app must parse the existing Markers2 tag, extract all CUE entries, and re-serialize them into the new payload. Overwriting this tag without preserving existing entries would result in data loss for the user.
3.1.3 The BPMLOCK Entry
The BPMLOCK entry is a simple but vital component for the external application. It consists of the type string BPMLOCK, a length of 1, and a single byte payload. Setting this payload to 0x01 indicates that the beatgrid is locked. This flag signals to Serato that the grid has been manually verified or externally modified and should not be recalculated by the internal auto-analysis engine.9 For an app providing "more accurate beat detection," asserting this lock is mandatory to prevent Serato from discarding the improved analysis.
3.1.4 The COLOR Entry
The COLOR entry defines the track's color as seen in the library. The payload typically contains 4 bytes, with the structure 0x00 0xRR 0xGG 0xBB. While not strictly related to beat detection, managing this entry allows the external app to potentially tag processed tracks with a specific color (e.g., green for "verified accurate"), providing visual feedback to the user.10
3.2 Serialization of the Serato BeatGrid Tag
The Serato BeatGrid tag contains the definition of the grid itself. Unlike the generic container nature of Markers2, the BeatGrid tag is a specialized sequence of marker definitions. The binary stream typically starts with a version header (likely 0x01 0x00 or similar) and is followed by a sequence of markers.
3.2.1 Static vs. Dynamic Grids
For a static grid, the structure is minimal. It requires only a single "Terminal Marker" that defines the start point (Downbeat) and the tempo (BPM) that applies to the entire track. The payload for such a grid is very small, often observed as 15 bytes in total.1 This compactness implies a structure that is highly optimized, likely storing the BPM as a float and the offset as a float or integer.
For dynamic grids, the structure is more complex. The payload contains a list of "Non-Terminal Markers" followed by a single "Terminal Marker".5 A Non-Terminal Marker defines a section of the track: it includes a start position, a tempo for that section, and the number of beats until the next marker. This allows the grid to warp and adapt to tempo changes. The Terminal Marker defines the final section, extending to the end of the file.
3.2.2 Byte-Level Marker Definition
While the exact proprietary byte mapping is not fully disclosed in the snippets, the functional requirements for these markers are clear from the triseratops struct definitions. Each marker must encode:
●	Position: The absolute sample offset or time in milliseconds where the marker is placed.
●	BPM/Tempo: The tempo value active starting from this marker.
●	Beat Count: For non-terminal markers, the duration of the segment in beats.
When the external app detects beat positions, it must calculate the interval between beats. If the interval remains constant, a single Terminal Marker is sufficient. If the interval varies (e.g., a live drummer speeds up), the app must generate a new Non-Terminal Marker at the point of divergence, with the updated BPM and position. This sequence is then serialized into the binary blob and written to the tag.
3.3 The Serato Analysis and Autotags
The Serato Analysis tag typically contains a 2-byte version number, such as 0x01 0x01, representing the version of the analysis engine.2 Updating this tag might be necessary to force Serato to acknowledge new analysis data, although the BPMLOCK is usually the primary enforcement mechanism.
The Serato Autotags field stores the BPM and Autogain values as ASCII text.11 For example, a BPM of 120.5 might be stored simply as the string "120.5". While the BeatGrid tag serves as the authoritative source for the grid alignment, the Autotags BPM value is often used for display in the library columns. Therefore, an external app should update this textual tag to match the calculated BPM of the generated grid, ensuring consistency between the waveform view and the library list.
4. Serato Library Database Architecture
While the audio files serve as the portable containers for metadata, the database V2 file located in the _Serato_ directory acts as the central system index. This binary file, along with the .crate files in the Subcrates directory, governs the organization of the library, including crate structures, play history, and cached metadata values.
4.1 Database V2 File Structure
The database V2 file is not a relational database in the traditional SQL sense but rather a proprietary flat-file database composed of a concatenated sequence of records. This format is shared with .crate files. The structure is hierarchical, resembling a DOM (Document Object Model) where container records hold simpler data records.12
4.1.1 The Record Header
Every record in the database begins with a generic header consisting of two 4-byte fields:
1.	Tag (4 bytes): A FourCC (Four-Character Code) ASCII string identifying the record type (e.g., otrk, vrsn, ptrk).
2.	Length (4 bytes): A 32-bit integer indicating the size of the record's content. A critical distinction here is that unlike the Markers2 tag payload, the database file uses Big Endian byte order for these length fields.14
4.1.2 Record Tag Schema
The interpretation of the data following the header depends on the tag pattern. Known tags include:
●	vrsn (Version): A UTF-16 Big Endian string defining the version of the crate or database format (e.g., "1.0/Serato ScratchLive Crate").
●	otrk (Object Track): A container record representing a single track. Its content is a nested sequence of sub-records.
●	ptrk (Path Track): A UTF-16 Big Endian string representing the file path of the track. This is the primary key used to link the database entry to the file on disk.
●	sbpm: A Signed 32-bit Big Endian integer representing the BPM value.
●	udat: An Unsigned 32-bit integer representing the "date added" timestamp.
●	bcrl: A single byte representing the track color index.
The hierarchical nature means that an otrk record encloses the ptrk and other metadata fields for that specific song. To modify a track's entry in the database, the parser must iterate through the file, identify the otrk block containing the matching ptrk, and then modify the sibling fields within that otrk block.
4.2 Crate Files
The .crate files found in the Subcrates directory follow the exact same binary specification as the database V2 file. They essentially serve as subsets of the main database. A .crate file contains a header vrsn record followed by a sequence of otrk records. Interestingly, the otrk records in crate files are often "shallow," containing only the ptrk (file path) to reference the track, relying on the main database V2 to hold the detailed metadata.15 This structure minimizes data redundancy but increases the dependency on the main database file for metadata consistency.
4.3 Risks of Database Modification
While it is technically possible to parse and modify the database V2 file to reflect new beatgrid or BPM values, this approach carries significant risk. The database format is brittle, and incorrect serialization (e.g., writing Little Endian lengths instead of Big Endian) will corrupt the library, potentially causing Serato to crash or reset the library.16 Furthermore, Serato employs internal checksums and caching mechanisms that may detect external tampering. If the timestamp of the audio file does not match the timestamp recorded in the database, Serato may trigger a re-read of the tags, overriding any direct changes made to the database fields.
5. Synchronization Protocols and Workflow Integration
The most robust strategy for an external application is to treat the audio file tags as the primary write target and leverage Serato’s built-in synchronization mechanisms to update the database. However, achieving immediate reflection of changes in the Serato UI requires understanding how the software manages its cache.
5.1 The "Rescan ID3 Tags" Mechanism
Serato includes a specific function labeled "Rescan ID3 Tags" within its Files panel. This function forces the software to re-read the metadata from the files and update the database V2 index accordingly.17 This is the safest method for integration.
1.	Write Phase: The external app analyzes the audio and writes the new Serato BeatGrid and Serato Markers2 tags to the file.
2.	Sync Phase: The user opens Serato, selects the modified files (or the entire library), and initiates the "Rescan ID3 Tags" command.
3.	Result: Serato parses the new tags, validates the BPMLOCK flag, and updates its internal library and waveform display to match the external analysis.
5.2 Automated Synchronization Techniques
For a seamless user experience, relying on manual rescanning is suboptimal. External tools have developed methods to trigger or simulate this synchronization programmatically.
●	File Renaming: One method involves slightly modifying the filename or the file modification timestamp. When Serato launches, it detects that the file has changed (via OS file system events or startup scan) and automatically re-reads the metadata. However, renaming files can break existing crate associations if the ptrk in the crate file is not also updated.19
●	Database Path Manipulation: A more advanced technique involves modifying the ptrk record in the database V2 file to point to the new file location (if the file was rewritten) or updating the metadata fields in the database directly to match the tags. Tools like serato-tools facilitate this by parsing the database and updating specific fields, allowing changes to appear instantly without a manual rescan.10
5.3 The BPMLOCK Safeguard
A critical requirement for the external application is the management of the BPMLOCK flag. Serato’s auto-analysis engine is aggressive; if it detects a track without a locked grid, it may attempt to re-analyze it, discarding the custom beatgrid. By setting the BPMLOCK entry in the Markers2 tag to 0x01 (Locked), the external app explicitly instructs Serato to respect the existing grid data. This effectively designates the external app as the authoritative source for the beatgrid.9
6. Implementation Strategy for the External Application
Based on the technical analysis, the recommended architecture for the beat detection application involves a three-stage pipeline: Analysis, Serialization, and Synchronization.
6.1 Stage 1: Audio Analysis and Grid Generation
The application must first decode the audio file to a raw PCM stream. Using its superior beat detection algorithm, it identifies the precise sample offsets of every beat.
●	Static Detection: If the beats are equidistant, the app calculates the average BPM and the offset of the first beat.
●	Dynamic Detection: If tempo drift is detected, the app generates a list of "warp markers." Each marker corresponds to a point where the tempo shifts or the grid needs realignment.
6.2 Stage 2: Tag Serialization and Writing
This is the core persistence step.
1.	Format Selection: Determine the container format (MP3/AIFF vs. MP4 vs. FLAC).
2.	Payload Construction:
○	Construct the BeatGrid binary payload using the proprietary marker sequence format.
○	Construct the Markers2 binary payload. This must include the BPMLOCK entry set to true. It should also preserve any existing cue points read from the original tag.
○	Construct the Autotags text payload with the new BPM value.
3.	Encoding:
○	If the file is MP3/AIFF, write the raw binary payloads to GEOB frames.
○	If the file is MP4/FLAC, encode the binary payloads to Base64. Ensure that newline characters (0x0A) are inserted every 72 characters to satisfy Serato's parser requirements.4
4.	Writing: Save the metadata to the file.
6.3 Stage 3: Database Update (Optional but Recommended)
To ensure the user sees the new BPM immediately in the library list:
1.	Parse the database V2 file.
2.	Find the otrk record corresponding to the file path.
3.	Update the sbpm (BPM) integer field to match the new analysis.
4.	Save the database V2 file. Note: The application must correctly calculate the Big Endian checksums or lengths to prevent corruption.
7. Handling Edge Cases and Data Integrity
The Serato ecosystem is sensitive to data corruption. Developers must handle several edge cases to ensure stability.
7.1 Text Encoding and Character Sets
ID3v2 specs allow for different text encodings (ISO-8859-1 vs. UTF-16). Serato historically has mixed support. For GEOB descriptions, ISO-8859-1 is the standard. However, for content within tags (like cue names in Markers2), UTF-8 is standard. In the database file, strings like paths (ptrk) are strictly UTF-16 Big Endian.14 Mismatching these encodings will result in "garbage" characters or the file being marked as missing.
7.2 Cross-Platform Path Handling
The database V2 file stores absolute paths. If the user moves their library between Windows and macOS, the path separators (\ vs /) and drive letters differ. Serato handles this via the "Relocate Lost Files" feature, but a third-party app modifying the DB must be aware of the host OS's filesystem conventions. When parsing the ptrk fields, the app should normalize paths to the current OS format to ensure it can locate the physical files.
7.3 "Read-Only" and Locked Files
If an audio file is marked as read-only by the OS, Serato cannot write tags to it and will fallback to storing metadata solely in the database V2 file (or Metadata/ XMLs). An external app should check for write permissions before attempting analysis. If write access is denied, the app can theoretically update the database V2 entry, but this data will not be portable. The robust solution is to alert the user to unlock the files.
8. Conclusion
The integration of an external beat detection engine into the Serato DJ Pro workflow is a feasible engineering challenge that hinges on the precise manipulation of proprietary metadata structures. The research confirms that the audio file tags—specifically the Serato BeatGrid and Serato Markers2 GEOB frames—are the authoritative source for performance data. By rigorously implementing the binary serialization formats for these tags, including the handling of Base64 encoding for specific containers and the enforcement of the BPMLOCK flag, developers can successfully inject superior beatgrid data into the ecosystem.
While the database V2 file offers a mechanism for library organization, its fragility and binary complexity make it a secondary target for modification, useful primarily for updating cached values like BPM text. The recommended path forward prioritizes file-level tag manipulation, leveraging Serato’s own scanning architecture to propagate these changes into the user’s library. This approach maximizes data safety, portability, and compatibility with future versions of the software.
Table 1: Summary of Serato Metadata Storage Vectors
Data Type	Primary Storage (File Tag)	Secondary Storage (Database/Crate)	Binary Format Notes
Beatgrid	Serato BeatGrid (GEOB/Atom)	Binary Blob in database V2	Sequence of markers; Base64 in MP4/FLAC.
Hot Cues	Serato Markers2 (GEOB/Atom)	Binary Blob in database V2	TLV entries; Little Endian lengths; Base64 in MP4/FLAC.
BPM Value	Serato Autotags (Text)	sbpm (Int32) in otrk	ASCII text in file; Big Endian Int in DB.
Track Color	Serato Markers2 (Color Entry)	bcrl (Byte) in otrk	RGB values in tag; Index byte in DB.
Waveform	Serato Overview (Binary)	Not typically stored in DB	Compressed overview data (~4KB).
Table 2: Binary Serialization Endianness Guide

Context	Data Element	Endianness	Reference
File Tags (Markers2)	Entry Lengths (UInt32)	Little Endian	2
File Tags (Markers2)	Cue Position (UInt32)	Little Endian	2
Database V2	Record Lengths (UInt32)	Big Endian	14
Database V2	Integer Values (UInt32/Int32)	Big Endian	14
Database V2	Text Strings (UTF-16)	Big Endian	14
Works cited
1.	Reversing Serato's GEOB tags, accessed on February 10, 2026, https://homepage.rub.de/jan.holthuis/reversing-seratos-geob-tags.html
2.	serato-tags/docs/writeup.md at main · Holzhaus/serato-tags · GitHub, accessed on February 10, 2026, https://github.com/Holzhaus/serato-tags/blob/main/docs/writeup.md
3.	serato-tags/docs/fileformats.md at main · Holzhaus/serato-tags ..., accessed on February 10, 2026, https://github.com/Holzhaus/serato-tags/blob/main/docs/fileformats.md
4.	Issue #3 · Holzhaus/serato-tags - MP4 files - GitHub, accessed on February 10, 2026, https://github.com/Holzhaus/serato-tags/issues/3
5.	Beatgrid in triseratops::tag::beatgrid - Rust, accessed on February 10, 2026, https://holzhaus.github.io/triseratops/triseratops/tag/beatgrid/struct.Beatgrid.html
6.	Serato Metadata Format · mixxxdj/mixxx Wiki - GitHub, accessed on February 10, 2026, https://github.com/mixxxdj/mixxx/wiki/Serato-Metadata-Format
7.	triseratops - Rust, accessed on February 10, 2026, https://holzhaus.github.io/triseratops/
8.	bvandercar-vt/serato-tools: Serato track metadata (cues, beatgrid, etc.), crate, smart crate, and library database modification; dynamic beatgrid analysis; and better USB sync. - GitHub, accessed on February 10, 2026, https://github.com/bvandercar-vt/serato-tools
9.	Mixxx Manual 2.5 en | PDF | Disc Jockey | Icon (Computing) - Scribd, accessed on February 10, 2026, https://www.scribd.com/document/846125928/mixxx-manual-2-5-en
10.	Serato_Tools: a python package that allows for track, library, and crate modification, and has Dynamic Beatgrid analysis : r/Serato - Reddit, accessed on February 10, 2026, https://www.reddit.com/r/Serato/comments/1k64c73/serato_tools_a_python_package_that_allows_for/
11.	Holzhaus/serato-tags: Serato DJ Pro GEOB tags documentation - GitHub, accessed on February 10, 2026, https://github.com/Holzhaus/serato-tags
12.	TobiasJacob/seratolibraryparser - GitHub, accessed on February 10, 2026, https://github.com/TobiasJacob/seratolibraryparser
13.	seratolibraryparser, accessed on February 10, 2026, https://tobiasjacob.github.io/seratolibraryparser/index.html
14.	Serato Database Format · mixxxdj/mixxx Wiki · GitHub, accessed on February 10, 2026, https://github.com/mixxxdj/mixxx/wiki/Serato-Database-Format
15.	Serato crate text encoding - Reddit, accessed on February 10, 2026, https://www.reddit.com/r/Serato/comments/hy3kp1/serato_crate_text_encoding/
16.	What is the DatabaseV2 file? - Serato Support, accessed on February 10, 2026, https://support.serato.com/hc/en-us/articles/204194250-What-is-the-DatabaseV2-file
17.	The EASY way to merge _Serato_ libraries and get your music off your laptop. - Reddit, accessed on February 10, 2026, https://www.reddit.com/r/DJs/comments/eqi0dk/the_easy_way_to_merge_serato_libraries_and_get/
18.	Re-scanning File Information - Serato Support, accessed on February 10, 2026, https://support.serato.com/hc/en-us/articles/227561687-Re-scanning-File-Information
19.	Using Mixed in Key with Serato Software, accessed on February 10, 2026, https://the-drop.serato.com/how-to/using-mixed-in-key-with-serato-software/
