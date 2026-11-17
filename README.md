# OneDriveOrganizer

A .NET 10 C# console application for **local, personal OneDrive folders**. Analyze first; review CSV reports; then explicitly authorize reversible cleanup or organization. Phase 3 adds local CLIP inference and explicitly opted-in Azure enrichment. There are no Microsoft Graph or online geocoding calls. Use `--ai off` for the original non-AI analysis.

## Build and run

Requires a stable .NET 10 SDK (10.0.100 or a later 10.0 feature band/patch). `global.json` selects the latest installed stable .NET 10 SDK; both projects target `net10.0`. Framework-dependent runs require the .NET 10 runtime; the self-contained Windows publish includes it. Nullable, implicit usings and invariant globalization are enabled. The application uses System.CommandLine, Microsoft.Extensions.Hosting DI, SQLite, Magick.NET, MetadataExtractor, Serilog, CsvHelper, PdfPig and TagLibSharp. It does not use System.Drawing.

```powershell
dotnet restore OneDriveOrganizer.sln
dotnet build OneDriveOrganizer.sln
dotnet test OneDriveOrganizer.Tests\OneDriveOrganizer.Tests.csproj
dotnet run --project OneDriveOrganizer -- --help
```

The solution-local `NuGet.Config` isolates restores from unrelated machine-specific private feeds. If this environment cannot negotiate TLS with api.nuget.org, Microsoft's public mirror can be used explicitly without changing machine configuration:

```powershell
dotnet restore OneDriveOrganizer.sln --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
```

