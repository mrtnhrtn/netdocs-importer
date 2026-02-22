# OWASP Review Plan and Handover Pad

## Purpose
- Turn the architecture/security review findings into an execution plan with safe, incremental changes.
- Preserve current direct upload stability while hardening queue + scheduling behavior for secure enterprise environments.

## Scope and Guardrails
- Do not rewrite core upload logic.
- Prioritize targeted fixes with tests.
- Keep `Run Direct Upload` behavior intact.
- Enforce queue invariant: only one queue job may be `Running` at any time, including restart and multi-instance scenarios.

## Priority Summary
- `P0` (must do now): queue single-runner integrity across processes.
- `P1` (do in MVP hardening pass): context binding, monitor observability/recovery, request timeout hardening.
- `P2` (follow-up hardening): UI thread-safety cleanup, log redaction expansion, atomic file writes, duplicate enqueue controls.

---

## Work Plan (Logical, Appropriately Sized)

### WP1 - Queue Single-Runner Integrity (`P0`, MVP now)
**Status**
- [x] Completed (implemented + tests)

**Goal**
- Guarantee at-most-one `Running` queue row across app instances.

**Changes**
- Add DB-level invariant for `UploadQueueJobs`:
  - Partial unique index for rows where `State = 'Running'`.
- Make acquire path robust:
  - Use write-locking transaction mode (`BEGIN IMMEDIATE`) for `TryAcquireNextQueuedJobAsync`.
  - Keep check-and-transition atomic and deterministic.
- Add explicit failure handling when transition fails due to invariant conflict.

**Files**
- `src/NetDocsImporter.Data/JobStore.cs`
- `tests/NetDocsImporter.Tests/JobQueueStoreTests.cs`

**Acceptance Criteria**
- Two concurrent acquire attempts cannot produce two running jobs.
- Existing Option B ordering remains unchanged.
- Restart behavior still fails stale running jobs and proceeds.

**Effort**
- `M`

---

### WP2 - Snapshot Context Binding Enforcement (`P1`, MVP now)
**Status**
- [x] Completed (implemented + tests)

**Goal**
- Prevent queued jobs from running under wrong tenant/region/profile context.

**Changes**
- Before queued execution, validate:
  - snapshot `RepositoryId` matches current selected repository.
  - snapshot `CabinetId` matches current selected cabinet.
  - snapshot plan context/API base URL alignment (or equivalent stable region marker).
- Fail closed with actionable error if mismatch.
- Keep queue state transitions valid (`Running -> Failed` with clear reason).

**Files**
- `src/NetDocsImporter.App/MainViewModel.JobQueue.cs`
- `src/NetDocsImporter.Core/UploadQueueSnapshot.cs` (only if extra fields are needed)
- Tests in `tests/NetDocsImporter.Tests` (new test file if needed)

**Acceptance Criteria**
- Job does not upload if context differs from snapshot.
- Failure reason is visible in queue record/log.
- No regression to normal same-context queue execution.

**Effort**
- `S`

---

### WP3 - Queue Monitor Observability + Safe Recovery (`P1`, MVP now)
**Status**
- [x] Completed (implemented + tests)

**Goal**
- Eliminate silent stalls and improve operational diagnosability.

**Changes**
- Replace silent catch in monitor loop with structured trace logging.
- Include queue job id and transition stage in error logs where available.
- Add defensive recovery path for abnormal loop failures (without destabilizing runner flow).

**Files**
- `src/NetDocsImporter.Core/UploadJobMonitor.cs`
- Optional UI status surface in `src/NetDocsImporter.App/MainViewModel.JobQueue.cs`
- Tests: `tests/NetDocsImporter.Tests/UploadJobMonitorTests.cs`

**Acceptance Criteria**
- Monitor exceptions are visible in logs.
- Queue does not silently stop progressing after transient errors.

**Effort**
- `S`

---

### WP4 - Request Timeout Hardening for V1 Upload Path (`P1`, MVP now)
**Status**
- [x] Completed (implemented + tests)

