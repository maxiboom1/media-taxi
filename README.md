# Media Taxi

**0.5.0 · Windows x64 broadcast ingest · 2026-11-28**

Media Taxi watches incoming media, creates Ross Tria/Abekas `.clip` files, attempts delivery to one to four servers, moves processed originals to Finished, and creates MP4 newsroom proxies. It runs as a console application with a commented INI file and read-only web dashboards. AMP Controller, Python and Ross DLLs are not runtime dependencies.

The original writer and the C# implementation passed offline production-fixture parity and Ross reader checks. The operator reported successful real production tests with one and two Tria servers, including a 51-clip batch. This is operator-reported acceptance of the tested native AVC-Intra100/1080p50 workflow, not an agent playout test or qualification of every normalization profile. Optional normalization still requires its own operator-controlled Tria acceptance.

## Quick start

1. Use the stable development deployment at `artifacts/media-taxi-current/`, or extract the Windows ZIP into a stable local folder on the RAID/10 GbE host.
2. Edit `media-taxi.ini` beside `MediaTaxi.exe`: source/output folders, destination shares, Tria addresses and dashboard bind address. Relative paths resolve against the INI directory.
3. Create the configured folders. Give the console account read/write/delete access to sources and local work/output folders, and create/write access to destination shares. Keep SQLite state local. If FFmpeg is absent from your package, put qualified `ffmpeg.exe` and `ffprobe.exe` at the configured paths.
4. Validate configuration and start:

```powershell
.\MediaTaxi.exe --version
.\MediaTaxi.exe --validate-config
.\MediaTaxi.exe
# Alternative configuration location:
.\MediaTaxi.exe --config 'D:\MediaTaxi\media-taxi.ini'
```

Open the configured dashboard, for example `http://localhost:8080/`, and inspect `/admin` before dropping a test MXF into Watch. Stop with Ctrl+C. Configuration changes apply on restart. One process may use a given state directory and cache root at a time.

The example INI uses local demonstration folders and documentation-only server addresses. Replace these before deployment. The dashboard has no authentication or operational commands; bind it to the intended broadcast LAN or localhost and restrict inbound access accordingly. This release does not install a Windows service or scheduled task.

## Processing contract

1. Discovery immediately creates a dashboard row. Watcher events prompt reconciliation; periodic complete scans remain authoritative.
2. Wait for stable size/mtime for the configured interval and acquire a readable, deny-write source handle. Recheck source identity before using prepared media and moving the source. A file still open for writing waits.
3. Probe actual video, audio, duration and start timecode. An H.264 label or filename extension alone is insufficient.
4. Copy compatible native compressed video without re-encoding. If enabled, normalize a supported incompatible source to the qualified target first; otherwise route it to Failed with Format mismatch.
5. Reserve bounded temporary cache capacity on C: and write one job-owned delivery `.clip`. Native compressed video streams directly into validated records; only PCM is spooled to WAV. No native intermediate H.264 file is created.
6. Move the successfully wrapped source to Finished. A failed source move must be resolved before either output branch starts.
7. Queue independent destination deliveries. Attempt each destination once, with global and per-destination limits. Overlap local reads and the required SHA-256 checks with ordered network writes through three 1 MiB buffers; hash each payload byte once, flush, compare the remote byte length through filesystem metadata and publish the final name atomically. Never read remote video/audio or headers back for verification. A fresh attempt never overwrites or adopts an existing file; a persisted interrupted publication can reconcile an existing final of the expected byte length.
8. Delete each delivery clip after its own destination attempts finish, including partial/all-copy failure. OGP confirmation and other jobs do not hold its cache. Generate `<clip-name>.mp4` from its Finished source through a separate queue. A running proxy finishes normally; by default no additional proxies start while high-resolution jobs wait for stability, cache, processing, archival or delivery. Source-absent jobs, terminal failures and OGP waits do not block proxy admission.

New files join the native/normalization pipeline without waiting for earlier proxies or server copies. FIFO admission follows readiness for each stage, allowing a later native file to overtake a slow normalization. Proxy admission and worker assignment use the same lock as incoming registration. Native workers default to **1**, normalization to **1**, proxies to **1**, global copies to **4**, and copies per destination to **1**. A burst may finish all high-resolution deliveries while proxies remain queued. Proxy progress is separate from high-resolution progress; Probing means a worker is actually inspecting the source, while sources waiting for that worker show Pending.

Each job API includes persisted `timings`: `probe`, `wrap` (combined native streaming/audio packing or normalization/wrapping), `archive`, `delivery:ID`, `copy-write:ID`, `copy-publish:ID` and `proxy`, with UTC start/end and measured seconds. Queue/readiness waiting is separate from active phase time. Delivery time includes the remote write, flush and publication, with no media readback. Timing metadata follows the job's source-owned lifetime.

