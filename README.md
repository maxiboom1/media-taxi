# Media Taxi

Version: **0.1.0**. Updated: **2026-11-25**.

**Status: requirements and research baseline. The watchfolder service and dashboard
have not been implemented yet.** Native .clip creation has been demonstrated with
real production MXFs, and the user confirmed that the generated clips work on Tria
after direct copying into its media folder.

Media Taxi will be an independent background ingest and delivery utility for Ross
Tria. It will watch incoming media, copy compatible AVC-Intra essence into native
Abekas `.clip` containers, optionally transcode incompatible sources, deliver the
result to up to four destinations, archive completed sources, and create low-resolution
MP4 previews for the Octopus newsroom workflow.

This README preserves the user's requirements and the investigation results so
implementation can continue in a separate chat without losing context. Requirements,
proposed defaults, and deferred decisions are identified below; descriptions of the
future service are not claims that those components already exist.

## Relationship to AMP Controller

Both applications are developed in one shared local Codex workspace, with
independent repositories, versions, builds, and deployment:

- **AMP Controller** owns Tria playback/transport controls and its existing Octopus
  MOS integration.
- **Media Taxi** owns watchfolder ingest, optional normalization, native wrapping,
  delivery, source archival, proxies, persistent jobs, and delivery/inventory status.

The tested OGP/openGear implementation in the local sibling folder
`../AMP Controller/src/main/opengear/`
is the reference for Tria inventory access. Reuse its protocol handling and tests
with clear provenance. Media Taxi must run without the AMP Controller executable or
source checkout being installed beside it. Any shared library should be versioned
and independently packaged rather than accessed through a runtime sibling-source path.

This chat's scope is exclusively Media Taxi. AMP Controller may be read for context;
its files and repository must not be modified by Media Taxi work.

Shared rules, AGENTS files, research documents, and prompt history remain local and
are excluded from this repository. The README is the published requirements and
investigation summary. Application source and necessary build/version metadata will
be added when implementation begins.

**Requirements gaps and the technology stack remain open for the next discussion.**
This repository setup does not select a runtime, framework, or deployment stack.

## Required processing sequence

1. **Detect a new input** in the configured watch folder and create a dashboard row
   immediately. Newest incoming items appear at the top.
2. **Wait for a complete, stable source.** Do not process a file that is still copying
   or growing. Keep this readiness stage distinct from a processing failure.
3. **Probe the actual media**, including video codec/profile, geometry, frame rate,
   scan mode, bit depth/chroma, audio, duration, and timecode.
4. **Choose native rewrap or optional normalization.** Compatible AVC-Intra inputs
   use the fast rewrap path. Incompatible inputs are transcoded only when explicitly
   enabled in configuration; otherwise show a red `Format mismatch` status.
5. **Create and validate one local canonical `.clip` artifact.** Preserve native
   compressed video without re-encoding and preserve supported audio/timecodes.
6. **Deliver that artifact independently to each enabled destination**, with separate
   progress, verification, retry, and inventory confirmation state for each one.
7. **Move the original completed source into the configured finished folder** once
   the configured completion/archive policy is satisfied. Persist the new source path.
8. **After the source move succeeds, generate the low-resolution MP4** into the
   configured proxy folder and record its final path/URL for Octopus.
9. **Continue monitoring inventory availability** so a later missing server copy can
   be shown and, in a later version, re-sent without repeating successful stages.

Proxy generation is a separate queued stage after successful archival. It must not
consume the native-import queue's capacity or cause successful deliveries to be
repeated merely because a proxy needs retrying.

## Watchfolder and source readiness

- The watch folder is configurable and local in the initial requirement. Network
  paths may be supported, but must not weaken readiness/recovery behavior.
- Use event notifications for responsiveness plus startup and periodic reconciliation
  scans so missed notifications do not permanently miss incoming files.
- Debounce duplicate create/change/rename events into one active job for the same
  source instance. Do not enqueue one job for every filesystem notification.
- Readiness settings must include a configurable stability interval and polling
  cadence. Check size/mtime stability, readable/probeable media, and source identity
  before and after processing. A paused copy can still look stable temporarily.
- Where the producer supports it, a temporary upload name followed by a final rename
  or explicit completion marker provides stronger completion signaling. Support a
  classic stability-based fallback with retries for incomplete/truncated media.
- The finished folder may be inside the watched folder. Exclude the entire resolved
  finished subtree, working/cache folders, proxy output, and destination outputs from
  ingestion. Avoid recursive ingestion of generated artifacts or archived sources.
- If an input disappears before processing completes, record `Input missing` and
  retain useful recovery state. An intentional archive move is a different event.