**No image-library license key is required.** Image decoding uses Apache-2.0-licensed [Magick.NET](https://github.com/dlemstra/Magick.NET), with bundled native ImageMagick components for Windows and Linux. It replaces ImageSharp without downgrading or bypassing license enforcement. Magick.NET was chosen over SkiaSharp to retain TIFF alongside JPEG, PNG, WebP, GIF and BMP support. Review and retain applicable third-party license/attribution notices when distributing, including ImageMagick's bundled dependencies and TagLibSharp's LGPL license.

### Self-contained single-file Windows publish

Release publishing does not require a Six Labors account, key or license file:

```powershell
dotnet publish OneDriveOrganizer\OneDriveOrganizer.csproj -p:PublishProfile=win-x64 -o artifacts\win-x64
.\artifacts\win-x64\OneDriveOrganizer.exe --help
```

Equivalent explicit switches are `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`. SQLite, Magick.NET and ONNX Runtime native libraries are bundled and extracted by the .NET single-file host; CLIP model weights remain separate local assets. Native libraries increase the executable size. Trimming is deliberately disabled for reflection-based serializers and metadata libraries. Linux is supported via `dotnet run` or a separately published `linux-x64` build. The Windows manifest opts into long paths; this app does not change the machine's `LongPathsEnabled` registry policy.

### Upgrading from the ImageSharp build

Commands, CSV columns, rules, review gates, undo logs and SHA-256/CRC32 duplicate detection are unchanged. Existing image/video analysis is refreshed once on the next scan, even when size and mtime are unchanged; content hashes are preserved. The replacement still uses a 64-bit dHash, the same Hamming threshold and keeper selection. Decoder/resampling differences can change individual perceptual hashes or borderline photo classifications, so the index records an analysis version rather than mixing old and new hashes. Previously generated `photos` and `classify` reports must be regenerated and reviewed before applying them. Exact-duplicate/archive reports and undo journals remain compatible. No files are moved or deleted by this cache refresh.

## Safety model - read before applying anything

* Start with an independent backup and a small test copy. Pause OneDrive synchronization and close applications editing the files during review/apply/undo. The app lock serializes its own processes; it cannot lock out OneDrive or other programs across a multi-file operation.
* `clean`, `organize`, `rename`, and **`undo`** are dry-run by default. Execution requires **both `--reviewed` and `--no-dry-run`**. The first flag attests that you actually reviewed the report; software cannot determine whether a human read it.
* Reports have a required sibling `<report>.manifest.json` containing the root, exclusions, original rows and full SHA-256 proofs. Keep both files together and treat them as trusted local control files, not authenticated documents received from someone else. The app validates paths, proofs, keepers, links, exclusions and collisions again before applying work. It rejects stale reports before the batch and rechecks immediately before each action.
* You may remove CSV rows or change `Keep` from `False` to `True`. Do not change paths, targets, categories, confidence, or other fields; regenerate using different options/rules instead. You cannot turn a protected/keeper row into a deletion candidate. Never edit the manifest. Use an editor that preserves ISO timestamps, quoting and Unicode; import paths as text in spreadsheets rather than evaluating formula-like filenames.
* Reports never overwrite existing outputs. Give every analysis run a fresh `--out`. Do not combine deletion reports generated before a previous cleanup: regenerate after each applied report because a previously kept file might have moved or disappeared.
* Windows cleanup defaults to `SHFileOperationW` with `FOF_ALLOWUNDO`, **after a verified local undo backup is made**. Long/UNC paths use `_Trash` instead because the legacy Shell API rejects extended paths. `--recycle false`, and all non-Windows cleanup, move into `_Trash/<unique-id>/` inside the root. No user file is hard-deleted by this application.
* Recycle Bin plus backup uses extra space. `_Trash` may itself sync to OneDrive. No automatic trash purging is provided: a zero-space-reclaimed figure is honest until you independently dispose of retained backups. OneDrive personal Recycle Bin retention is normally **30 days**; this is not a backup guarantee.
* Undo journals are atomically replaced and flushed before each action and after completion. Interrupted copies remain in journaled staging paths. Undo reconciles prepared/applied entries, refuses to overwrite changed files and can be repeated. Undoing a copy moves its duplicate into `_Trash` rather than deleting it. Keep journals and backups until satisfied. Empty destination directories may remain after undo; they are not recursively removed.
* `.lnk`, `.loop` (case-insensitive), `desktop.ini`, `Thumbs.db`, `.DS_Store`, OneDrive's internal `.849C9593-D756-4E56-8D6E-42412F2A707B` sync file, symbolic links/junctions, explicit `--exclude` paths, `.odo`, `_Trash`, and registered reports/journals are not scan/mutation candidates. Loop files are excluded even with `--skip-offline false`; cached entries are removed on refresh and older reports cannot execute operations on them. A repository with protected, excluded, linked or offline descendants is not moved. Git metadata is excluded from individual file indexing but is preserved when a complete repository is moved.
* Offline and `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x400000)` placeholders are skipped by default, including offline directories. Skipped entry counts are logged. `--skip-offline false` explicitly permits reads that may download files; keep OneDrive running and connected during those downloads. Skipping an offline directory counts the directory, not every unenumerated child. Once downloads finish, pause sync for a stable review/apply snapshot.
* Indexing makes **three attempts total per file**, with a cancellable one-second pause between attempts, for file-read I/O and access-denied errors, including cloud download failures and locked files. After the third failure, it logs the path, HRESULT and reason, omits the file from the refreshed index even if previously cached, and continues. The final summary counts locked and other unreadable files separately. Skipped files are retried on the next scan; a completed report with skipped files covers only successfully indexed files. Protected-path checks, files changing during a scan, index/database failures and cancellation still stop the command.
* A failed file read during hashing logs `Cannot read file <path> (HRESULT 0x...)` before propagating the original error. If OneDrive says a cloud download requires action, check its notifications and the named file's download status; retries cannot override the provider's request. Resolve the provider error and scan again with a fresh report filename to include skipped files. `--parallelism 1` can make failures easier to isolate. Later full-hash/report-proof checks and destructive operations remain fail-closed; they do not skip failed reads or enable write-sharing to bypass locks.
* RAW+JPEG and matching-stem Live Photo MOV+HEIC/JPEG companions stay together; clean normally protects both. `photos --drop-jpeg-when-raw` explicitly proposes dropping a JPEG, not a MOV companion. Edited plans that would split a pair are refused. Companion matching is a conservative same-directory/same-basename heuristic, not Apple's content-identifier matching.
* Low-confidence `_Review` targets are **suggestions only**: `organize` does not move those rows. Revise rules and regenerate a sufficiently confident plan after inspection. Sensitive identity documents are flagged; reports/logs include private filenames and should be protected accordingly.
* Encryption detected in PDF trailer/xref metadata, ZIP entry flags, or modern Office compound-file `EncryptionInfo` + `EncryptedPackage` streams produces a `PasswordProtected` row targeting `_PasswordProtected/<original filename>`, with `Status=Move`, `Confidence=1`, and `Sensitive=True`. No passwords are requested or guessed, and recognized encrypted content is not decrypted, extracted, or submitted to AI/OCR, even with sensitive-cloud opt-in. Header/container metadata and opaque bytes for safety hashes are still read. OpenMcdf 3.4.0 ([MPL-2.0; source](https://github.com/openmcdf/openmcdf)) supplies read-only Office directory inspection without a license key.
* Protected-file moves happen only through a newly generated, reviewed classification plan and `organize --reviewed --no-dry-run`, with the usual collision handling and undo journal. Custom rules cannot override this routing; files already under `_PasswordProtected` remain there. Atomic repositories stay intact rather than having individual protected files removed. Regenerate older classification plans to get these targets. `zips` keeps encrypted ZIPs and reports them as `Protected` without opening member contents.
* Encryption detection is format-specific, **not universal**: legacy Office encryption, 7z/RAR and other proprietary protection schemes are not currently detected. Detected formats with malformed or unsupported protection metadata are logged and left in place as `Review`, regardless of confidence settings; do not treat an unrecognized file as proven unencrypted.

## Recommended run order

**index -> dupes -> zips -> photos -> folders -> review -> clean -> classify -> review -> organize -> report**

Use a report directory outside the OneDrive root when possible. The following PowerShell commands assume that directory already exists and explicitly disable optional AI. For the default local AI mode, install models as described below and omit `--ai off`.

```powershell
$root = "$env:USERPROFILE\OneDrive"
$app = ".\OneDriveOrganizer\OneDriveOrganizer.csproj"

dotnet run --project $app -- index $root --ai off --out reports\index.csv --exclude PersonalVault Work

dotnet run --project $app -- dupes $root --out reports\dupes.csv --keep oldest --exclude PersonalVault Work
dotnet run --project $app -- zips $root --out reports\zips.csv --exclude PersonalVault Work
dotnet run --project $app -- photos $root --ai off --out reports\photos.csv --threshold 6 --exclude PersonalVault Work
dotnet run --project $app -- folders $root --out reports\folders.csv --exclude PersonalVault Work

# Inspect the CSVs and their keepers. No changes without BOTH flags.
dotnet run --project $app -- clean reports\dupes.csv
dotnet run --project $app -- clean reports\dupes.csv --reviewed --no-dry-run --recycle --undo-log reports\clean-undo.json

# Regenerate zips/photos reports before applying them after any earlier cleanup.
# Folder reports are advisory only, never accepted by clean.

dotent run --project $app -- classify $root --ai off --out reports\plan.csv --min-confidence 0.7 --exclude PersonalVault Work
# Review every target, sensitive item, and low-confidence suggestion.
dotnet run --project $app -- organize reports\plan.csv
dotnet run --project $app -- organize reports\plan.csv --reviewed --no-dry-run --undo-log reports\organize-undo.json

dotnet run --project $app -- report $root --out reports\summary.html --exclude PersonalVault Work

# Reverse journals in reverse execution order, after reviewing the undo dry run.
dotnet run --project $app -- undo reports\organize-undo.json
dotnet run --project $app -- undo reports\organize-undo.json --reviewed --no-dry-run
```

Every analysis command refreshes the index, so calling `index` first is recommended but not required. Repeat exclusions on analysis commands; manifests carry those exclusions into execution. Index/partial hashes are cached in `<root>/.odo/index.db`. The index stores full paths, sizes, UTC timestamps, attributes, extension, partial/full hashes, CRC32 when needed, dimensions, metadata, dHash and conflict candidates. Unchanged size + mtime reuses cached analysis. Full SHA-256 is lazy during indexing/dedup candidate selection; producing an executable report additionally hashes its referenced bytes for stale-report protection. Do not hand-edit the SQLite file.

Logs: `%LOCALAPPDATA%\OneDriveOrganizer\logs` on Windows, the platform's local application-data directory on Linux; daily rolling files, 14 retained. Exit code 0 means success, 1 means operation failure, 130 means cancellation; parser failures are also nonzero. Ctrl+C cancels work and retains recoverable journals. Progress is printed as bounded-worker counts rather than terminal-specific progress widgets.

## Commands and behavior

| Command | Important options and output |
|---|---|
| `index <root>` | `--out index.csv`, `--exclude <paths...>`, `--skip-offline true`, `--parallelism 1..64`; conflict candidates reported separately via `Conflict`/`Status` |
| `dupes <root>` | `--keep oldest` / `newest` / `shortest-path` / `prefer-folder`; use `--keep prefer-folder --prefer-folder Pictures`; size -> first 64 KiB SHA-256 -> full SHA-256 |
| `zips <root>` | Member size + central-directory CRC32, with streamed SHA-256 confirmation before proposing removal; `Redundant`, `Partial`, `Unique`, `Unsupported`; nested ZIPs and duplicate archive content signatures reported |
| `photos <root>` | `--threshold 6` (0..16), `--drop-jpeg-when-raw`; dHash/union-find; highest resolution, then largest bytes, then EXIF date keeper; direct keeper-distance checks protect transitive outliers |
| `folders <root>` | `--out folders.csv`; duplicate/subset content **multisets** among indexed descendants; filenames/layout are not required to match; ancestor/child comparisons omitted; always advisory |
| `classify <root>` | `--rules rules.json`, `--min-confidence 0.7`, `--stale-years 3`, `--learn`; `Path,DetectedType,Category,Confidence,TargetPath,Reason` plus safety/status columns |
| `clean <report.csv>` | `--reviewed --no-dry-run`, `--recycle true`, `--undo-log <new.json>` |
| `organize <plan.csv>` | `--reviewed --no-dry-run`, `--copy-instead-of-move`, `--undo-log <new.json>`; short-hash collision suffix, never overwrite, preserve timestamps |
| `search <root> "<text>"` | Local text-to-image search over current stored embeddings; `--top-k 10`, `--clip-model-dir`, exclusions/offline policy |
| `rename <plan.csv>` | Apply `SuggestedFileName` within the source directory; dry-run, review gates, collision handling and undo; regenerate the organize plan afterward |
| `undo <undo.json>` | Default dry-run; `--reviewed --no-dry-run` to restore |
| `report <root>` | `--out summary.html`; self-contained HTML with inline SVG, removed-byte totals, duplicate suggestions, categories, review/sensitive lists, conflicts, largest files, stalest folders |

CSV files share a superset schema for reliable round trips. `Keep=False` is a suggestion, not permission to delete. Name patterns such as `-DESKTOP-ABC123`, `(1)`, and ` - Copy` are possible sync conflicts, never proof of duplicates.

### Classification and rules

Built-in classification uses magic bytes/container structure before filename extension, then EXIF/QuickTime metadata, first-page PDF/leading DOCX text, technical text signatures and audio tags. Photos use metadata date first, creation date second, parsed filename date third. No online reverse-geocoding is attempted; GPS presence is recorded in the index. Media subfolders include Screenshots, WhatsApp, Scans, Memes and Videos; scans/memes are low-confidence heuristics. Documents use PDF/Word/Excel/Presentations/Text folders and Finance, Legal, Medical, Education, Travel or Identity topics. Git repositories remain atomic; scripts/config go to Code; recognized archives/installers get age-based Stale suggestions; audio uses artist/album tags or Recordings; unknown content goes to Misc and usually `_Review`. Existing category-matching folders are marked `AlreadyPlaced`.

Valid BOM-marked UTF-16/UTF-32 text is recognized before MPEG audio sync bytes: the shared `FF FE` prefix must not send Unicode diagnostic/text files to Music. Detection validates the sampled text, including samples ending mid-character, rather than trusting a `.txt` extension. File bytes and encoding are never rewritten. Regenerate and review classification plans created before this fix; existing indexed files are reclassified without rebuilding the index.

`OneDriveOrganizer/rules.json` is embedded. A custom file is a JSON array; matching names replace defaults, other rules extend them. Higher `priority` wins. Matcher groups combine with **AND**; alternatives inside extensions, magicBytes or textKeywords combine with **OR**. Regexes have a one-second timeout. A rule can specify `extensions`, hexadecimal `magicBytes` prefixes, `filenameRegex`, `exifPresence`, `textKeywords`, `minSize`, `maxSize`, `minWidth`, `maxWidth`, `minHeight`, `maxHeight`, `target`, `confidence`, and `sensitive`. Targets must remain inside root and outside exclusions. Supported templates: `{yyyy}`, `{yyyy-MM}`, `{Extension}`, `{ProjectName}`, `{Artist}`, `{Album}`. Repository/companion protection outranks custom rules.

```json
[
  {
	"name": "Documents/PDF/Finance",
	"priority": 200,
	"magicBytes": ["25504446"],
	"textKeywords": ["tax", "Revenue", "P60"],
	"minSize": 100,
	"target": "Documents/PDF/Finance/Tax",
	"confidence": 0.95
  }
]
```

`--learn` writes `<plan>.suggested-rules.json` for extensions with at least three examples and a 70% dominant existing folder. Suggestions are never automatically enabled and must be inspected for overgeneralization.

## Phase 3: tiered AI enrichment

`index`, `photos`, `classify`, and `search` default to **`--ai local`**. `--ai off` retains the non-AI pipeline; `--ai cloud` enables local inference plus Azure enrichment of low-confidence classification candidates. No analysis command silently downloads models. Missing/incompatible local assets cause an actionable error, not a silent fallback. Search requires local models even if `--ai cloud` is supplied and never calls Azure.

### Local CLIP setup

Run the explicit downloader once (approximately 155 MB; public model downloads only, no local content uploads):

```powershell
.\tools\Download-ClipModels.ps1
dotnet run --project OneDriveOrganizer -- index $root --out reports\ai-index.csv
dotnet run --project OneDriveOrganizer -- photos $root --out reports\ai-photos.csv --cosine-threshold 0.92
dotnet run --project OneDriveOrganizer -- classify $root --out reports\ai-plan.csv
dotnet run --project OneDriveOrganizer -- search $root "a dog playing outside" --top-k 10
```

Default model location: the platform's local application-data directory under `OneDriveOrganizer\models\clip-vit-b32`. Override with `--clip-model-dir <directory>` or `ONEDRIVE_ORGANIZER_CLIP_MODELS`. This workspace also has verified assets at `artifacts\clip-models`; pass that directory explicitly. Downloaded models and published binaries are not included in Git; generate them after cloning. Keep model assets outside OneDrive (or under its protected `.odo` directory).

The downloader pins [Xenova's OpenAI CLIP ViT-B/32 ONNX export](https://huggingface.co/Xenova/clip-vit-base-patch32) at revision `6b7c523703ae26e39be4b03774966f8571c7f523`, verifies published SHA-256 checksums for both quantized models, and never overwrites existing assets. Models are MIT-licensed; retain applicable model notices when redistributing. You may instead supply trusted full-precision exports with the same contract:

* `vision_model.onnx`: float32 `pixel_values` input `[1,3,224,224]`, projected float32 `image_embeds` output `[1,512]`.
* `text_model.onnx`: int64 `input_ids` `[1,77]`, optional int64 `attention_mask`, projected float32 `text_embeds` `[1,512]`.
* Matching OpenAI CLIP `vocab.json` and `merges.txt`, BOS 49406 / EOS-and-padding 49407.

CPU inference uses Microsoft.ML.OnnxRuntime and Microsoft.Extensions.AI's `IEmbeddingGenerator`. Images use the same signature-whitelisted raster decoder as dHash, EXIF orientation, RGB, bicubic short-edge resize/center crop, and CLIP channel normalization. Text uses byte-level BPE, Unicode/HTML normalization, and EOS-preserving truncation. It does not repair malformed/mojibake text with Python's `ftfy`. Extreme-aspect-ratio images exceeding a 50-megapixel resized working image are refused.

Normalized 512-D float32 embeddings are stored as 2,048-byte SQLite BLOBs by SHA-256, input kind and a fingerprint of all model/tokenizer files plus preprocessing version. Identical bytes share inference results; unchanged indexed files reuse their stored vectors. Model changes cannot mix vector spaces. Text queries and label embeddings are cached too. Source files are never modified by inference.

`photos` merges dHash and CLIP cosine edges into the same union-find. A metric tree preserves cosine-neighbor recall; dense/high-dimensional data can still approach quadratic work. A removal suggestion must directly match its keeper by dHash **or** cosine, and existing repository, RAW/Live Photo, and video protections still apply. Semantic similarity is not proof of redundant content: inspect the actual images.

Embedded `rules.json` contains ten `aiLabel` entries: people, pets, food, documents, screenshots, landscapes, vehicles, receipts, whiteboards, memes. These rules are excluded from deterministic matcher evaluation. Override them by name using `--rules`; `target` uses the existing templates, optional `aiCategory` controls the report category, and `sensitive` controls local privacy flags. Deterministic matching rules and companion placement take precedence over AI placement. Label confidence is a softmax of CLIP cosine scores at scale 100, **not a calibrated probability**. Sensitive label posterior >= 0.1 conservatively flags document/receipt/whiteboard/screenshot candidates even when another label wins.

### Optional Azure tier

Only `classify --ai cloud` can upload file content. Set `AZURE_OPENAI_ENDPOINT` to your HTTPS resource endpoint and optionally `AZURE_OPENAI_DEPLOYMENT` (default `gpt-4o-mini`, with vision and structured-output support). Authenticate with `AZURE_OPENAI_API_KEY` (or `AZURE_OPENAI_KEY`), or omit the key to use `DefaultAzureCredential`/Entra. Never put keys in rules, source files, command-line arguments, or reports.

Scanned PDFs additionally need `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` and optionally `AZURE_DOCUMENT_INTELLIGENCE_KEY`; without the key, Entra is used. Grant the relevant Azure data-plane permissions. First-page scans use prebuilt-invoice, prebuilt-receipt or prebuilt-idDocument when local evidence identifies the type, otherwise prebuilt-read. This adapter uses the official 2024-11-30 REST API rather than an extra SDK package. Files are limited to 50 MiB, analysis requests specify page one, and accepted asynchronous operations are polled without resubmitting their documents. Azure receives the PDF bytes even though only page one is analyzed.

```powershell
# Set endpoint/deployment and credentials securely in your environment first.
dotnet run --project OneDriveOrganizer -- classify $root --ai cloud --out reports\cloud-plan.csv --min-confidence 0.7 --exclude PersonalVault Work
```

Only confidence **below** `--min-confidence` is eligible. A successfully inspected PDF with no first-page text gets a low scan-candidate score. Undecodable images and documents without usable extracted content stay local. Repositories and media pairs are not cloud-enriched or automatically renamed. Chat receives a metadata-stripped image thumbnail (up to 1024 pixels per side), or bounded extracted text. Existing filename text is included as classification evidence. No tools/functions execute from AI output.

The response schema has exactly `category`, `subcategory`, `confidence`, `suggestedFileName`, `isSensitive`, `reason`. Local validation rejects missing/extra/duplicate properties, wrong types, out-of-range/nonfinite confidence, unknown top-level categories, and unsafe path components or filenames. Cloud output cannot clear a prior sensitivity flag. Malformed results are cached and rejected rather than triggering another billable classification.

**Privacy:** Tier 0/1-sensitive files are blocked before either Azure upload unless `--allow-sensitive-cloud` is explicitly supplied. Sensitivity discovered by OCR blocks the subsequent chat call and produces a sensitive review item. Keyword and CLIP checks are heuristic, not a comprehensive DLP system; unreadable/scanned documents can contain undetected personal information. Exclude private folders rather than relying solely on classifiers. The override permits both OCR and chat disclosure.

All outbound attempts log content hash, provider and token usage (pending at dispatch; OCR is page-billed and logs zero tokens). Cloud calls are serialized with a one-second minimum start interval; explicit 429/503 responses get up to three exponential-backoff retries with jitter. Hidden OpenAI SDK retries are disabled. Successful OCR and chat responses persist independently by content hash/provider configuration. A pending ledger entry is flushed **before** dispatch; a timeout, crash or ambiguous failure refuses automatic replay on later runs. Inspect the provider operation/history and local ledger before any deliberate recovery. Network retries and provider-side failures mean exactly-once billing cannot be guaranteed; the app never promises it.

The cache is per OneDrive root and provider/model configuration, not global. Changing a deployment/model or deleting `.odo` invalidates that cache scope. SQLite contains unencrypted embeddings, model responses and possibly sensitive OCR text; protect `.odo` and the rolling logs. Because `.odo` is inside the synced root, OneDrive itself may sync it. This privacy policy governs additional Azure AI uploads, not OneDrive's existing synchronization.

### Reviewed descriptive renaming

Only eligible cloud-enriched rows receive descriptive filename suggestions; CLIP alone is not a caption generator. Review `SuggestedFileName`, confidence and `Sensitive` alongside the normal plan fields. Newly added CSV columns are optional when reading legacy reports; original required safety columns remain mandatory.

```powershell
dotnet run --project OneDriveOrganizer -- rename reports\cloud-plan.csv
dotnet run --project OneDriveOrganizer -- rename reports\cloud-plan.csv --reviewed --no-dry-run --undo-log reports\rename-undo.json
dotnet run --project OneDriveOrganizer -- undo reports\rename-undo.json --reviewed --no-dry-run
```

Rename preserves the original extension, folder and timestamps, refuses unsafe names, never overwrites, and uses the existing full-content proofs, hash collision suffixes and durable Move undo entries. Review/protected/directory rows are not renamed; companion stems must remain together. Remove rows to decline suggestions; do not edit the manifest or generated suggestions. **Renaming invalidates the old organize plan: regenerate and review classification before organizing.**

## Deliberate trade-offs and limits

* **HEIC/RAW remain metadata-only where MetadataExtractor can parse them; their visual decoding is not enabled in this migration.** These appear as metadata-only/unsupported visual comparisons and are never silently discarded. JPEG, PNG, WebP, GIF, BMP and TIFF use Magick.NET; only the first frame is analyzed, with a 100-megapixel source guard and analysis images resized to at most 2048 pixels per side. Decode memory depends on source dimensions. Inputs are opened as streams and restricted by magic bytes to raster coders, not interpreted as ImageMagick pseudo-paths, SVG, PDF, or external delegates. No System.Drawing fallback.
* **Video near-duplicates are opt-in, advisory only.** Supply trusted local `--ffmpeg <path>` and `--ffprobe <path>` to `photos`. Duration within one second plus first-frame dHash produces candidates, not proof: all video candidates are kept. Decoders have a timeout and bounded piped output; no extracted frame is written to disk. First frames cannot establish identical videos.
* 7z/RAR are explicitly unsupported; ZIP processing is streamed, never extracted. A ZIP over 100,000 members or 2 GiB advertised uncompressed size is retained for manual inspection. Redundancy means member content exists loose, not that archive folder layout, comments, timestamps, encryption or compression settings are preserved. Keep important original archives if those details matter. Empty ZIPs are never redundant.
* Above 20,000 decoded images, multi-index hash bands replace unrestricted comparisons. Unlike a single prefix bucket, `threshold + 1` disjoint bands preserve threshold-neighbor recall. Identical hashes are collapsed. Highly clustered nonidentical hashes can still degrade toward quadratic comparisons; this is not a distributed similarity index.
* Perceptual hashes can collide (especially flat-color images) and union-find is transitive. All photo removal suggestions require visual review; transitive members outside keeper distance and media companions are protected. With AI disabled, WhatsApp/screenshot/scanned-document/meme classification is purely heuristic; CLIP and cloud predictions can also be wrong.
* PDF inspection uses page one; DOCX has no stable page model without a layout engine, so a bounded leading XML text window substitutes for page one. Text inspection is limited to 50 MiB files and 10 MiB DOCX XML. Legacy Office's shared compound-file signature needs an extension hint. Unknown/invalid content is retained or causes a logged nonzero failure, not deletion.
* Linux creation time is filesystem-dependent; timestamp APIs are used but arbitrary ACLs, NTFS alternate streams and extended attributes are not cloned by stream-based copy. Moves on the same filesystem preserve the underlying file. Keep an independent backup if those metadata matter. Cross-filesystem directory moves fail explicitly rather than partially copying a repository.
* Review rows remain in place, rather than automatically moving ambiguous or sensitive material into `_Review`. Classification report history is advisory; regenerate after changes. HTML numbers are not a cloud quota measurement and physical space reclaimed is not claimed while reversible backups remain.
* JSON manifests/journals are local trust artifacts, not cryptographically signed authorization. Rechecks narrow race windows but cannot make a multi-file operation atomic against concurrent writers or malicious local processes. Run on a quiescent folder.

## Tests

xUnit coverage includes staged hashing, CRC32, dHash/Hamming, exact similarity thresholds and a 20,001-image bucketing case, filename dates, conflict detection, both placeholder flags, rule matching, strict CSV round-trip, incremental indexing, exclusions, magic-byte detection, collision handling, ZIP/image duplicate reports, review gates, stale-report refusal, clean/organize/copy undo, companion preservation, protected repositories and HTML escaping. Tests generate isolated temporary data and do not touch your OneDrive. Native Windows recycling and real cloud-placeholder behavior require separate interactive testing on a disposable OneDrive tree.

The image-backend regression tests additionally cover every previously decoded raster format, TIFF/GIF first-frame behavior, EXIF orientation/date/camera metadata, source-byte preservation, legacy cache refresh, and rejection of obsolete visual reports. Release builds no longer require a Six Labors license. Linux runtime behavior has not been exercised in this Windows workspace.

AI tests mock `IChatClient` and `IEmbeddingGenerator` to exercise persistent content caching, concurrent single-flight calls, privacy and confidence gates, OCR sensitivity escalation, strict schema validation, BLOB shape/model isolation, cosine grouping against brute force, local labeling/search, legacy CSV compatibility, and reviewed rename/collision/undo. Real CPU ONNX inference, 512-D output and semantic search have also been exercised with the pinned models and generated test images. To include that opt-in test locally:

```powershell
$env:ONEDRIVE_ORGANIZER_TEST_CLIP_MODELS = (Resolve-Path .\artifacts\clip-models).Path
# Optional: also exercise index/photos/classify/search/rename through the published executable.
$env:ONEDRIVE_ORGANIZER_TEST_EXE = (Resolve-Path .\artifacts\win-x64\OneDriveOrganizer.exe).Path
dotnet test OneDriveOrganizer.Tests\OneDriveOrganizer.Tests.csproj -c Release
```

Without the model variable the real-model test is explicitly skipped; mocks do not require model downloads, credentials or Azure connectivity. The Release single-file executable was exercised with local CLIP and generated fixtures. Azure integration was validated with mocks, not live service calls.

## Next additions

Microsoft Graph `quickXorHash`/delta indexing to reduce local reads; validated HEIC/RAW visual decoding; stronger Live Photo content-identifier pairing; multi-frame video fingerprints; optional offline city lookup data; calibrated labels and local OCR/captioning; an interactive visual review UI; richer PDF/Office parsers; copy support for ACLs/alternate streams; signed review receipts; and opt-in, separately reviewed backup retention management. The application now targets .NET 10 LTS.