After publication, request a fresh read-only OGP inventory without holding the copy slot. A completed copy remains yellow Copying until fresh OGP presence is confirmed, then green **OK**. OGP presence is the operator's acceptance criterion; no duration warning/status is shown. No SMB header read, full-file checksum readback or AMP fallback is performed. Availability remains separate from historical processing outcome.

There is one automatic copy attempt per destination. A completed unsuccessful attempt is not retried after restart. There is no delayed delivery queue, automatic restoration or re-sync. Delivery clips are temporary and are not an operator archive. Original MXFs remain in Finished; another import requires a new managed arrival and an available output/destination name.

Terminal ingest errors, unsupported formats and local name conflicts move the source to Failed. Failed-name collisions receive a unique suffix; the dashboard retains the original clip name. A source that physically cannot be moved stays visibly unresolved in the activity list and raises a system error. Rediscovery does not repeat conversion. A proxy failure after archival is reported separately and makes completion a warning; it never cancels high-resolution delivery or retries the proxy on restart. The original stays in Finished.

Existing archived sources and different local artifacts are never overwritten. To reuse a name, remove/archive its tracked source, allow two successful scans and owned local cleanup to finish, and resolve any destination-name conflict. Alternatively use a new basename. There is no resend control in this release.

## Native format and investigation

The native target is **AVC-Intra 100, 1920×1080 progressive, 50 fps, 10-bit 4:2:2**, with supported 48 kHz integer PCM audio. Native compressed video, PCM values, channel order, duration and source start timecode are preserved. Stereo occupies channels 1/2 and unused channels are silent. Ambiguous/multiple audio-stream layouts and unsupported mappings fail explicitly.

An Abekas `.clip` is not an MXF with a different extension. The qualified flat layout is:

| Part | Layout |
|---|---|
| Header | 32,768 bytes containing `AbekasClipMeta1` XML, UUID, track extents, video standard and striped start timecode |
| Video | 568,832-byte AVC-Intra access unit plus 512 zero bytes; 569,344-byte record stride |
| Audio | Eight 4,096-byte AUK channel records per video frame; each has a 32-byte header, 960 PCM32 samples and padding |
| Storage | Flat video extent followed by audio extent; 24-bit precision carried in PCM32 |

The writer validates the qualified access-unit structure and fixed frame size throughout wrapping. Native video streams directly from FFmpeg into a seekable cached clip in one source pass, while PCM is spooled to a temporary WAV and then packed into AUK records. The writer finalizes XML using the actual frame count and computes separate header/payload SHA-256 digests. Copying validates those digests from the local read stream. Managed binary buffers stay below 8 MiB per native worker. Inspection is reused. Normalization retains the qualified elementary-video/WAV path on C: and deletes essence immediately after wrapping. Native wrapping avoids video encoding, but extraction, wrapping and delivery still consume storage bandwidth. Proxy decoder and encoder thread limits are both applied. Throughput equivalence to Tria Importer requires measurement on the target RAID/network.

The independent reader supports this flat layout for validation and recovery. Sparse, externally referenced, virtual and unsupported edited/trimmed layouts are rejected. This is a qualified format implementation rather than a decoder for every Abekas variant.

Optional normalization uses FFmpeg `libx264` AVC-Intra class 100 and verifies fixed access units before publication. Defaults are 1080p50, aspect-preserving scaling with padding, square pixels and 48 kHz audio. Preserve ordered channels without automatic downmixing. Unsupported HDR/color transformations and ambiguous mappings fail explicitly; supported interlaced SDR inputs are deinterlaced. Normalization is disabled by default.

## Source-owned retention

There is **no 72-hour expiry or 02:00 purge timer**. EditShare owns the nightly archive action; Media Taxi follows actual folder membership.

| Source location | Behavior |
|---|---|
| Watch | Waiting, processing or visibly unresolved |
| Finished | Retain proxy and current destination availability; delivery clip already released |
| Failed | Retain failed entry while source exists |
| Absent from all three | Retire after two successful complete scans separated by the reconciliation interval |

Source roots are scanned at the top level. Finished/Failed may be separate children of Watch and are excluded from incoming discovery. Temporary names and reparse points are excluded. A disconnected root, failed enumeration or unknown storage state pauses absence confirmation and cleanup; it never means an empty folder.

Confirmed removal cancels active conversion/proxy work, closes owned handles, removes owned local `.clip`, MP4 and temporary artifacts, and drops the detail row. It never deletes completed Tria files or archived sources. Ownership and source generations protect today's same-named output from yesterday's delayed cleanup. Locked artifacts appear as Cleanup pending in `/admin` and are revisited during housekeeping. Startup observations do not confer ownership of unknown local artifacts.

## Dashboard and OGP availability