- Keep unsupported/corrupt inputs visible with an actionable error. The exact
  failed/quarantine-folder policy is a configuration/design decision, not an implicit
  source deletion.

## Native format and optional transcoding

The first supported target is the format already demonstrated on the user's Tria:

| Property                 | Initial supported target                                                      |
| ------------------------ | ----------------------------------------------------------------------------- |
| Container                | Native flat Abekas `.clip`, not renamed MXF                                   |
| Video                    | AVC-Intra 100 / H.264 High 4:2:2 Intra                                        |
| Picture                  | 1920x1080 progressive, 50 fps                                                 |
| Pixel format             | 10-bit 4:2:2                                                                  |
| Native AVC payload       | 568,832 bytes per access unit in the tested profile                           |
| Native disk video record | 569,344 bytes including the tested 512-byte trailer                           |
| Audio                    | Eight ordered AUK channels, 48 kHz, 32-bit sample words with 24-bit precision |
| Stereo input             | Preserve channels 1/2 and make channels 3..8 silent                           |
| Timecode                 | Preserve source start timecode using its actual base/drop-frame metadata      |

Compatibility must be determined from the actual stream/frame structure, not the
extension or an `h264` codec label alone. A native input must avoid video decoding
and re-encoding in the conversion path. Lossless audio word-width normalization is
allowed when required by AUK; do not mix channels or resample silently.

When incoming-media transcoding is enabled, FFmpeg may normalize supported sources
to the target before wrapping. Its output must satisfy the same target checks.
Frame-rate, aspect-ratio, interlace, audio mapping, duration, and timecode policies
must be explicit. An arbitrary hardware H.264 encoder is not automatically an
AVC-Intra encoder. Qualify the selected FFmpeg build/profile against the proven
format and actual Tria before claiming support for additional conversions.

When transcoding is disabled, a mismatch must be red in the dashboard and must not
silently trigger a lossy conversion. Preserve the source for correction or a later
explicit retry. The initial service should accept supported input containers;
"incoming file" does not mean every arbitrary file is decodable media.

## Destinations and reliable publication

- Support **one to four enabled destinations**, each with a stable ID, display name,
  destination folder, associated Tria name/IP, inventory folder/namespace, and retry
  policy. Display names such as `TLV1` and `TLV2` become dashboard column headings.
- Build one canonical local `.clip`, then copy that same artifact to each server.
  Re-delivery of this artifact preserves its UUID and bytes.
- Transfers are independent. One inaccessible destination must not block successful
  delivery to the others or unrelated jobs.
- Copy under a temporary/non-final name, verify completion, and publish the final
  name within the destination filesystem only after the file is complete. Tria must
  not encounter a partially copied file with its final `.clip` name.
- Persist each destination's attempt count, error, copied/verified state, inventory
  state, retry schedule, expected path, and last successful confirmation.
- Distinguish bytes copied from clip registered in Tria inventory. Allow a bounded
  catalog-discovery delay before declaring an inventory problem.
- Use UNC share paths where applicable and configure the Windows service account's
  permissions. Do not assume interactive mapped drive letters exist for a service.

**Archive/completion policy requires an explicit setting.** A proposed initial
default is to archive after local validation and at least one successful destination
delivery, keep unavailable destinations queued, and report yellow completion when
the remaining required work finishes. If no destination succeeds, show `Awaiting
delivery`. Alternatives such as all-required-destinations or local-artifact-plus-
durable-retry should be deliberate policies. This proposal is not yet a fixed API.

## Finished-source handling

- Move completed sources from watch to the user-defined finished folder; this may
  be a subfolder of watch, with exclusion rules preventing re-import loops.
- Persist the job's archived source path and resume safely after interruption.
- Handle name collisions according to a configured policy. Never silently overwrite
  a different archived source with the same name.
- A cross-volume "move" is copy, verify, then remove the original source only after
  successful verification and a final source-identity check.
- Archive failure is a distinct stage failure. Do not pretend the move succeeded
  or start the required post-archive proxy stage before it actually succeeds.

## Low-resolution MP4 and Octopus

- After archival, create a low-resolution MP4 in the user-configured proxy folder.
- Configure codec, resolution, frame rate, rate control/bitrate or quality, audio
  selection/bitrate, encoder preset, and concurrency through the config file.
- H.264 video with AAC audio is the proposed default. H.265 may be offered as a
  documented option after testing the actual Octopus preview client/browser.
- Publish complete MP4s under final names and place MP4 metadata for progressive
  playback. Preserve correct picture/audio duration and the last video frame.