**Goal**
- Prevent indefinite hanging uploads in proxy/intermittent networks.

**Changes**
- Set bounded default request timeout for v1 upload requests (aligned with enterprise conditions).
- Ensure timeout failures are treated as transient where appropriate, with existing capped retries.
- Keep multipart behavior unchanged unless explicitly adjusted.

**Files**
- `src/NetDocsImporter.NetDocs/NetDocumentsDirectUploadService.cs`
- `src/NetDocsImporter.NetDocs/NetDocumentsApiClient.cs` (if shared helper change is needed)
- Upload tests as appropriate.

**Acceptance Criteria**
- V1 upload cannot hang indefinitely on network stalls.
- Retries remain bounded and cancellation still works.

**Effort**
- `S`

---

### WP5 - Thread-Safe UI Updates in Queued Runner (`P2`, soon)
**Status**
- [ ] Pending

**Goal**
- Avoid cross-thread UI update risks in queued execution path.

**Changes**
- Marshal queued upload UI-bound state changes through `UpdateOnUi`.
- Keep background work off UI thread.

**Files**
- `src/NetDocsImporter.App/MainViewModel.JobQueue.cs`

**Acceptance Criteria**
- No cross-thread binding exceptions under sustained queue activity.

**Effort**
- `M`

---

### WP6 - Secure Logging and Persistence Hardening (`P2`, follow-up)
**Status**
- [ ] Pending

**Goal**
- Reduce sensitive data leakage and improve corruption tolerance.

**Changes**
- Expand redaction strategy beyond bearer tokens:
  - path segments, query values, IDs where practical.
- Add atomic write pattern (`temp + replace`) for:
  - settings, token cache, completed-job summaries/markers.
- Add tolerant read/reporting for malformed persisted files.

**Files**
- `src/NetDocsImporter.NetDocs/NetDocumentsApiClient.cs`
- `src/NetDocsImporter.NetDocs/NetDocumentsDirectUploadService.cs`
- `src/NetDocsImporter.Core/AppSettings.cs`
- `src/NetDocsImporter.NetDocs/NetDocumentsTokenStore.cs`
- `src/NetDocsImporter.Core/CompletedJobLogStore.cs`

**Acceptance Criteria**
- Logs avoid sensitive payload/path leakage in expected failure paths.
- Crash/power interruption during write does not leave unrecoverable state.

**Effort**
- `M`

---

### WP7 - Duplicate Enqueue Guard (Optional `P2`, follow-up)
**Status**
- [ ] Pending

**Goal**
- Prevent accidental duplicate queue entries from repeated UI actions.

**Changes**
- Add optional dedupe strategy at enqueue:
  - short-window duplicate suppression keyed by source job + target + snapshot hash.
- Keep user-intended repeated queueing possible when explicitly desired.

**Files**
- `src/NetDocsImporter.Data/JobStore.cs`
- `src/NetDocsImporter.App/MainViewModel.JobQueue.cs`
- tests in `tests/NetDocsImporter.Tests`

**Acceptance Criteria**
- Rapid double-trigger does not create unintended duplicate jobs.

**Effort**
- `S`

---

## Execution Order
1. `WP1` (blocker invariant)
2. `WP2` + `WP3` + `WP4` (MVP security hardening)
3. `WP5` + `WP6` + `WP7` (follow-up hardening)

## Validation Gate Per Work Package
- Unit tests pass:
  - `dotnet test tests/NetDocsImporter.Tests/NetDocsImporter.Tests.csproj`
- App builds:
  - `dotnet build src/NetDocsImporter.App/NetDocsImporter.App.csproj`
- Manual queue smoke:
  - enqueue immediate + scheduled
  - restart during running
  - verify only one running job
  - verify queue progress and UI status visibility

---

## Handover Pad (Next Agent)

### Current Status
- MVP hardening delivered for `WP1` + `WP2` + `WP3` + `WP4`.
- `dotnet test tests/NetDocsImporter.Tests/NetDocsImporter.Tests.csproj` passes (`146/146`).
- `dotnet build src/NetDocsImporter.App/NetDocsImporter.App.csproj` passes.
- Manual queue smoke scenarios are still required (not executed in this implementation step).

