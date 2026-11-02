Aegis hands-free activation export — schema 1

Open index.html after extracting the entire ZIP. No server, CDN, API, network,
installation or third-party library is required. Keep all extracted assets together.
Use current desktop Chromium or Firefox. Audio players use relative WAV paths.
To export a dataset with audio, choose the extracted folder using the directory
picker. This explicit local selection works around browser file:// read restrictions.

metrics.json preserves the complete historical metrics export, including legacy
identity attribution; collection does not rewrite its format. export.json records
export time, counts and store status. manifest.jsonl has one JSON object per event;
events/YYYY-MM-DD/<UUID>/ contains event.json and two PCM16LE mono 16000Hz WAVs.
Every event has schemaVersion, original id, activatedAt (ISO8601 offset), endedAt
(or null while active), model/app/build information supplied by the native runtime,
detector configuration/scores (null when unavailable), activation source, initial
screen state, finalState, endReason, transcriptionStatus, storageIssues,
captureIssues and correlation identifiers. No transcript is required or included.
Human labels are null until the owner classifies. Unknown values remain null.
triggerContext/postActivation objects contain path, sampleRate, channels, encoding,
samples, durationMs and SHA-256 of the complete WAV including its RIFF header.

trigger_context.wav includes roughly 2 seconds before acceptance and up to 200ms
after it. It is strictly local diagnostic evidence. post_activation.wav starts at
the detector's effective sample boundary and ends with normal command capture,
even for no speech, cancellation or failure before STT. Zero-sample WAVs are valid.
Collection never sends diagnostic context to STT/backend and never extends recording
through processing/TTS. Wake-word diagnostic mode retains the existing roughly
3-second no-speech capture bound while continuous detection proceeds. Normal
commands retain the existing 20-second maximum. Collection is disabled by default.
Capture gaps/storage failures are explicit; the collection
has a bounded queue and quota. At quota it reports skipped events and stops accepting
new recordings; no unexported event is silently deleted. Disable/delete in Android
settings. Files are app-private and excluded from Android automatic backup.

Exports are consistent snapshots. snapshotInProgress=true indicates an event whose
recording/session was still open at the snapshot barrier. This is not a final failure
or final outcome. Its original local recording continues. exportedAt records this
snapshot's time in every manifest row. No original data is automatically deleted.
After unexpected process death, recoverable audio is finalized and endReason becomes
process_restart_unknown; endedAt/interruptionAt remain null because the actual end
time is unknown. recoveredAt records recovery observation; terminalRecovered=true. Explicit cancellation reasons reflect actual user input.

Review: 1=correct; 2=false_positive; 3=uncertain; 0=clear; arrows=navigate.
Labels persist in localStorage when available, with a visible warning otherwise.
The dated dataset ZIP includes only human-labeled examples, selected WAVs, JSONL/CSV,
provenance, original ids, occurrence/label timestamps, versions, hashes and groupId.
The browser assembles the ZIP in memory; available RAM limits very large exports.
The ZIP32 writer explicitly rejects over 65,535 entries or 4 GiB. All members of a
session/group must stay in the same train/validation/test split. No split or training
is run here. Uncertain examples never become automatic negatives. Dataset groupId
uses the native session correlation, or original event id with groupUnknown=true
when unknown. Preserve these fields in any future dataset processing.

Audio can include nearby conversations following a false activation. ZIPs/datasets
contain private recordings: choose their storage/share recipients explicitly.
This reviewer and ZIP writer are dependency-free Aegis source; no bundled ZIP library.