- Generate previews that represent the delivered media. For normalized inputs,
  define whether the proxy reads the normalized master or final clip; do not create
  an unnoticed difference between Tria playout and newsroom preview.
- A proxy failure must not discard successful Tria transfers. Queue that stage for
  retry and show its status separately.
- Define clip-to-proxy naming, configured base URL/path mapping, how Octopus obtains
  that URL, and any required MOS integration. An MP4 appearing in a folder alone
  does not complete the Octopus integration. AMP Controller's existing MOS ownership
  must remain clear.
- If Media Taxi serves previews itself, implement appropriate MP4 responses/range
  handling; otherwise document the external web server's mapping.

## Configuration

All user settings must live in a **commented INI or XML file**. INI is the proposed
initial choice. No configuration administration panel is required in the first
stage; the browser UI is an activity dashboard.

Document valid values, units, defaults, and restart/reload behavior. Validate the
whole configuration at startup, including path overlap/exclusions, destination
count, profile options, and worker limits. Record a configuration version/snapshot
with jobs when changes could affect recovery.

Configuration must cover:

- Watch, finished, working/cache, failed/quarantine if used, and proxy folders.
- Readiness timing, periodic scans, source filters, and incomplete-file handling.
- Target format and the explicit allow/disable switch for incoming normalization.
- One to four destinations with names, folder paths, Tria IPs, and inventory mapping.
- Completion/archive policy, transfer verification, retries/backoff, and retention.
- Native workers, incoming transcode workers, proxy workers, copy workers, per-server
  copy limits, and FFmpeg threads/resource limits.
- Proxy codec settings, output naming, and Octopus URL mapping.
- Dashboard bind address/port, inventory polling/freshness, and logging.

The following is an **illustrative requirements example**, not an implemented
configuration schema or a deployment-ready file. Paths/IPs are placeholders.

```ini
[watch]
path = C:\MediaTaxi\watch
finished_path = C:\MediaTaxi\watch\finished
; Exclude finished_path and every working/output subtree from ingestion.
; Stability is a readiness heuristic, not proof that a producer has closed a file.
stable_seconds = 15
poll_seconds = 2
reconcile_seconds = 30

[native]
target = avcintra100_1080p50
; false: red Format mismatch. true: normalize supported incompatible input first.
allow_input_transcode = false

[workers]
native_rewrap = 2
input_transcode = 1
proxy_transcode = 1
destination_copy = 2
copies_per_destination = 1
ffmpeg_threads_per_process = 4

[storage]
work_path = C:\MediaTaxi\work
clip_cache_path = C:\MediaTaxi\cache
; Retention and minimum free-space rules must be validated during implementation.
clip_cache_days = 7

[delivery]
; Proposed policy; other policies must be documented if implemented.
archive_after = any_destination
retry_initial_seconds = 30

[proxy]
output_path = C:\MediaTaxi\proxies
base_url = http://media-taxi:8080/proxy/
; Requested codec options: h264 or h265. h265 requires Octopus client testing.
video_codec = h264
; Rate control options to define: bitrate or crf; validate codec-specific ranges.
rate_control = bitrate
video_bitrate = 1200k
width = 640
height = 360
fps = 50
audio_codec = aac
audio_bitrate = 128k
; One-based source channel indexes, kept in this order.
source_audio_channels = 1,2

[destination.TLV1]
enabled = true
display_name = TLV1
tria_ip = 192.0.2.11
destination_path = \\TRIA-1\Media
inventory_folder = IMPORTS

[destination.TLV2]
enabled = true
display_name = TLV2
tria_ip = 192.0.2.12
destination_path = \\TRIA-2\Media
inventory_folder = IMPORTS

[dashboard]
bind_address = 127.0.0.1
port = 8080
```

INI settings are operator configuration. Job history, attempts, hashes, progress,
and current availability belong in persistent runtime storage, not edits to the INI.

## Minimal web dashboard

Serve a small, functional table with one row per incoming job and new rows at the
top. Closing the browser must not stop the background service.

Required columns:

- Incoming date/time and clip/source name.
- Probed container/codec/format summary.
- Overall processing stage and actionable error text when relevant.
- Percentage/progress bar for incoming normalization where duration/progress permits.
- One destination column per enabled configured destination, using its display name.
- A separate proxy stage/link so delivery and preview availability remain visible.

Required/proposed status meanings:

| Display                                 | Meaning                                                                                                 |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| Pending / Waiting for copy              | Source detected; readiness not yet satisfied                                                            |
| Working / Probing / Rewrapping          | Native ingest stages are running                                                                        |
| Transcoding with percentage             | Enabled incoming-media normalization is running                                                         |
| Red Format mismatch                     | Input incompatible and normalization disabled                                                           |
| Copying / Awaiting inventory            | Destination transfer or catalog confirmation pending                                                    |
| Done, green                             | Required pipeline completed and all enabled destinations confirmed                                      |
| Done, yellow                            | Required pipeline completed but one or more destinations failed/unconfirmed under the completion policy |
| Red destination                         | Confirmed missing clip or failed delivery, with an explanation                                          |
| Unknown / stale, gray or yellow         | Server/inventory unavailable; not proof the clip was deleted                                            |
| Proxy queued / working / ready / failed | Separate preview generation state                                                                       |

Use real stage progress where measurable: transcoder media time versus duration,
copy bytes versus expected size, or an indeterminate indicator when a reliable
percentage is unavailable. Do not invent a smooth percentage for unknown work.

Keep historical processing outcome distinct from current availability. A delivered
clip can remain historically complete while a server cell later becomes missing.
Server-level connectivity/freshness should be visible without turning every row
into a false deletion alarm.

## OGP/openGear inventory and future re-sync

Use the existing tested OGP implementation as the first inventory reference. It
uses TCP 5254, initialization/handshake, and complete `explorer.raw_clip_list` data.
AMP inventory can be considered as a fallback; it is not the first integration task.

- Track each configured Tria independently and preserve the last valid inventory.
- Process only complete, valid inventory responses as authoritative snapshots.
- Mark data stale on disconnect/timeouts; never replace a valid list with a failed
  or partial scan and infer that all clips disappeared.
- A fresh list that no longer contains a previously confirmed clip should change
  that destination's current status to missing/red.
- Inventory currently exposes paths/names, not a guaranteed content/version identity.
  Combine it with the service's own artifact/delivery records and explicit collision
  policies. A green inventory name alone does not prove expected file contents.
- Distinguish source disappearance from a later deletion on a Tria destination.

**Deferred feature: per-clip re-sync controls.** The persistent model must support
them from the beginning. A future action should resend the retained verified clip
only to missing/failed selected destinations, retaining successful deliveries and
avoiding another rewrap/transcode. If no retained artifact exists, explain whether
the archived source can regenerate it or recovery is unavailable.

Automatic healing after deletion should be opt-in: an operator may intentionally
delete a server copy. The precise UI, confirmation/replacement rules, and automatic
policies will be designed later, as requested by the user.

## Persistent jobs, retries, and duplicate inputs

A local durable job database is proposed, with isolated source, artifact, stage,
attempt, proxy, and per-destination records. SQLite is a reasonable initial option;
implementation must make restart behavior explicit.

- Do not use filename alone as a completed-job identity. Record stable source
  identity/fingerprint, content hashes where appropriate, and a separate job ID.
- Repeated filesystem notifications must not duplicate active processing.
- The same file submitted again should be recognized and support retrying missing
  stages/destinations without repeating successful work.
- Different content under the same filename needs an explicit version/replacement
  policy. Do not silently overwrite production or archived media.
- Keep a canonical clip cache and archive references for future recovery/re-sync.
- Recover after interruption during copy, transcode, wrapping, delivery, archival,
  and proxy generation. Resume at safe stage boundaries; restart incomplete encoder
  work when it cannot safely resume, and clean/reconcile abandoned partial files.
- Persist retry attempts/backoff and protect jobs against simultaneous claims.
- Handle full disks, permissions, disconnected shares, read-only sources, source
  changes, corrupt media, and application shutdown with clear states.
- A destination failure must not lose the source or artifact and must not prevent
  other destinations/jobs from progressing.

## Concurrency and performance

Configure native rewrap, input normalization, proxy encoding, and delivery capacity
independently. Bound child-process counts and FFmpeg thread usage. Keep the service
and dashboard responsive under load, and give native ingest priority over proxies.

The performance goal is native-import-class speed for compatible media. The
conversion path should preserve compressed video, avoid unnecessary full decoding,
minimize intermediate copies, and verify/hash while streaming where practical.
The research scripts deliberately perform extra passes for proof and are not the
final high-throughput implementation.

The tested format is roughly 1.8 GB per minute including native audio. Four full
destination copies create roughly 7.2 GB of outgoing data for that minute of media.
Measure rewrap time separately from network delivery and proxy time; throughput
depends on storage/network bandwidth and configured parallelism. Benchmark against
the actual Tria Importer and representative production media rather than promise
an unmeasured speed.

## Investigation summary and reusable evidence