### Immediate Next Task
- Execute `WP5` (UI thread marshaling in queued runner) and then `WP6`/`WP7` follow-up hardening.

### Completed Implementation Notes
1. `WP1` queue single-runner integrity
- Added partial unique index: `IX_UploadQueueJobs_SingleRunning` (`WHERE State = 'Running'`).
- Added invariant repair on initialize for legacy/corrupt states with multiple `Running` rows (keeps oldest running, fails extras deterministically).
- Acquire path now uses `BEGIN IMMEDIATE` transaction semantics in `TryAcquireNextQueuedJobAsync`.
- Added explicit constraint-conflict handling with trace logging on acquire collision.
- Added/updated tests in `tests/NetDocsImporter.Tests/JobQueueStoreTests.cs`:
  - concurrent acquire attempts keep one running row
  - partial unique index presence assertion

2. `WP2` snapshot context binding enforcement
- Added validator `UploadQueueContextValidator` in core.
- Enforced validation in queued execution before running upload:
  - snapshot repository vs currently selected repository
  - snapshot cabinet vs currently selected cabinet
  - snapshot `PlanContext` repository/cabinet alignment with snapshot top-level fields
  - snapshot API base URL vs current API base URL (normalized)
- Fail-closed behavior returns actionable mismatch reason; queue transitions to `Failed` through monitor path.
- Added tests in `tests/NetDocsImporter.Tests/UploadQueueContextValidatorTests.cs`.

3. `WP3` monitor observability and safe recovery
- Replaced silent loop catch with structured `Trace` diagnostics.
- Added stage + queue job id in error/completion/failure logs.
- Added defensive recovery behavior:
  - recovery promotion tick after loop exceptions
  - if an exception occurs after job acquisition, monitor attempts to fail the active running job to avoid silent stalls.
- Added tests in `tests/NetDocsImporter.Tests/UploadJobMonitorTests.cs` for recovery behavior on transient store/mark failures.

4. `WP4` v1 timeout hardening
- Added bounded default `DirectUploadPlanContext.V1UploadRequestTimeout` (30 minutes).
- V1 upload path now always applies request timeout (bounded, configurable per context).
- Timeout exceptions in non-user-cancel paths are mapped to transient status (HTTP 408 semantic) to participate in existing bounded retries.
- Added test in `tests/NetDocsImporter.Tests/NetDocumentsDirectUploadServiceTests.cs` validating timeout then retry success.

### Suggested Commit Slices
1. `queue: enforce single-running invariant at db level`
2. `queue: enforce snapshot context binding before queued execution`
3. `queue: add monitor exception visibility and recovery-safe behavior`
4. `upload: add bounded timeout for v1 upload requests`
5. `docs: mark wp1-wp4 complete and handover next-agent checklist`

### Risks to Watch
- SQLite migration compatibility on existing user DBs.
- No regression in Option B ordering.
- No regression in direct upload resume/cancel flow.
- `WP2` now fails queued jobs on context mismatch by design; confirm expected operator UX text in queue list/logs.
- Timeout default is currently 30m for v1 uploads; confirm enterprise preference if policy requires shorter default.

### Open Questions for Implementer
- Should context mismatch auto-retry after reconnect, or remain explicit fail-only (current behavior is fail-only)?
- Should default v1 timeout remain 30m, or be tightened (for example, 10m) after field validation?
- For `WP5`, should queue-run UI status writes be fully marshaled (`UpdateOnUi`) even in early-fail paths for strict consistency?

### Done Definition for MVP Hardening
- `WP1` + `WP2` + `WP3` + `WP4` implemented and automated tests/build passing.
- Remaining for absolute closure: manual queue smoke validation:
  - enqueue immediate + scheduled
  - restart during running
  - verify only one running job
  - verify queue progress + status visibility + mismatch failure text
