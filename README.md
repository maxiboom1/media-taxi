# Media Taxi

**0.3.0 · Windows x64 broadcast ingest · 2026-11-27**

Media Taxi watches incoming media, creates Ross Tria/Abekas `.clip` files, attempts delivery to one to four servers, moves processed originals to Finished, and creates MP4 newsroom proxies. It runs as a console application with a commented INI file and read-only web dashboards. AMP Controller, Python and Ross DLLs are not runtime dependencies.

The original investigation writer produced clips from six real production MXFs. The operator confirmed those clips played after direct copying to Tria's media folder. This release ports the format to C#. All six production fixtures also passed the C# parity checks and Ross reader validation. **The C# port and optional normalization still require an operator-controlled Tria playback check before unattended production use.**

## Quick start

1. Extract the self-contained Windows ZIP to a local folder on the RAID/10 GbE host.
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
7. Queue independent destination deliveries. Attempt each destination once, with global and per-destination limits. Validate local bytes while writing, flush, compare the remote byte length through filesystem metadata and publish the final name atomically. Never read remote video/audio or headers back for verification. A fresh attempt never overwrites or adopts an existing file; a persisted interrupted publication can reconcile an existing final of the expected byte length.
8. Delete each delivery clip after its own destination attempts finish, including partial/all-copy failure. OGP confirmation and other jobs do not hold its cache. Generate `<clip-name>.mp4` from its Finished source through a separate queue. A running proxy finishes normally; by default no additional proxies start while high-resolution jobs wait for stability, cache, processing, archival or delivery. Source-absent jobs, terminal failures and OGP waits do not block proxy admission.

New files join the native/normalization pipeline without waiting for earlier proxies or server copies. FIFO admission follows readiness for each stage, allowing a later native file to overtake a slow normalization. Proxy admission and worker assignment use the same lock as incoming registration. Native workers default to **1**, normalization to **1**, proxies to **1**, global copies to **4**, and copies per destination to **1**. A burst may finish all high-resolution deliveries while proxies remain queued. Proxy progress is separate from high-resolution progress; Probing means a worker is actually inspecting the source, while sources waiting for that worker show Pending.

Each job API includes persisted `timings`: `probe`, `wrap` (combined native streaming/audio packing or normalization/wrapping), legacy `prepare`, `archive`, `delivery:ID`, `copy-write:ID`, `copy-publish:ID` and `proxy`, with UTC start/end and measured seconds. Queue/readiness waiting is separate from active phase time. Delivery time includes the remote write, flush and publication, with no media readback. Timing metadata follows the job's source-owned lifetime.

After publication, request a fresh read-only OGP inventory without holding the copy slot. A completed row shows Copied; awaiting Tria until fresh OGP presence is confirmed, then green **OK**. OGP presence is the operator's acceptance criterion; no duration warning/status is shown. No SMB header read, full-file checksum readback or AMP fallback is performed. Availability remains separate from historical processing outcome.

There is one automatic copy attempt per destination. A completed unsuccessful attempt is not retried after restart. There is no delayed delivery queue, automatic restoration or re-sync. Delivery clips are temporary and are not an operator archive. Original MXFs remain in Finished; another import requires a new managed arrival and an available output/destination name.

Terminal ingest errors, unsupported formats and local name conflicts move the source to Failed. Failed-name collisions receive a unique suffix; the dashboard retains the original clip name. A source that physically cannot be moved stays visibly unresolved in Active and raises a system error. Rediscovery does not repeat conversion. A proxy failure after archival is reported separately and makes completion a warning; it never cancels high-resolution delivery or retries the proxy on restart. The original stays in Finished.

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

`/` has Active and Older tabs, newest arrival first. Arrival time is immutable; inventory updates do not reorder rows.

- **Active:** managed arrivals from the last hour plus unresolved Watch sources regardless of age.
- **Older:** older Finished/Failed entries and startup observations, retained while their sources exist.

Rows show arrival time, clip name, source format, processing stage/progress, proxy status and one column per configured destination. Stages include waiting for copy, pending, probing, native rewrap, transcoding, delivery, archiving and proxy generation. Completion is green when all destinations succeeded, yellow for partial delivery and failed when none succeeded. Local processing/archive/proxy can still succeed in the all-failed case. Format mismatch and other errors retain their source location and explanation.

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
| `/api/jobs?view=active` or `view=older` | Current job snapshot |
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
| `logging`: `level` | Trace, Debug, **Information**, Warning, Error, Critical or None; console output. |

`--validate-config` checks syntax, ranges and folder relationships, not credentials or live access. `/admin` provides live checks. UNC shares use the console account's credentials; mapped drives depend on its session.

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

## Measured broadcast test — 2026-11-27

Six real production MXFs (5.92, 30, 32.92, 36.04, 46.12 and 59.1 seconds) were ingested using D: media/work paths, one native worker, one proxy worker and one copy per destination. All six local clips, archived originals, server publications, OGP confirmations and proxies succeeded with no failures. All six native wraps completed **18.96 seconds before the first proxy finished**; the proxy queue reached five entries. No remote media/header checksum readback was performed. OGP presence is the green OK criterion.

For the 59.1-second clip in that busy six-file run:

| Phase | Seconds |
|---|---:|
| Probe | 0.45 |
| Native essence preparation | 2.83 |
| Native container writing | 7.62 |
| Probe + preparation + wrapping | **10.90** |
| Destination write/flush/publication | **58.97** |
| First observed arrival to publication, including receipt/readiness | **86.79** |
| First observed arrival to fresh OGP confirmation | **about 95.1** |