The format is a proprietary container around standard AVC-Intra video and PCM
audio. The tested flat layout has a 32 KiB XML header, fixed-size video records,
and eight AUK audio channels. Renaming an MXF is insufficient; correct wrapping is
required. An independent writer works without Ross DLLs or the Oak codec card.

Evidence already established:

- Two original native sample clips were extracted and converted to complete MP4s.
- Newly generated synthetic video/audio was placed in `.clip` containers accepted
  by Ross's supplied reader.
- Synthetic AVC-Intra MXF essence could be extracted and wrapped without video
  re-encoding or header repair.
- Six real Premiere/Media Encoder production MXFs were rewrapped with exact video,
  exact normalized audio, preserved channel order/start timecodes, and unique UUIDs.
- Independent decoding read every output video frame, and Ross ClipLib read every
  video/audio unit and confirmed duration/timecode/profile.
- On **2026-11-25**, the user confirmed those production `.clip` files **work perfectly
  on Tria after being copied directly into its media folder**. Hardware acceptance
  for this native profile is therefore confirmed by the user.
- The supplied `clip2mp4.exe` worked offline but produced one fewer decodable video
  frame and only six audio channels in our tests. The independent FFmpeg proxy
  route preserved every frame and expected decoded audio duration.

The detailed reference material remains in the local workspace, outside this GitHub
repository. Paths below are local context references, not downloadable repository links:

- Detailed format/provenance notes: `../rules/tria-clip-investigation.md`.
- Independent flat writer: `../AMP Controller/scripts/abekas-create-clip.py`.
- Independent inspector/extractor/proxy converter: `../AMP Controller/scripts/abekas-clip.py`.
- Native-library writer proof: `../AMP Controller/scripts/create-tria-clip.ps1`.
- Production validation: the `REWRAP_RESULTS` files under the sibling controller's
  `Documents/Tria Abekas example and app/media example/real mxf from production/`.
- Research reports: `../AMP Controller/Documents/clip-research/`.
- Ignored production helpers/logs: `../AMP Controller/.tools/clip-production/`.

These are research tools and evidence, not a packaged Media Taxi service. Additional
.clip generations, codecs, frame rates, sparse/virtual/growing layouts, and arbitrary
new encoder output are outside the demonstrated scope until specifically qualified.
Some historical reports say hardware testing was pending; that describes the date
they were written and is superseded by the user's confirmation above.

## Suggested implementation milestones

1. Preserve this specification and proven format/OGP context; hardware acceptance of
   native production rewrap is already reported as passed.
2. Isolate/harden the native muxer, input probing, configuration, readiness, persistent
   jobs, delivery, and archive behavior with representative fixtures.
3. Add the minimal dashboard, destination status, recovery/retry, and OGP inventory.
4. Add queued proxies and the explicit Octopus URL/MOS integration contract.
5. Add optional incompatible-input normalization, concurrency/resource controls,
   compatibility qualification, and realistic throughput measurements.
6. Design and implement operator retry/re-sync actions and any explicit automatic
   healing policies. The data model should already support these operations.

The exact runtime/framework packaging is still to be selected. No service
installation, build commands, production connections, or dashboard deployment are
provided by this documentation-only 0.1.0 baseline.

## Development and prompt history

Keep modules small, cohesive, and isolated. Follow the shared rules and maintain
this README as both the system description and primary versioned changelog.
Every delivered code change must bump `VERSION` and any future package/project
version metadata, update relevant docs, and add a changelog entry below. Do not
create a separate changelog file.

Every human-authored user prompt must be saved as its own text-only `.txt` file in
the local workspace's shared `../prompt history/` folder, excluding
attachments, automatic context, assistant output, and tool/reasoning content.
Prompt archives and local agent/rule documents are not published to GitHub.

## Changelog

### 0.1.0 — 2026-11-25

- Created the independent Media Taxi project and preserved the user's complete
  watchfolder, rewrap/optional-transcode, four-destination, archive, proxy, commented
  config, dashboard, concurrency, recovery, inventory, and future re-sync requirements.
- Recorded native flat AVC-Intra100/1080p50 proof, six production conversions, and
  the user's successful direct-copy Tria playback confirmation.
- Linked the existing format prototypes, production evidence, and OGP implementation.
- Added shared/app-specific instructions, a specification version file, and local
  artifact ignores in the local workspace; agent/rule/research files are excluded
  from the published repository.
- Prepared the independent `media-taxi` repository for initial publication with
  README and repository/version metadata only. Kept AMP Controller read-only and
  left characterization gaps and technology-stack selection explicitly open.
- Runtime/service implementation has not started.