The Activity page uses AMP Controller's charcoal/Segoe UI language, an independent orange/white/black Media Taxi SVG mark, and a pinned header/footer. One unpaginated list includes every tracked source, sorted newest arrival first; inventory/progress updates do not change arrival ordering. Active/Older tabs, search, refresh controls, statistics cards and the clip-details dialog have been removed.

Rows are fixed at **42 px** and status boxes at **128 x 30 px**. Columns have fixed widths: Date 176 px; Clip Name and Clip Format 360 px each; Processing, Proxy and each enabled destination 144 px. Up to four destinations use their configured names. Long content is ellipsized, with full text available on hover. Table headings stay visible while rows scroll; narrow viewports scroll horizontally rather than resizing columns. Updates preserve row identity, focus and the current scroll anchor.

Clip Format presents stored original-source metadata compactly, for example **MXF/ Intra 1920x1080 50fps / 2ch / Dur: 1:31**. Duration is whole minutes:seconds, truncated to whole seconds, with two second digits and minutes continuing beyond 59. The full probe summary remains available on hover; no additional media probing is launched for display. Source format labels do not determine native compatibility.

Each box contains one visible text line with left-to-right background progress:

| Box | Status and color |
|---|---|
| Processing | Waiting/Pending/Probing neutral; Probing has no growing fill. Native grows green and stays Native/full green after wrapping. Transcoding grows yellow and finishes as Transcoded/full yellow. Failed/Blocked is solid red. |
| Proxy | Pending/Queued/Untracked neutral; Working grows green; Ready full green; Failed red. |
| Destination | Pending neutral; Copying grows yellow from bytes written. It stays full yellow/Copying after publication until fresh OGP confirms green OK. Copy failure is red Failed, including an existing-name conflict even when OGP sees that name. |

Processing, proxy and destination outcomes are independent. A failed proxy or destination does not repaint a completed native wrap as a processing failure. Later Removed/Missing/Unknown states retain their meaning below. No resend or retry is added.

Full clip errors appear in custom hover/focus tooltips, fetched from the read-only detail endpoint on demand. Long diagnostics are scrollable/selectable within the viewport; Escape dismisses them. Detailed errors retain the existing 64 KiB cap and never expand a row. The footer shows system/update problems with full messages on hover and compact 24-hour Imported, Native, Transcoded and Proxies counters. The former Live indicator only described the browser's SSE connection; connection problems now appear in the footer. System retains its health sections with the shared shell and palette, with additional counters in Diagnostics.

Availability is separate from historical copy outcomes:

| Availability | Meaning |
|---|---|
| OK | Name present in fresh complete inventory |
| Missing | Absent and never observed online for this job |
| Removed | Previously online, now absent from fresh complete inventory |
| Unknown | Inventory unavailable, incomplete or stale |

Removed is expected after operators clear Tria. Manual copying can make the status OK again; Media Taxi sends no media in response. Protected banners/openers and unrelated server files are never modified or deleted.

The independent OGP client ports the existing tested AMP Controller handshake, netstring framing, compressed JSON, complete `explorer.raw_clip_list` parsing, reconnect and stale-data rules. Requests are read-only inventory operations. Configure the logical inventory folder to match the destination. Existing Finished files discovered at startup are compared by name only: no rewrap, resend, overwrite or historical import statistics.

`/admin` shows cache usage/reservations/quota/free space, version/uptime, processing readiness, folder/share access and free space, FFmpeg/ffprobe versions and encoder capabilities, OGP readiness/freshness/errors, queues and workers, cleanup-pending items, system errors and rolling statistics. An offline destination or OGP connection does not stop attempts to other destinations. Missing required source/local-output folders or media tools pause new processing.

## Read-only HTTP interfaces

| Path | Response |
|---|---|
| `/` | Activity dashboard |
| `/admin` | System dashboard |
| `/api/jobs?view=all` | All tracked sources, ordered by original arrival; active/older filter views also remain available |
| `/api/jobs/{id}` | Read-only source/stage/wait/timing/diagnostic details, including destination diagnostics for tooltips; 404 after retirement |
| `/api/events` | SSE revision notifications and heartbeat; reconnect reloads a snapshot |
| `/api/system` | Folders, workers, media tools, servers, errors and readiness |
| `/api/statistics` | Rolling 24-hour counters |
| `/health/live` | Process is serving HTTP |
| `/health/ready` | 200 when processing prerequisites are healthy, otherwise 503 |

Proxies are **folder-only**. MP4 serving, Octopus registration, MOS messages, configuration editing and operational command endpoints are outside this release.

## Configuration reference

`media-taxi.example.ini` is the complete commented template. Copy to `media-taxi.ini`; use `--config PATH` for another location. All relative paths resolve against the INI directory. Booleans are `true`/`false`. Local output roots must not overlap one another, destinations or source roots.