Proxy completion is excluded from those high-resolution times. The whole six-file test including all proxies took about 146 seconds from the first copy start. An isolated SMB write test reached about 250 MB/s; the first large write under concurrent ingest/proxy/storage load was substantially slower. A negotiated 10 Gb/s link does not establish sustained file-transfer speed. This run confirms pipeline independence; it does not demonstrate Tria Importer speed parity or isolate D: performance from the simultaneous delivery-validation change. A later controlled single-file, equal-validation comparison can isolate storage/network effects without a large burst.

## 0.3.0 qualification — 2026-11-27

Six unique live publications succeeded: one matched 0.2.1 baseline, the same 59.1-second production source on 0.3.0, and four shorter native sources on 0.3.0. Inputs were pre-staged before timed appearance; both versions retained 10-second stability/reconciliation settings and the same destination, one native worker and one copy per destination. No playback commands or remote media/header reads were used.

| Matched 59.1-second source | 0.2.1 | 0.3.0 |
|---|---:|---:|
| Probe + native preparation/wrapping | 20.22 s | **9.66 s** |
| Destination copy/flush/publication | 22.90 s | **19.08 s** |
| First detection to publication (includes readiness; excludes proxy) | 53.23 s | **39.19 s** |
| First detection to fresh OGP confirmation | 55.24 s | **48.95 s** |

That is 52% less active native-processing time and 26% less detection-to-publication time in this matched sample. OGP indexing/inventory delay varied between trials; green status is still fresh presence. Copying remains the largest active phase. These are single pre-staged trials with normal OS/controller caching and host activity, not cold-cache or sustained RAID/network benchmarks, guaranteed speedups, or Tria Importer parity claims.

In the four-file burst (5.92, 30, 46.12 and 32.92 seconds), wrapping overlapped delivery, copies stayed serialized to TLV1, and every delivery finished before any proxy started (0.38-second admission gap). All five 0.3.0 clips and proxies succeeded with fresh green OGP availability. Reported burst cache usage peaked at about 2.39 GiB with 3.23 GiB reserved; final usage and reservations were both zero. Each clip was released after its own copies, while other clips were still wrapping/copying. Cache accounting conservatively uses reservations when a transient ownership/metadata read is unavailable.

During the matched high-resolution windows, host-wide Windows logical D: disk-time counter averages fell from about 67% to 13%; average D: reads fell from 111 to 60 MB/s and writes from 107 MB/s to about 0.02 MB/s. Temporary writes moved to C:. These one-second logical-volume counters include host/cache activity and are not individual physical-drive utilization measurements. The data supports reduced D: contention on this host without establishing the physical PERC drive grouping.

Automated qualification passed **105 tests**, with the opt-in production case skipped in the general run and separately passing all six fixtures: unchanged compressed-video hashes, exact ordered PCM, duration/start timecode/final frame, and Ross metadata/native-reader acceptance. Regression tests cover source-based H.264/H.265 proxies, normalized framing/silence, active inherited-handle move/delete, cache quota/cleanup, restarts, independent queues, 50-file bursts and a simulated 500-clip day. Hardware playout of the new streaming writer and normalization remains an operator acceptance step.

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

Real-media cases require these environment variables and are skipped without them. Production media is absent from Git; `MEDIATAXI_PRODUCTION_MEDIA` optionally points to local fixtures for parity qualification. Synthetic tests verify native wrapping/extraction, exact PCM/channel packing, timecode, duration/final frame, normalization, H.264/H.265 MP4s and ownership tags. Filesystem/protocol tests cover growing copies, duplicates, conflicts, source moves, all/partial/no delivery, storage outages, cancellation, locked cleanup, safe name reuse, restart, OGP framing/staleness and HTTP/SSE. A 50-file burst and simulated 500-clip day exercise the real engine, SQLite and filesystem with fake media processing; these prove orchestration correctness, not encoder/network throughput.

```powershell
.\scripts\Publish.ps1 -MediaToolsDirectory 'C:\Tools\QualifiedFFmpeg'
```

Publish uses `.tools/dotnet` when present, otherwise `dotnet` from PATH. It creates a self-contained `win-x64` folder with native SQLite, assets, README, VERSION and INI, plus an optional FFmpeg pair. Default output is `artifacts/MediaTaxi-<VERSION>-win-x64` and `.zip`; existing output is not overwritten. Redistributed media tools retain their supplied licenses/notices. The published console needs no installed .NET runtime; FFmpeg remains a child executable dependency.

For upgrade, stop the previous console and retain its INI and SQLite state. Back up the original INI before editing it. Remove `folders.abekas`, set `folders.work = C:/MediaTaxiCache`, and add the cache and proxy-admission keys from the template. Keep the resolved state directory unchanged. On startup Media Taxi creates a consistent SQLite `pre-0.3.0.db` backup through SQLite's backup API and a `.pre-0.3.0.bak` INI copy once per state directory. A referenced, owned legacy clip can finish its existing attempts in place; completed legacy clips and specifically named legacy essence are removed through persisted ownership checks. No Abekas-directory sweep or old C: cleanup occurs. Untracked files and links are retained for review. Existing explicit worker settings remain authoritative: change `native = 2` to `native = 1` if desired. Relative paths change meaning if the INI is moved, so preserve their resolved locations. Completed 0.2.0 jobs remain completed; interrupted jobs keep their successful stages and completed failed destination attempts.

AMP Controller remains a separate read-only reference project. Media Taxi has no sibling-source runtime dependency. Agent/rule files, prompt archives, vendor research, production media, local configuration, runtime state and artifacts stay excluded from Git. Synchronize version metadata and this README for each delivered version; keep the changelog here.

## Changelog

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
