# Diffusion Toolkit Shareability Audit

**Audit date:** 2026-07-20  
**Branch reviewed:** `feature/metadata-overlay`  
**Commit reviewed:** `a56d376`  
**Purpose:** Identify the problems that currently make this fork unsafe, misleading, unreliable, or difficult to share with other users.

## Executive verdict

This fork should **not be distributed in its current form**, especially not by copying an existing `bin` directory or locally produced release archive.

The most urgent problem is not merely code quality: the project file recursively copies the entire local `Diffusion.PyScripts` tree into build output. On the audited machine, that output includes active-looking CivitAI cookie exports, old cookie exports, a state database, a multi-megabyte log, and a machine-specific Python virtual environment. These files are ignored by Git, but Git ignore rules do not affect MSBuild packaging.

The tracked source tree did not contain an obvious plaintext API key, cookie file, private key, or password in the checks performed for this audit. That is good, but it does not make the project ready to share. The tracked source still contains personal paths, personal collection identifiers, a personal external-database integration, unfinished features that throw at runtime, unsafe release/update behavior, undocumented account automation, outdated build instructions, a vulnerable package version, thousands of compiler warnings, and no real automated test suite.

There is also a serious authentication design concern: the Python client intentionally copies cookies exported for `.civitai.com` onto the unrelated `civitai.red` hostname. Unless the ownership and trust relationship of that domain can be independently established and explained to users, this must be treated as a credential-disclosure risk and a release blocker.

### Bottom line

- **Do not share existing Debug/Release output.**
- **Do not publish a release from the current working tree.**
- **If any locally built package has already been shared, invalidate the relevant CivitAI sessions and remove the package.**
- The repository can become shareable, but it needs a deliberate sanitization and stabilization pass first.

## Severity definitions

| Severity | Meaning |
|---|---|
| **P0 — Critical** | Potential credential/privacy exposure or behavior that makes distribution immediately unsafe. Must be fixed before sharing any build. |
| **P1 — Release blocker** | A security, data-loss, correctness, installation, or trust problem that makes a public release unsuitable. |
| **P2 — Serious** | Important reliability, maintainability, privacy, or usability debt that should be addressed before calling the fork stable. |
| **P3 — Polish/debt** | Does not necessarily block a preview release, but makes the project look unfinished or creates avoidable contributor/user friction. |

---

## P0 — Critical findings

### P0-1. Local credentials and private runtime data are copied into application builds

**Evidence**

`Diffusion.Toolkit/Diffusion.Toolkit.csproj` recursively includes everything below `..\Diffusion.PyScripts\**\*` and copies it to the output directory:

```xml
<None Include="..\Diffusion.PyScripts\**\*">
  <Link>Diffusion.PyScripts\%(RecursiveDir)%(Filename)%(Extension)</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

The local ignored scraper directory contained, and the audited Release output reproduced:

- `civitai.com_cookies.txt`
- `civitai.com_cookies_old.txt`
- `civitai.red_cookies.txt`
- `civitai_state.db`
- `civitai_downloader.log` (approximately 4 MB)
- `.venv/` (5,735 files, approximately 115 MB)
- `.venv/pyvenv.cfg`, including the local path `C:\Users\trunk\AppData\Local\Programs\Python\Python311`

The existing Release directory contained more than 12,000 files and was approximately 708 MB. It included thousands of cached Python bytecode files as well as the private files above.

**Why this blocks sharing**

Cookie export files can represent active authenticated sessions. The log and database can reveal account activity, collection names, image identifiers, file paths, and other personal information. The virtual environment reveals machine details and is neither portable nor a legitimate distribution strategy.

This is particularly deceptive because the sensitive files are ignored by Git. A maintainer may correctly see a clean Git status and still unknowingly package them.

**Required remediation**

1. Never publish or send the current `bin`, `obj`, scraper `.venv`, or any archive made from them.
2. If a package containing these files has left the machine, log out/invalidate the associated CivitAI sessions.
3. Replace the broad MSBuild glob with an explicit allowlist of required Python source/config-template files, or add robust exclusions for:
   - `.venv/**`
   - `__pycache__/**`
   - `*.pyc`
   - `*cookies*.txt`
   - `*.db`, `*.db-*`, `*.sqlite*`
   - `*.log`
   - user configuration and browser profiles
4. Build releases only from a clean checkout in CI and inspect the produced archive with an automated denylist check.
5. Add a secret scanner to both the current tree and Git history before making the repository or releases public.

### P0-2. CivitAI session cookies are deliberately copied to a different registrable domain

**Evidence**

The tracked `Diffusion.PyScripts/Civitai Collections Scraper/config.yaml` configures:

- CDN: `civitai.com`
- API: `https://civitai.red/api`
- tRPC: `https://civitai.red/api/trpc`

`api_client.py` contains logic and comments explaining that cookies exported for `.civitai.com` would not normally be sent to `civitai.red`, then registers a copy for the API host. The UI and documentation instruct the user to export CivitAI cookies, while authenticated write operations are routed through this session.

**Why this blocks sharing**

`civitai.com` and `civitai.red` are different registrable domains. Browser cookie isolation normally prevents one from receiving the other's session cookies. Circumventing that boundary can disclose a user's authenticated session to another operator.

This audit did not establish who owns or operates `civitai.red`; therefore it does not assert malicious intent. The safe conclusion is that the current design must be treated as a critical credential-handling risk until the domain relationship is verified and clearly disclosed.

**Required remediation**

1. Stop mirroring session cookies across domains.
2. Prefer documented official CivitAI endpoints and a narrowly scoped supported authentication mechanism.
3. If a proxy is genuinely required, document its owner, source, deployment, privacy behavior, retention policy, and threat model, and make it an explicit opt-in rather than a hidden default.
4. Separate read-only browsing from account-mutating operations.
5. Add an in-product confirmation explaining exactly where credentials and authenticated requests go.

---

## P1 — Release blockers

### P1-1. The checked-in default configuration contains personal paths and personal collections

**Evidence**

`Diffusion.PyScripts/Civitai Collections Scraper/config.yaml` contains:

- Personal `E:\...` image archive paths.
- `C:\Users\trunk\...` state-database paths.
- Personal collection names and numeric collection IDs.
- A browser-profile name, `Profilo Pezzotto`.
- A corrupted personal name rendered as `JosÃ¨`.

Additional personal paths occur in:

- `Diffusion.Toolkit/Scripts/Civitai_Collections_Pipeline.bat`
- `Diffusion.Toolkit/Scripts/NSFW Civitai Collections Scraper.bat`
- `Diffusion.PyScripts/...` batch scripts
- `Diffusion.Toolkit/Pages/Settings.xaml`
- `TestBed/MainWindow.xaml.cs`
- `Diffusion.Updater/Properties/launchSettings.json`
- `backfill_usernames.py`
- `AGENTS.md`
- `.claude/claude.md`
- Historical commits, even where the current file has changed

**Impact**

New users receive configuration that points to directories they do not own and collections they did not select. It also exposes the maintainer's username, directory layout, workflow names, collection taxonomy, and fork details. On systems where similarly named drives or folders exist, a default can write to an unintended location.

**Required remediation**

- Replace the tracked live configuration with a safe `config.example.yaml` containing placeholders and all collections disabled.
- Create user configuration on first run under `%APPDATA%\DiffusionToolkit`, never inside the installation tree.
- Require the user to select a download root before enabling downloads.
- Remove personal paths and examples from source, scripts, documentation, IDE settings, and agent notes intended for publication.
- Decide whether historical paths are sensitive enough to justify rewriting Git history. Removing them in a new commit does not remove them from prior commits.

### P1-2. A fresh clone and a developer machine have opposite Python packaging failures

**Evidence**

The C# application requires an exact bundled interpreter path such as:

`Diffusion.PyScripts/Civitai Collections Scraper/.venv/Scripts/python.exe`

This assumption occurs in:

- `Diffusion.Toolkit/MainWindow.xaml.cs`
- `Diffusion.Toolkit/Pages/Settings.xaml.cs`
- `Diffusion.Toolkit/Services/CivitaiPostsService.cs`

A clean archive of the 580 tracked files built successfully, but it contained no `.venv`, so the CivitAI functions could not run. On the development machine, the broad copy rule did find `.venv`, but it also packaged machine-specific binaries and private data.

**Impact**

- Clean clone: the advertised CivitAI features are installed but unusable.
- Developer build: the features may work locally but the release is huge, non-portable, and unsafe to distribute.

**Required remediation**

Choose and document one supported strategy:

- install/use a detected system Python and provision a per-user environment;
- ship a deliberately assembled embedded Python runtime;
- replace the Python subprocess with maintained .NET code; or
- make the integration an optional separately installed component.

Whichever option is chosen must include dependency locking, platform checks, clear setup errors, and an unambiguous support policy.

### P1-3. The application silently falls back to the maintainer's collections and paths

**Evidence**

`MainWindow.xaml.cs` falls back to the tracked scraper `config.yaml` if the UI collection list is empty or every entry is disabled. The settings page extracts the download root from YAML rather than offering a complete, validated first-run configuration flow.

**Impact**

An empty configuration—a normal state for a new user—does not mean “do nothing.” It can mean “use the author's defaults.” That is unsafe and surprising.

**Required remediation**

- Treat zero enabled collections as a valid no-op.
- Block synchronization until the user explicitly supplies and confirms a destination and collection list.
- Display the effective configuration before the first authenticated/network action.

### P1-4. Updater identity, version detection, and package validation are unsafe

**Evidence**

- `Diffusion.Toolkit/UpdateChecker.cs` checks releases from `RupertAvery/DiffusionToolkit`, not this customized fork.
- `SemanticVersionHelper.cs` calculates an application-relative `versionPath` but then reads `version.txt` from the current working directory.
- `Diffusion.Updater/Form1.cs` selects `assets[0]` rather than selecting an expected artifact by name/platform/type.
- The updater downloads and extracts the selected asset over the installation without signature or checksum verification.
- `README.md` also points users to upstream releases.

**Impact**

Users of this fork could be told to install an incompatible upstream build and overwrite the customized application. A changed working directory can produce an incorrect local version. Selecting the first arbitrary release asset is brittle, and unauthenticated integrity at the application level leaves the updater dependent on repository/account/CDN security alone.

**Required remediation**

- Give the fork a distinct application identity, repository, release feed, package name, and update channel.
- Resolve `version.txt` from `AppInfo.AppDir`.
- Select an exact expected asset name and verify platform/architecture.
- Publish and verify a cryptographic checksum at minimum; preferably sign both application and updater packages.
- Download to a temporary location, validate completely, then replace files atomically with rollback.
- Never overwrite a running installation until the package and manifest are validated.

### P1-5. Nonzero scraper failures can be reported as success

**Evidence**

`LaunchCivitaiScraper()` in `MainWindow.xaml.cs` specially treats exit code `2` as failure, but other nonzero exit codes continue into database validation, rescanning, album assignment, and eventually a success toast. The workflow then waits a fixed ten seconds after initiating a scan rather than awaiting an explicit scan-complete condition.

**Impact**

A Python crash, dependency error, authentication error with another exit code, or partial download can be presented as a successful sync. Subsequent album assignment operates on uncertain state.

**Required remediation**

- Treat every nonzero exit code as failure unless a documented exit-code contract says otherwise.
- Capture structured stdout/stderr and show a useful error.
- Replace fixed delays with an awaited scan/import completion signal.
- Make the workflow transactional or resumable so partial state is clear.

### P1-6. Album assignment can hide failed path-to-image mappings

**Evidence**

`Diffusion.Database/DataStore.Image.cs` returns one ID per input path and inserts `0` for paths that are not found. `MainWindow.xaml.cs` checks only whether the returned list is empty, which cannot identify partial or total lookup failure when input paths were supplied. `DataStore.Album.cs` later ignores IDs that do not map to images and can still report success.

**Impact**

The UI can say the album assignment succeeded even when some or all newly downloaded files were not assigned.

**Required remediation**

- Return a typed result containing matched paths, unmatched paths, and actual inserted count.
- Reject ID `0` before database insertion.
- Report partial success explicitly.
- Add an integration test covering path normalization, case differences, missing images, duplicate paths, and zero matches.

### P1-7. User-visible controls reach code that deliberately throws

**Evidence**

- The advertised move-files flow in `MainWindow.xaml.cs` throws `NotImplementedException("Too Lazy to fix")` after the user selects a destination.
- Previous/next paging handlers in `Diffusion.Toolkit/Pages/Search.xaml.cs` are subscribed to UI events and throw `NotImplementedException`.

There are other `NotImplementedException` occurrences in one-way converter `ConvertBack` methods; those are conventional and are not classified as broken features here.

**Impact**

Normal UI interaction can crash the application. The wording in the exception is also unsuitable for end users or a public project.

**Required remediation**

Implement the flows with tests, or remove/disable the controls and stop advertising the features until complete.

### P1-8. An external personal pipeline is elevated to administrator and closes the app

**Evidence**

The CivitAI pipeline launcher in `MainWindow.xaml.cs`:

- Executes a configured external repository and its `.venv1`.
- Starts `run_pipeline.py`.
- Uses `Verb="runas"` to request administrator privileges.
- Shuts down Diffusion Toolkit immediately afterward.

**Impact**

The application asks users to elevate external Python code without showing why elevation is required. Closing the main application prevents normal error reporting and recovery. This is an unsafe default for a feature that is inherently environment-specific.

**Required remediation**

- Remove the elevation requirement unless a narrowly defined privileged operation is proven necessary.
- Keep the application open and capture process status/output.
- Treat the pipeline as an optional plugin or advanced user command with an explicit executable/repository trust prompt.
- Do not ship a personal batch path as a default.

### P1-9. ComfyUI automation can target or kill unrelated processes

**Evidence**

`Diffusion.Toolkit/Services/ComfyUIService.cs` includes behavior that:

- Treats generic `python` processes as possible ComfyUI instances.
- Kills all processes named `ComfyUI` when more than six are found.
- Forces a window to the foreground and sends a global `Ctrl+V` keystroke.
- Returns success in some timeout/server-not-ready paths.
- Can submit a workflow to `/api/prompt`, which may execute it.

There is also a second ComfyUI launch implementation in `MainWindow.xaml.cs`, creating divergent behavior depending on the entry point.

**Impact**

The process detection is too broad, and the global keyboard/process-management behavior can affect unrelated work. False success leaves users uncertain whether a workflow was actually transferred or run.

**Required remediation**

- Use a configured executable path, child-process ID, port, and health endpoint rather than process-name heuristics.
- Never kill unrelated processes without listing them and receiving confirmation.
- Remove global keystroke injection; use a documented API or an explicit clipboard-only action.
- Consolidate the two implementations.
- Distinguish “process started,” “server ready,” “workflow imported,” and “workflow executed.”

### P1-10. Dependency and build health is not suitable for a stable release

**Evidence**

A clean tracked-source rebuild of the main application succeeded under .NET SDK `10.0.101`, but emitted **2,570 warnings**. Major categories included nullable dereference/initialization warnings and unawaited asynchronous calls.

NuGet also reported:

- `NU1902`: `SixLabors.ImageSharp 3.1.7` has a known moderate-severity vulnerability (`GHSA-rxmq-m78w-7wmc`).
- `NU1701`: `FontAwesome.WPF 4.7.0.9` was restored using .NET Framework targets rather than the project's `net10.0-windows` target.
- `NU1510`: a redundant `System.Security.Cryptography.ProtectedData` package reference.

**Impact**

Thousands of warnings make new regressions effectively invisible. Nullable and unawaited-call warnings are directly relevant to runtime crashes and lost errors. A known vulnerable image parser is particularly important in an application that processes untrusted downloaded images.

**Required remediation**

- Upgrade ImageSharp to a version that addresses the advisory and test metadata/image behavior.
- Replace or validate the legacy FontAwesome package.
- Triage warnings by category and establish a decreasing warning budget.
- Make high-value warnings errors in CI after the existing debt is reduced.
- Pin the supported SDK with `global.json`.

### P1-11. There is no automated regression suite for the riskiest workflows

**Evidence**

The solution contains `TestBed` and `TestHarness`, but no meaningful unit/integration test suite was found for:

- database migrations and path lookup;
- file move/copy safety;
- metadata parsing;
- CivitAI configuration/authentication/download state;
- album assignment;
- updater package selection and extraction;
- ComfyUI process/API behavior.

The Python requirements do not establish a runnable test environment.

**Impact**

The project cannot confidently distinguish a safe cleanup from a regression. This is especially problematic where code touches user image archives, authenticated accounts, SQLite state, external processes, and self-update files.

**Required remediation**

Create a small but meaningful test pyramid before broad release:

1. Unit tests for path normalization, version parsing, config loading, and metadata conversions.
2. Temporary-database tests for schema, queries, albums, and deduplication.
3. Fixture-based Python tests using mocked HTTP responses.
4. Packaging tests that fail if private/runtime files enter an artifact.
5. A smoke test from a clean install and empty user profile.

### P1-12. CI and published prerequisites do not describe the project that now exists

**Evidence**

- All current projects target `.NET 10`, while the GitHub workflow installs .NET 6.
- `README.md` still tells users to install/use .NET 6-era prerequisites.
- The workflow uses older action versions and runs `publish.cmd`.
- `publish.cmd` is interactive (`pause`), does not robustly clean/validate the artifact, and is unsuitable as a deterministic CI release pipeline.
- There is no SDK pin, dependency lock, artifact manifest, checksum generation, signing step, secret scan, or release smoke test.

**Impact**

Contributors cannot reliably reproduce the maintainer's build, and users receive incorrect installation guidance. A “green” or manually produced release does not demonstrate that it is clean or portable.

**Required remediation**

- Rewrite CI for the actual target framework and supported Windows architecture.
- Replace `publish.cmd` with a noninteractive, fail-fast release script.
- Build from a clean checkout.
- Test, scan, inventory, checksum/sign, and inspect the exact artifact that will be uploaded.
- Update all prerequisite and install documentation in the same release.

---

## P2 — Serious quality, privacy, and maintainability findings

### P2-1. The CivitAI upload window is a prototype presented as a feature

**Evidence**

`CivitaiUploadWindow.xaml.cs`:

- Detects the create page using a loose URL substring check.
- Waits a fixed delay for page readiness.
- Loads an entire image as Base64 and injects it into JavaScript.
- Interpolates a raw filename into a JavaScript string without safe serialization.
- Allows only one attempted injection per window instance.
- Depends on brittle DOM selectors and page structure.

`CivitaiPostService.PostImages` accepts multiple images but uploads only the first one. The window uses hardcoded styling and English text.

**Impact**

Large images produce excessive memory use, quotes/backslashes in filenames can break the injected script, page timing changes can make the operation fail, and the API suggests multi-image behavior that does not exist.

**Required remediation**

Mark this feature experimental or remove it from release builds until it has safe JavaScript serialization, multiple-image semantics, explicit state/progress, WebView2 prerequisite handling, and automated browser-level tests.

### P2-2. A personal external database is hardcoded and silently initialized

**Evidence**

`MainWindow.xaml.cs` unconditionally references:

`C:\Users\trunk\AppData\Roaming\DiffusionToolkit\civitai-extension-trimmed.db`

The associated `CivitAiExtensionDataStore` exposes workflow-specific fields such as `lora_riforgiati`, and catches/suppresses database errors. Related UI such as “Lora Riforgiati” is visible to users who do not have that database.

**Impact**

This is a personal integration embedded in the core application rather than a configured optional component. On other machines it silently becomes empty or nonfunctional, making diagnosis difficult.

**Required remediation**

Move it behind an optional feature/plugin boundary with a user-selected database path and schema/version validation, or remove it from the distributable branch.

### P2-3. Logs are privacy-sensitive, unbounded, and tied to the working directory

**Evidence**

`Logger.cs` appends to `DiffusionToolkit.log` using a relative path, with no rotation, size cap, retention, or redaction. Call sites record absolute paths, album/collection names, user names, image identifiers, and workflow details. Some HTTP failure handling records response content.

**Impact**

The log can grow indefinitely and reveal a user's archive layout and online activity. It may fail when the process starts in an unwritable directory, and users are not told what is recorded.

**Required remediation**

- Store logs in a per-user application-data directory.
- Add rotation and retention.
- Redact credentials, cookie values, query tokens, and sensitive response bodies.
- Provide a “diagnostic logging” switch and privacy documentation.
- Make support bundles reviewable before upload.

### P2-4. Resource resolution depends inconsistently on the process working directory

**Evidence**

- `MainWindow.xaml.cs` reads `samplers.txt` by relative path.
- `Diffusion.Database/DataStore.cs` loads `extensions\path0.dll` by relative path.
- `SemanticVersionHelper.cs` reads `version.txt` by relative path despite calculating an application path.
- `Logger.cs` writes a relative log.
- A `models.json` existence check and read use different path bases.

The existing log confirms a real failure to find `samplers.txt` when launched from the repository directory.

**Impact**

Features work or fail depending on the shortcut, shell, updater, or debugger used to launch the application.

**Required remediation**

Centralize immutable resource paths under the installation directory and mutable data under `%APPDATA%`/`%LOCALAPPDATA%`. Add a startup test that changes the current directory before exercising resource loading.

### P2-5. Error handling frequently converts defects into silent missing data

**Evidence**

Multiple configuration, scanner, metadata, and external-database paths catch exceptions and discard them or return an empty/default result. `SettingsContainer.UpdateList` can dereference a null list while trying to update it. `App.xaml.cs` does not establish consistent top-level exception reporting. The codebase also contains many `async void` methods; many are legitimate event handlers, but there is no uniform policy for catching/reporting failures after `await`.

**Impact**

Users see missing metadata, empty filters, or false success instead of actionable errors. Maintainers receive logs that may not contain the root cause.

**Required remediation**

- Catch only expected exceptions.
- Log structured context without sensitive data.
- Distinguish “not present” from “failed to read.”
- Add a global WPF dispatcher/unobserved-task exception policy.
- Prefer `Task`-returning command methods beneath event handlers.

### P2-6. Main-window and service classes have grown beyond maintainable boundaries

**Evidence**

- `MainWindow.xaml.cs` is approximately 109 KB and orchestrates UI, configuration, Python processes, database work, scanning, external pipelines, upload, album assignment, and ComfyUI.
- `ComfyUIService.cs` is approximately 53 KB.
- Service-locator/global patterns make dependencies and test boundaries unclear.
- Similar ComfyUI behavior exists in multiple implementations.

**Impact**

Changes to one personal workflow can regress unrelated core image-management behavior. Testing requires constructing or driving the UI, and ownership of cancellation, errors, and lifetime is unclear.

**Required remediation**

Extract use-case services with explicit interfaces for process launch, filesystem, HTTP, database, configuration, and dialogs. Keep WPF code-behind limited to presentation/event adaptation.

### P2-7. Scanner support is incomplete and can silently omit metadata

**Evidence**

The ComfyUI parser skips some object/array inputs rather than preserving them. Several scanner paths use broad exception handling. The project has accumulated multiple tool-specific parsing branches without a fixture-based compatibility suite.

**Impact**

Images can be indexed with incomplete prompts or parameters, and users may not know that metadata was discarded.

**Required remediation**

- Preserve unknown structured metadata as raw JSON.
- Add representative fixtures for every supported generator/version.
- Record parse warnings on the image rather than silently dropping fields.
- Fuzz/test malformed metadata and unusually large inputs.

### P2-8. SQLite diagnostics and data access need a production policy

**Evidence**

SQL tracing is enabled on connections in `Diffusion.Database/DataStore.cs`, including release operation. The database layer relies heavily on global locking and many individually issued queries. Some batch operations report a Boolean rather than affected/missing counts.

**Impact**

Verbose SQL can leak paths/metadata into diagnostics and add overhead. Boolean results hide partial failure. The global lock can become a performance bottleneck on large libraries.

**Required remediation**

- Disable SQL tracing by default in Release.
- Add opt-in structured database diagnostics.
- Return affected counts and failure details.
- Benchmark common operations on a realistically large, temporary database before optimizing query/locking behavior.

### P2-9. Remote/account automation lacks a clear safety and support boundary

**Evidence**

The Python tree contains development probes capable of account mutations, including creating and deleting posts. Because of the broad project glob, these scripts are copied into application output. Other code relies on internal tRPC routes and DOM selectors rather than a stable public contract.

**Impact**

Dangerous diagnostics are distributed beside normal runtime code, and small remote-site changes can break authenticated workflows. Users cannot easily tell which operations are read-only and which mutate their account.

**Required remediation**

- Exclude probes and developer utilities from releases.
- Require explicit confirmation for upload/create/delete/schedule actions.
- Document the supported API contract and expected breakage risk.
- Add mocked contract tests and safe dry-run modes.

### P2-10. Python dependencies and runtime inputs are not reproducible

**Evidence**

Python dependencies are expressed with broad `>=` constraints and no resolved lock file or hashes. The local solution relies on a copied virtual environment. NuGet dependencies likewise have no package lock, and no `global.json` pins the .NET SDK.

**Impact**

Two builds can install materially different dependencies. A later transitive release can break the application or introduce a vulnerability without a source change.

**Required remediation**

- Generate a reviewed Python lock file with hashes.
- Build the Python runtime/environment during release, never copy a developer environment.
- Pin the .NET SDK and consider NuGet locked restore for release builds.
- Add automated dependency update and vulnerability review.

### P2-11. Third-party binary and asset provenance is not documented

**Evidence**

The repository tracks native/managed DLLs and extension binaries, including SQLite, metadata/editor/rendering components, and plugin binaries. Image/icon filenames suggest third-party icon sources. No consolidated third-party notice, binary origin manifest, or license inventory was found.

**Impact**

This audit does not conclude that any license is violated. It concludes that a prospective distributor cannot currently verify redistribution rights and notice obligations from the repository.

**Required remediation**

- Inventory every tracked and packaged binary/asset, its source version, license, and redistribution terms.
- Prefer package-manager restoration over committed binaries where practical.
- Add `LICENSE`, `THIRD_PARTY_NOTICES`, and source/provenance links.
- Remove unused binaries and assets.

### P2-12. The README and internal documentation no longer match the fork

**Evidence**

The primary README still describes the upstream application, points to upstream releases, and gives outdated framework prerequisites. Numerous implementation summaries, root-cause notes, generated plans, and debugging documents contain stale designs, personal paths, collection identifiers, log fragments, or confident claims that no longer match current code.

The scraper README includes promotional language such as “Secret Sauce,” “without paying membership,” “99%+ reliability,” and “literally no downside.”

**Impact**

Users cannot tell what this fork adds, which features are experimental, what data leaves the machine, or what is actually supported. Some phrasing creates unnecessary trust, policy, and reputation risk.

**Required remediation**

Write documentation for the current fork:

- purpose and differences from upstream;
- supported/experimental/personal-only feature matrix;
- installation and first-run configuration;
- network endpoints and credential handling;
- file/database/log locations;
- backup and recovery advice;
- known limitations;
- project policy regarding third-party service terms.

Move obsolete engineering notes to an archive or issue tracker, and sanitize retained examples.

### P2-13. Localization is incomplete and new UI is largely hardcoded

**Evidence**

Compared with the default resource set:

- `en-US` and `uk-UA` are missing approximately 57 keys.
- German, Spanish, and French are missing approximately 237 keys.
- Japanese is missing approximately 238 keys.

New CivitAI, ComfyUI, upload, and personal workflow UI contains hardcoded English. Some source/configuration text contains mojibake such as `JosÃ¨`.

**Impact**

Existing translated UI becomes mixed-language, and corrupted names can affect both display and path matching.

**Required remediation**

- Move all user-facing text into resources.
- Define a fallback/frozen translation policy.
- Normalize source files to UTF-8 and repair corrupted values deliberately.
- Test non-ASCII filenames, account names, collections, and paths.

### P2-14. Download destinations and stored browser state are not clearly disclosed

**Evidence**

The CivitAI posts service can select the first configured root folder when downloading missing images. WebView2 uses a persistent per-user profile below the Diffusion Toolkit application-data area, which can retain authenticated browser state. These behaviors are not presented as part of a privacy/storage model.

**Impact**

Users may not know where remote files or session data are stored, backed up, or removed.

**Required remediation**

- Ask for and display the destination before the first download.
- Document all user-data locations.
- Provide “clear browser/session data” and “open data folder” actions.
- Keep authentication state separate from distributable files and logs.

---

## P3 — Project hygiene and presentation debt

### P3-1. Repository-local editor/agent configuration is mixed into the product

Tracked `.claude`, agent-instruction, and VS Code configuration includes maintainer-specific workflow and permission context. `.claude/settings.local.json` is tracked even though its name and contents are machine-local in nature.

**Recommendation:** Keep public contributor guidance generic; ignore personal/local settings and publish sanitized examples only.

### P3-2. Placeholder and manual-harness projects add noise

The solution contains placeholder/experimental projects such as `Diffusion.Data`, `Diffusion.Scripting`, `TestBed`, and `TestHarness` without a clear supported role or automated test value.

**Recommendation:** Remove them from the release solution, document them as development tools, or convert them into real tests.

### P3-3. No contributor, security, privacy, or support process is defined

No clear `CONTRIBUTING`, security-reporting policy, privacy statement, feature-status document, or fork-specific changelog was found.

**Recommendation:** Add these before inviting outside users/contributors, especially because the application handles local archives, online sessions, downloads, and self-updates.

### P3-4. Release artifacts lack a manifest

There is no human/machine-readable list of expected files in a release.

**Recommendation:** Generate a manifest with paths, sizes, and hashes. Compare it against an allowlist and fail the release when cookies, databases, logs, profiles, bytecode caches, or virtual environments appear.

---

## Confirmed personal or sensitive-data inventory

This table distinguishes tracked source/privacy issues from local artifact leakage.

| Item | Tracked in current Git tree? | Present in local build output? | Risk |
|---|---:|---:|---|
| CivitAI cookie exports | No | **Yes** | Authenticated session disclosure |
| Old CivitAI cookie export | No | **Yes** | Potential still-valid session disclosure |
| CivitAI state database | No | **Yes** | Account/activity/path metadata |
| CivitAI downloader log | No | **Yes** | Paths, IDs, operations, errors |
| Developer `.venv` | No | **Yes** | Huge, non-portable, machine paths, unnecessary code |
| Personal download/check paths | **Yes** | Yes, via copied config | Privacy and unsafe defaults |
| Personal collection names/IDs | **Yes** | Yes, via copied config | Privacy and unintended account actions |
| Hardcoded personal external DB path | **Yes** | Yes | Broken/personal-only feature |
| Maintainer paths in docs/scripts/settings | **Yes** | Often | Privacy and contributor confusion |

The audit intentionally did **not** print or inspect cookie values.

---

## Positive findings and important limitations

### Positive findings

- A clean snapshot containing only tracked files built the main WPF project successfully.
- The current tracked tree did not reveal an obvious plaintext API key, password, private key, or cookie file in the targeted checks.
- A name-based Git-history check did not identify committed cookie/database/key files.
- The application has a DPAPI-based protected-string mechanism for sensitive C# settings.
- The C# launcher passes the API key to Python through an environment variable rather than placing it on the command line.
- All tracked Python files passed an AST syntax parse.
- Git already ignores several scraper runtime files; the main failure is that build packaging does not honor those ignores.

### Audit limitations

- This was a source, configuration, history-name, build, and artifact review—not a formal penetration test.
- No authenticated CivitAI operation was performed.
- The ownership or trust relationship of `civitai.red` was not established.
- Git-history checks were heuristic, not a replacement for a dedicated entropy/secret scanner over every revision.
- Third-party licenses and binary provenance were not individually adjudicated.
- The full solution build in the active Dropbox workspace encountered access-denied/locked `obj` artifacts; the clean tracked-source main-project rebuild was used to separate environment locking from source buildability.
- Runtime workflows involving the real image archive, browser session, ComfyUI, updater installation, and external pipeline were not executed because doing so could mutate user data or accounts.

---

## Recommended remediation order

### Repository and branching strategy

The recommended approach is to **create a hardening branch now and create a separate public repository only after the project has been sanitized and verified**.

Creating a new repository immediately would copy most of the existing problems into a new location. A dedicated branch preserves the current working version while allowing cleanup to proceed in small, reviewable commits.

For stronger isolation, create the branch in a separate Git worktree:

```powershell
git worktree add ..\Diffusion-Toolkit-public-hardening -b hardening/public-release
```

This allows the existing checkout to remain the personal development environment while the new worktree contains only public-release work. Local changes such as `.claude/settings.local.json` must not be committed to the hardening branch.

The first hardening commit should have one narrow objective:

> Make it technically impossible for a build to contain developer credentials or runtime data.

That first commit should:

1. Replace the recursive `Diffusion.PyScripts\**\*` packaging rule with an explicit allowlist.
2. Exclude cookies, databases, logs, browser profiles, `.venv`, `__pycache__`, bytecode, and user configuration.
3. Add an automated artifact inspection that fails if a forbidden file enters a build.
4. Disable cross-domain CivitAI cookie copying.
5. Replace the personal `config.yaml` with a harmless `config.example.yaml`.
6. Verify the resulting artifact from a clean checkout rather than the developer's existing output directories.

Refactoring, UI polish, and broad feature work should wait until this containment step is complete. The first milestone is not "the program is polished"; it is "building the program cannot leak private state."

After the hardening branch:

- builds from a clean checkout;
- contains no personal paths, collection identifiers, or enabled personal defaults;
- produces a clean and reproducible artifact;
- no longer depends on the maintainer's local Python environment;
- has its own application identity, documentation, and update feed;

create a separate public repository from a reviewed, sanitized snapshot. A squashed initial product commit is reasonable if preserving the fork's entire personal-development history would disclose unwanted paths or workflow details. The new repository must still retain the original project's license, copyright notices, attribution, and an explanation that it was derived from the upstream project.

The suggested long-term arrangement is:

| Location | Purpose |
|---|---|
| Existing repository | Private personal-development archive and reference |
| `hardening/public-release` branch/worktree | Cleanup, stabilization, and release preparation |
| Future public repository | Sanitized supported product, public issues, releases, and updater feed |

If the existing remote is already public, moving to a new repository does not remove information from the old repository or its history. In that case, the old remote and any published artifacts must be reviewed separately, and exposed sessions should be invalidated where appropriate.

### Phase 0 — Contain exposure immediately

1. Do not distribute any existing artifact.
2. Delete/quarantine old shareable archives after checking whether they contain cookies, databases, logs, profiles, or `.venv`.
3. Invalidate CivitAI sessions if an affected artifact was shared.
4. Fix the MSBuild copy rule using an explicit allowlist.
5. Remove cross-domain cookie mirroring and disable authenticated `civitai.red` operations pending review.
6. Add artifact denylist inspection and secret scanning.

**Exit criterion:** A clean-CI artifact contains only expected application/runtime files and no user/developer state.

### Phase 1 — Separate product configuration from the maintainer's machine

1. Introduce `config.example.yaml`.
2. Create private per-user config under AppData.
3. Add a real first-run setup flow for destinations, collections, Python/runtime, ComfyUI, and optional integrations.
4. Remove personal defaults, paths, collection IDs, profile names, and examples.
5. Make personal database/pipeline integrations optional plugins or remove them.
6. Sanitize public docs and decide whether to rewrite history.

**Exit criterion:** A new Windows user can clone/install without editing source and cannot accidentally operate on the maintainer's paths or collections.

### Phase 2 — Establish a safe distribution and update model

1. Give the fork its own identity and update feed.
2. Pin SDK/dependencies and build noninteractively from a clean checkout.
3. Package Python deliberately.
4. Validate, checksum/sign, and inventory releases.
5. Fix application-relative resource loading.
6. Remove unnecessary elevation and broad process/keyboard automation.

**Exit criterion:** Installation and update are reproducible, portable, integrity-checked, and do not execute arbitrary personal-environment code.

### Phase 3 — Fix correctness and add regression coverage

Prioritize tests and fixes for:

1. scraper exit handling and cancellation;
2. scan-completion synchronization;
3. album path matching and inserted counts;
4. move/copy and search paging crashes;
5. database migration/backup behavior;
6. metadata fixtures and malformed input;
7. updater asset/version handling;
8. ComfyUI process/API states;
9. upload filename serialization and multi-image behavior.

**Exit criterion:** Core workflows have automated tests and no reachable intentional exceptions.

### Phase 4 — Make it supportable

1. Reduce compiler warnings to an enforced budget.
2. Update vulnerable/legacy dependencies.
3. Add third-party notices and provenance.
4. Rewrite README/install/privacy/security/support documentation.
5. Label experimental features.
6. Complete localization or clearly state its status.
7. Add structured, rotating, privacy-conscious diagnostics.

**Exit criterion:** A user can understand what the program does, what it sends/stores, what is experimental, and how to recover from failure.

---

## Minimum public-release checklist

Do not call the fork shareable until every critical item and the applicable release blockers below is checked:

- [ ] Existing artifacts have been checked for cookies, logs, databases, browser profiles, and virtual environments.
- [ ] Any exposed sessions have been invalidated.
- [ ] MSBuild uses a Python-file allowlist, and an automated packaging test enforces it.
- [ ] No CivitAI cookie is copied or sent to an unrelated domain.
- [ ] The tracked default config contains no personal paths, IDs, names, or enabled collections.
- [ ] Empty configuration performs no network/download action.
- [ ] Python runtime installation/packaging works from a clean machine.
- [ ] The fork has its own update feed and cannot overwrite itself with upstream.
- [ ] Update artifacts are selected exactly and integrity-checked.
- [ ] All nonzero child-process exits are failures unless explicitly documented.
- [ ] Reachable `NotImplementedException` features are implemented or removed.
- [ ] External pipelines do not request administrator privileges without a proven, disclosed need.
- [ ] ComfyUI automation cannot kill or type into unrelated applications.
- [ ] ImageSharp and other dependency warnings have been resolved or formally accepted.
- [ ] CI uses the real target SDK and builds/tests a clean checkout.
- [ ] Core database, file, scraper, album, and updater workflows have automated tests.
- [ ] Logs are stored per-user, rotated, and scrubbed of credentials/private response data.
- [ ] Resource paths do not depend on the current working directory.
- [ ] Personal integrations are configurable optional components.
- [ ] README, prerequisites, privacy, security, and feature-status documentation describe this fork.
- [ ] Third-party binaries/assets have documented origins and redistribution terms.
- [ ] A dedicated secret scanner has checked the current tree and full Git history.
- [ ] The exact final archive has passed a clean-machine smoke test.

## Final assessment

The fork contains genuinely useful workflow ideas, but it currently behaves like a personal development environment captured inside an application repository. The largest obstacle is not that every feature must be polished before anyone can see the source. The obstacle is that personal state, unsafe defaults, credential routing, environment assumptions, and prototype behavior are mixed directly into the distributable product.

The safest path is to create a hard boundary between:

1. a clean, portable core application;
2. user-owned configuration and credentials;
3. optional experimental integrations; and
4. maintainer-only tools and notes.

Once that separation exists, the remaining warning, testing, documentation, and polish work becomes manageable and can be improved incrementally in public.