| Section / keys | Valid values, units and defaults |
|---|---|
| `folders`: `watch`, `finished`, `failed` | Required existing distinct source roots. Finished/Failed may be children of Watch, not ancestors of it or each other. |
| `folders`: `proxy`, `work` | Proxy output on D:; temporary work defaults to **C:/MediaTaxiCache**. Separate roots. The obsolete `abekas` setting is ignored with a warning and is never probed for new work. |
| `cache`: `max_gib`, `min_free_gib` | Peak reservations **64 GiB** (0.1..86400); free disk reserve **32 GiB** (integer 0..65536). No spill to D:. |
| `proxy`: `defer_while_hires_busy` | **true** (default) or false. Defer only new proxy starts; never suspend a running encoder. |
| `folders`: `state` | Local SQLite and single-instance lock directory; not a network share. |
| `destinations:ID`: `enabled`, `name`, `path`, `host`, `port`, `inventory_folder` | One to four enabled destinations. IDs: letters/digits/dash/underscore. Display name, share and Tria OGP host. Port 1..65535, default 5254; folder default `IMPORTS`. |
| `workers`: `native`, `normalize`, `proxy`, `copy` | Independent limits 1..64; defaults **1, 1, 1, 4**. Proxies and destination copies do not hold native/normalization slots. |
| `workers`: `copies_per_destination` | 1..64, default **1**; keep 1 for initial qualification. |
| `media`: `ffmpeg`, `ffprobe` | Defaults `tools/ffmpeg.exe`, `tools/ffprobe.exe`; qualified executable paths. |
| `media`: `allow_normalization` | Default **false**. True permits supported incompatible inputs. Target fixed to qualified native format. |
| `media`: `threads`, `timeout_seconds` | Encoder threads 1..256, default **4**. Process timeout 10..86400 seconds, default **3600**. |
| `timing`: `stability_seconds`, `reconcile_seconds` | 0.1..86400 seconds; defaults **10**, **10**. Deny-write protection supplements size/mtime checks. |
| `timing`: `inventory_poll_seconds`, `inventory_timeout_seconds`, `reconnect_seconds` | 0.1..86400 seconds; defaults **30**, **5**, **5**. |
| `proxy`: `codec` | **h264** or `h265`; qualify H.265 in the external preview player. |
| `proxy`: `width`, `height`, `fps` | Even width 2..3840 and height 2..2160; fps 1..50. Defaults **640×360, 50**. |
| `proxy`: `rate_control`, `video_bitrate`, `crf` | `bitrate` (default) or `crf`. Default **1200k** bits/sec or CRF **26** (0..51). Positive bitrates allow `k`/`M` suffixes. |
| `proxy`: `preset` | ultrafast, superfast, veryfast, faster, **fast**, medium, slow, slower, veryslow. |
| `proxy`: `audio_bitrate`, `channels` | AAC **128k**. One or two distinct one-based indexes 1..8, default **1,2**; no automatic downmix. |
| `web`: `listen`, `time_zone` | HTTP address/port, no subpath; default **http://0.0.0.0:8080**. Time zone **Asia/Jerusalem**. |
| `timing`: `source_scan_timeout_seconds`, `health_check_timeout_seconds` | Integer 1..300 seconds; defaults **30**, **10**. Results are judged by actual completion time, not when polled. One outstanding OS operation per component. |
| `timing`: `health_check_interval_seconds` | Integer 5..3600 seconds; default **60**. Write/delete probes run at startup and this interval, separate from read-only source enumeration. |
| `timing`: `stalled_source_warning_seconds` | Integer 1..86400 seconds; default **300**. Warning only: no quarantine deadline, timed proxy override or resend. |
| `logging`: `level` | Trace, Debug, **Information**, Warning, Error, Critical or None; applies to console and file output. |
| `logging`: `file_enabled`, `directory`, `max_files`, `max_mib` | **true**, **runtime/logs**, **5** files (1..20), **10 MiB** per file (1..100). Relative to INI. Bounded asynchronous local logs; no external alerts. |

`--validate-config` checks syntax, ranges and folder relationships, not credentials or live access. `/admin` provides live checks. UNC shares use the console account's credentials; mapped drives depend on its session.

## Source waits, diagnostics and durability

Sharing violations remain Waiting for copy with a producer-lock reason. Permission, storage and network access failures show Source blocked and the time waiting began. Ordinary reconciliation rechecks readiness; it does not repeat a completed conversion. After the configured stalled-source interval, the dashboard and /admin flag the wait. Native PCM matching remains exact: a mismatch reports expected samples, actual samples and the sample delta without padding/trimming native audio.

Strict high-resolution priority remains enabled by default. A nonterminal Watch job waiting for stability, an unlock or cache space can defer new proxies indefinitely; the source blocker and wait age are visible. A finished destination failure (including an existing target name) is terminal, releases its delivery cache and does **not** block proxies. All-copy failure still leaves the source in Finished and generates its proxy. Rejected ingest sources get no proxy and do not block proxies for other jobs. No automatic resend, completed-copy retry, proxy retry or re-sync is added.

Source enumeration is read-only. Separate write/delete probes expose their last check time; a not-yet-performed check is Unknown. A slow scan completed inside its deadline is accepted even when observed on a later poll. A timed-out or inaccessible scan cannot authorize cleanup, and a hung OS call is never replaced by more simultaneous calls. Retirement still requires two distinct successful absent scans separated by reconciliation timing.

The engine lock protects memory only. Database checkpoints are immutable batches admitted to a bounded FIFO writer and committed on a dedicated thread outside that lock; processing awaits durable acknowledgment before the next external stage. Job/statistic events remain atomic and idempotent. Persistence failure pauses new processing/publication and appears in /admin/readiness. Views read cached disk/statistics snapshots, keeping HTTP/SSE responsive during storage delays. OGP names are normalized/indexed once per inventory list, not for every job. Statistics retention writes occur in housekeeping, never in HTTP GETs.

FFmpeg progress records are excluded from diagnostic tails, including native binary streaming on stderr. Actual error records and other key=value messages remain. Summaries are limited to 1,024 characters, diagnostic tails to 64 KiB, with phase and exit code when available. Windows inherited-input children start suspended until the managed process handle is attached, preserving immediate-exit diagnostics. The handle allowlist, seeking and delete-sharing behavior are retained.

File logging uses a bounded queue and rotation alongside console output; encoder progress is not logged per frame. A full queue or unavailable log directory does not stop ingest. /admin shows log errors/dropped messages, checkpoint health, scan freshness and source blockers. Keep logs local on C:, away from source/output media folders. Configuration rejects log directories overlapping source, proxy, cache or destination roots. Unknown files inside a cache job directory are retained as Cleanup pending; there is no legacy-directory sweep.

## Recovery and statistics

SQLite stores cache lifecycle/ownership/reservations, independent high-resolution phase, expected frame count, header/payload integrity, source generations, immutable arrivals, stages, artifact ownership/hashes, copy outcomes, inventory observations and anonymous events. It is restart state, not a long-term catalogue. Keep it with its corresponding configured folders.

Startup reconciles sources before resuming interrupted work. Reuse owned cached artifacts for unfinished initial deliveries and successful stages. A released delivery cache is normal; proxies resume from Finished independently. An unexpectedly lost cache is regenerated only if an unfinished initial delivery needs it. Previously completed successful and failed attempts are never repeated. Reconcile interrupted publication through the persisted attempt, expected byte length and independent fresh OGP presence; never repeat a completed failed destination attempt. Tria may rewrite XML when indexing, so no remote full-file hash is assumed. Incomplete job-owned work can be rebuilt. Removed sources do not resume publication. Once processing begins, a changed source fingerprint is a different generation. A source moved during a multi-folder scan does not create a phantom incoming job. Local cross-volume source moves still use integrity checks; these are separate from Tria delivery.

Slow destination-health probes do not block new source discovery, and unavailable shares do not spawn endless overlapping probes. Housekeeping revisits pending artifact deletion and unresolved source movement; it does not repeat encoding/delivery for terminal failures.

Rolling 24-hour events contain no filenames and survive detail cleanup:

- **Processed / native / transcoded:** first successful local wrapping per job.
- **Imported / native / transcoded:** first successful Media Taxi destination delivery. Four successful destinations still count as one import.
- **Processing failures:** terminal processing errors, including proxy failure after archive.
- **Delivery failures:** all initial destination attempts failed.
- **Proxy completions:** first successful proxy per job.

Events expire after 24 hours independently of folder cleanup. Startup observations and manual copies do not invent import events. Per-destination dashboard outcomes still show partial failures.

## Temporary cache and proxy recovery

Cache files live in state-specific, per-job directories with generation ownership markers. An exclusive root lock prevents two applications with different state directories from sharing a cache concurrently. Reserve estimated peak clip + PCM bytes before processing, plus elementary video for normalization. Free-space calculations include other jobs' unwritten reservations. A job larger than the quota fails clearly; temporary pressure leaves it Pending with a cache-wait reason. A processing guard cancels malformed inputs that exceed the reservation or disk reserve. Locked cleanup retains its reservation and appears in /admin until ordinary housekeeping succeeds. Output-name claims survive cache deletion until source retirement.

Each job releases essence after wrapping and its delivery clip after all its destination attempts terminate. Deferred proxies hold neither. Failed sources keep their failed entry while present; completed originals remain in Finished even after total delivery failure. Actual source disappearance still requires two complete healthy scans before retirement and MP4 cleanup.

Proxies use the Finished source and saved probing/normalization decisions, explicit ordered channel selection and silent padding. On Windows FFmpeg receives a synchronous, seekable source handle with read/delete sharing through an explicit child handle allowlist; another MXF copy is unnecessary. EditShare can rename/delete the source while encoding. Confirmed source retirement cancels the process and removes owned output. FFmpeg must support the [handle: input protocol](https://ffmpeg.org/ffmpeg-protocols.html#handle); /admin reports a missing capability as a processing prerequisite error.

API additions retain existing fields: jobs include hiresPhase, cacheWaitReason, proxyDeferralReason and cacheState; /api/system includes cache path, usageBytes, reservedBytes, quotaBytes, freeBytes and minimumFreeBytes. Done outcomes and destination availability remain independent of proxy deferral. Cache release does not reset immutable arrivals or rolling anonymous statistics.

C: and D: are separate virtual disks on the PERC controller; their physical drive groups are not established. Performance benefit must be measured on this host. Moving temporary writes off D: and deferring new proxies reduce contention without assuming a guaranteed speed multiplier.

## Delivery investigation — 2026-11-27

A current-format 59.1-second clip is **1,779,273,728 bytes**. A 335 MB/s full-file transfer would take about 5.3 seconds, before additional work. The original delivery loop did a local read, redundant whole-file plus payload hashing, and one awaited network write in sequence for every MiB.

Scratch-file transfers used the same source and Tria share, without wrapping/proxy activity during timing or historical-version binaries:

| Copy method | Total time | Average throughput |
|---|---:|---:|
| Original serialized loop with duplicate digest work | 18.13 s | 98 MB/s |
| Serialized loop calculating only the required digests | 12.32 s | 144 MB/s |
| Bounded overlapping pipeline, required digests retained | **6.70 s** | **266 MB/s** |
| Shipping pipeline verification repeat | **6.80 s** | **262 MB/s** |

An instrumented serialized copy spent 1.12 s reading, 9.65 s hashing, 6.73 s awaiting writes and 0.004 s flushing. Single-digest hashing took 4.86 s. The pipeline overlaps those stages and holds at most 3 MiB per active destination copy. Full-file hashing is used for a current restart checkpoint without split digests; it is never also calculated alongside the split payload digest. Cancellation/read/write/hash failure stops both pipeline workers, retains no final filename and follows the existing single-attempt policy. No destination media/header is read back.

Removing write-through and keeping a final flush measured 6.78 s versus 6.80 s with write-through, so the shipping path retains write-through. An isolated Windows CopyFileEx copy completed in 2.38 s from C: and 3.96 s from D:, including its final flush, without application payload hashing. That is a transfer-engine reference with different verification, not equivalent verified-delivery timing. Later trials had warm OS/controller caches. Local C:/D: reads measured 505/426 MB/s without hashes; two SHA-256 passes reduced throughput to about 172/173 MB/s. These measurements identify redundant CPU work and serialized I/O as the practical bottleneck; they do not establish a fixed cold-disk speed or guaranteed sustained network rate.

Only uniquely owned remote `.partial` scratch files were created and removed. No completed Tria clips were modified, no playback command was sent and no older Taxi executable was used. Investigation source/cache files were removed afterward. Wrapping/proxy/inventory behavior remains independently tested.

## Build, tests and packaging

Stack: .NET 10 Generic Host, ASP.NET Core/Kestrel, Microsoft.Data.Sqlite, external FFmpeg/ffprobe, plain HTML/CSS/JavaScript and SSE. Responsibilities are isolated:

| Source directory | Responsibility |
|---|---|
| `Configuration`, `Domain` | INI validation, job state and read-only view contracts |
| `Storage` | SQLite, complete source snapshots and verified file operations |
| `Media` | Bounded child processes, probing, normalization, writer/reader and proxies |
| `Engine` | Stage orchestration, workers, recovery and lifecycle ownership |
| `Inventory` | OGP protocol and complete inventory state |
| `Web`, `wwwroot` | Read-only endpoints, SSE and dashboards |
| `tests/MediaTaxi.Tests` | xUnit unit, filesystem, process, HTTP and media integration tests |

Use the SDK pinned in `global.json`:

```powershell
dotnet restore MediaTaxi.sln
dotnet build MediaTaxi.sln -c Release --no-restore
$env:MEDIATAXI_FFMPEG = 'C:\Tools\ffmpeg.exe'
$env:MEDIATAXI_FFPROBE = 'C:\Tools\ffprobe.exe'
dotnet test MediaTaxi.sln -c Release --no-restore
```

For release verification use `./scripts/Test.ps1`: it discovers `.tools/media` or accepts `-MediaToolsDirectory`, requires both tools, runs real codec tests, and executes `node --experimental-vm-modules scripts/TestDashboard.mjs`. Node is a development-test dependency only, never a runtime dependency. Direct `dotnet test` skips codec cases without the environment variables. Production media is absent from Git; `MEDIATAXI_PRODUCTION_MEDIA` optionally points to local fixtures for parity qualification. Synthetic tests verify native wrapping/extraction, exact PCM/channel packing, timecode, duration/final frame, normalization, H.264/H.265 MP4s and ownership tags. Filesystem/protocol tests cover growing copies, duplicates, conflicts, source moves, all/partial/no delivery, storage outages, cancellation, locked cleanup, safe name reuse, restart, OGP framing/staleness and HTTP/SSE. A 50-file burst and simulated 500-clip day exercise the real engine, SQLite and filesystem with fake media processing; these prove orchestration correctness, not encoder/network throughput.

```powershell
.\scripts\Publish.ps1 -MediaToolsDirectory 'C:\Tools\QualifiedFFmpeg'
```

Publish uses `.tools/dotnet` when present, otherwise `dotnet` from PATH. It stages a clean self-contained `win-x64` build under `.tools/publish-stage/`, then updates **`artifacts/media-taxi-current/`** in place. Its private INI and `runtime/state` remain at the same paths across iterations. Use **`state = runtime/state`**; qualified FFmpeg/ffprobe live in `tools/` beside the executable. Watch/Finished/Failed/proxy and the C: cache retain their configured real locations. Paths must never depend on another version's artifact folder.

The ZIP is always **`artifacts/media-taxi-current.zip`**, created from the clean staging tree with generic settings before private settings/runtime data are involved. It contains native SQLite, assets, README, VERSION, commented INI and the FFmpeg pair/licenses when supplied. The console requires no installed .NET runtime or Python. A program-file manifest allows removal of superseded owned binaries while preserving configuration/state. Publishing requires the console/state lock to be released. It validates the active INI, replaces the current ZIP, then removes all other generated children of this application's artifacts directory after absolute path/link checks. Current data/settings must be independent of those old directories first.

Media Taxi is currently in **development**. Work and test the current version; do not build version-to-version compatibility/migration machinery or launch older Taxi versions for performance baselines unless explicitly requested. Current-version restart recovery, source ownership and user data/settings remain required. Automatic version-specific backups have been removed. Handle schema changes deliberately during development without promising support for older releases.

AMP Controller remains a separate read-only reference project. Media Taxi has no sibling-source runtime dependency. Agent/rule files, prompt archives, vendor research, production media, local configuration, runtime state and artifacts stay excluded from Git. Synchronize version metadata and this README for each delivered version; keep the changelog here.

## Changelog

### 0.5.0 — 2026-11-28

- Replace activity tabs, cards, search, paging and the details dialog with one fixed-row list using AMP Controller's palette and typography, an independent Media Taxi logo, pinned bars and compact footer counters.
- Add single-line status boxes with background progress, persistent Native/Transcoded colors, independent proxy/destination outcomes, and yellow Copying until fresh OGP reports green OK.
- Report per-destination progress from completed writes without additional hashing, media reads, SQLite progress writes or changed copy limits; preserve the bounded copy pipeline and publication checks.
- Show full bounded diagnostics only in hover/focus tooltips; add the all-jobs API view, wrap-completion metadata and compact source format/duration labels generated without extra probing.
- Preserve configuration, storage/retention behavior and no-resend policy. Apply the shared shell to System while retaining its health information.
- Validation: **137 automated cases passed**, including copy progress, API/format checks and the real codec/engine regression suite; one optional production-fixture test was skipped. The separate 500-row/four-destination dashboard check passed. Browser checks verified fixed geometry at 1920×1080 and 1366×768, long hover diagnostics, scrolling and System. No live Tria test files or playback commands were issued.


### 0.4.0 — 2026-11-27

- Separate read-only scans from periodic write/delete probes; accept slow scans within configurable deadlines, retain one outstanding OS call, and expose source blockers/wait ages without weakening strict hires priority.
- Move SQLite writes and cache/filesystem inspection outside the dashboard lock; add ordered bounded durable checkpoints, cached health/statistics, indexed OGP matching and read-only statistics GETs.
- Filter FFmpeg progress out of bounded diagnostics, retain immediate Windows child-exit errors, preserve exact native PCM matching, and remove obsolete media-service fallbacks, extraction paths and legacy cleanup branches.
- Use typed operational states and VERSION-derived assembly/runtime version. Add bounded local logs, read-only job details, 50-row search/pagination and keyed dashboard updates.
- Preserve independent worker limits, single destination attempts, OGP-only green OK, no remote content readback, and normal proxy admission after terminal delivery failures.
- Validation: **131 automated cases passed**, including real-media engine/codec processing, 500-clip orchestration, delayed/failed checkpoints, slow/hung scans, log rotation, error filtering, source delete-sharing and shared-host shutdown. The separate 500-entry dashboard check passed. One opt-in production-fixture test was skipped; the release smoke used real production MXF media as described below.
- Live smoke: two uniquely named copies of a 5.92 s production MXF published successfully to the currently enabled Tria, with OGP OK and both proxies Ready. Discovery-to-publication was 12.70/12.72 s, including the unchanged 10 s stability wait; probe 0.39/0.40 s, wrap 1.20/1.23 s, copy 0.76/0.75 s. Both copies completed before new proxies started. Source removal retired the local test jobs/proxies and released all cache reservations while retaining anonymous counters and final server clips. No playback commands or remote media/header reads were used.
- Correct duplicate disposal of the shared engine so console shutdown releases its locks without an exception.


### 0.3.1 — 2026-11-27

- Remove redundant whole-file hashing when split header/payload verification is used; overlap local reads/integrity work with ordered writes using a bounded 3 MiB pipeline. Preserve final flush, no-overwrite publication, cancellation and one attempt per destination.
- Investigate current delivery methods directly: the 1.78 GB production clip copied in 6.80 s through the shipping pipeline versus 18.13 s through the original serialized loop, without remote media reads.
- Deploy to stable `artifacts/media-taxi-current/` with `state = runtime/state`; keep only this active build and `media-taxi-current.zip`, preserving private INI/current state while replacing program files.
- Remove automatic version-specific backups; document development-only current-version work and add corruption, cancellation, bounded-buffer and destination-failure tests.
- Validation: 111 unit/integration cases passed; the optional production-fixture test was skipped because this change does not alter wrapping. The shipping copy passed a real native-media/live-share check. Repeated in-place publication preserved INI/SQLite bytes and removed an obsolete manifest-owned program file.


### 0.3.0 — 2026-11-27

- Stream native AVC-Intra directly into the flat writer, reuse inspection, and validate separate header/payload digests during local delivery reads. No remote media/header readback.
- Prioritize high-resolution queues over new proxy starts; running proxies continue. Proxies read Finished sources through inherited seekable delete-sharing Windows handles.
- Replace retained Abekas outputs with bounded C: cache reservations, per-job cleanup after all copy outcomes, quota backpressure, exclusive cache ownership and restart-safe independent proxy state.
- Add cache and deferral health/API/dashboard fields, migration backups, source-based recovery and regression coverage.
- Six live publications succeeded with OGP confirmation; the matched 59.1-second input reached publication in 39.19 s versus 53.23 s on 0.2.1, including readiness and excluding proxy completion.


### 0.2.1 — 2026-11-27

- Archive wrapped sources before starting independent delivery and proxy branches. Slow/failed proxies never block subsequent native imports, and proxy failures do not cancel high-resolution deliveries.
- Default to one native worker; report actual probing and queued proxies separately, with independent proxy progress.
- Extract native video and PCM in one FFmpeg pass and limit proxy decoder threads as well as encoder threads.
- Persist phase timings, separate write/publication from OGP confirmation, and derive packaging paths from VERSION.
- Remove all remote media/header checksum readbacks. Green OK is established by an immediate read-only OGP inventory response confirming presence; no duration warning/status is shown. Reconcile persisted interrupted publications without overwriting existing final names.
- Ignore stale Watch entries captured during an archive move.
- Default heavy media/work folders to D:. Record successful six-file live OGP test and measured per-phase timings; pause interrupted jobs whose source is already absent while retirement confirmation is pending.
- Added regression coverage for ten native deliveries while a proxy is blocked, per-target copy serialization, archive ordering and independent proxy failures/restart.

### 0.2.0 — 2026-11-25

- Implemented the Windows console, commented INI, self-contained packaging, activity/system dashboards, SSE and read-only health APIs.
- Ported the flat Abekas AVC-Intra100/1080p50 writer/reader to C#, with strict access-unit checks, AUK packing, timecode, optional normalization and configurable MP4 proxies.
- Added stable-file discovery, independent verified no-overwrite delivery, Finished/Failed routing, SQLite recovery and source-owned retention/cancellation/cleanup.
- Added OGP inventory with OK/Missing/Removed/Unknown, startup archive observations and anonymous rolling statistics.
- Added format, filesystem, process, HTTP, inventory, recovery and simulated broadcast-load tests. Six production fixtures passed C# parity and Ross reader checks; operator Tria acceptance remains required for the new writer and normalization.

### 0.1.0 — 2026-11-25

- Created the independent public project and preserved ingest, normalization, delivery, archive, proxy, dashboard, configuration and recovery requirements.
- Recorded the native-format investigation, six production conversions and successful operator Tria playback of the original writer's output.
- Kept AMP Controller separate and research/prompt archives local. This was a documentation baseline; runtime implementation began in 0.2.0.
