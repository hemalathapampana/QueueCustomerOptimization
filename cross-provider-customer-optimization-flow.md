# Cross-Provider Customer Optimization Flow

> Use this document as the authoritative reference for feature work, debugging, and onboarding related to the cross-provider customer optimization lifecycle. It maps user-facing actions to API logic, messaging, Lambdas, and database artifacts.

---

## 1. End-to-End Narrative
1. Tenant user launches the portal and selects Customer, Billing Period, Service Providers, pool scope, and feature flags.
2. UI verifies concurrency via `OptimizationCustomerProcessing` and displays the Optimize CTA only when no in-flight rows (`IsProcessed = 0`).
3. API validates concurrency again, resolves billing period context, and creates an `OptimizationSession` plus `OptimizationInstance`. SIM cards, usage projections, and rate-plan metadata are staged.
4. Communication groups, queue scaffolding, and queue ↔ rate-plan mappings are generated.
5. Queue batches are enqueued to SQS via `EnqueueCrossProviderCustomerOptimizationSqsAsync`. `QueueCustomerOptimization1` Lambda (SimCardCostOptimizer.Function) executes each queue, reloads devices, computes rate pools, and records results.
6. Cleanup Lambda (`SimCardCostOptimizerCleanup.Function`) selects winning queues, collates files, updates cross-provider tracking, and schedules notification work.
7. Notification pipelines (AMOP proxy, SES emails) finalize the run, purge `OptimizationCustomerProcessing`, and the UI surfaces downloadable assets and completion badges.

---

## 2. Component Responsibility Matrix
| Layer | Responsibilities | Key Code / Procedures |
| --- | --- | --- |
| UI (Portal) | Input gathering, preflight status checks, progress polling, download surfaces, aggregated usage views | `CrossProviderCustomerDropdown`, `CrossProviderCustomerRatePoolUsage`, `_CrossProviderCustomerRatePoolLineDetails`, export actions |
| API / MVC | Validation, session creation, staging, queue orchestration, SQS enqueue, AMOP 2.0 payloads | `EnqueueCrossProviderCustomerOptimizationSqsAsync`, `SendResponseToAMOP20`, `SendAllCustomersCrossProviderOptimizationSummaryEmail` |
| Database | Persist optimization state, queues, devices, history, outputs | `OptimizationCustomerProcessing`, `OptimizationSession`, `OptimizationInstance`, `OptimizationDevice*`, `OptimizationQueue*`, `CrossProviderDeviceHistory`, `OptimizationInstanceResultFile`, etc. |
| Stored Procedures | Bulk data loading, SIM discovery, result collation, aggregated usage | `usp_Optimization_CreateCrossProviderOptimizationInstance`, `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards`, `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId`, `usp_Optimization_GetCrossProviderOptimizationDeviceResults`, `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults`, `usp_GetCrossProviderCustomerPoolAggregatedUsage` |
| Messaging | SQS queue for execution, cleanup queue | `QueueCustomerOptimization1` Lambda, `SimCardCostOptimizerCleanup.Function`, `QueueLastStepOptCustomerCleanup` |
| Notification | AMOP proxy, SES summary emails, UI badges | `OptCustomerSendEmail`, `SendAllCustomersCrossProviderOptimizationSummaryEmail`, AMOP 2.0 proxy |

---

## 3. Stage-by-Stage Flow (UI → API → Messaging → Lambda → DB)
| Stage | Trigger & UI Event | API / Service Action | Messaging & Lambda | Data Stores |
| --- | --- | --- | --- | --- |
| 1. UI Initiation | User selects customer + filters; Optimize CTA clicked | `CheckOptCustomerProcessing` ensures no `IsProcessed = 0` rows | — | `OptimizationCustomerProcessing` (read) |
| 2. Guard Rails | API repeats concurrency check; resolves billing period | `UpdateOptCustomerProcessing`, `GetBillingPeriod`, session + instance creation (`StartOptimizationSession`, `StartOptimizationInstanceWithBillingPeriod`) | — | `OptimizationSession`, `OptimizationInstance`, `CustomerBillingPeriod` |
| 3. Device Staging | — | SIM discovery (`GetCrossProviderCustomerSimCards`), usage projection (`ProjectDataUsageAndSaveDeviceByPortalType`), rate-plan validation (`GetCrossCustomerRatePlans`) | — | `OptimizationDevice`, `OptimizationMobilityDevice`, `CustomerRatePlan`, `CrossProviderDeviceHistory` |
| 4. Queue Setup | — | `CreateCommPlanGroup`, `CreateQueue`, `CreateQueueRatePlans` | — | `OptimizationCommGroup`, `OptimizationQueue`, `OptimizationQueue_RatePlan` |
| 5. Enqueue | UI polls progress | `EnqueueOptimizationRunsAsync`, `EnqueueCrossProviderCustomerOptimizationSqsAsync` | Messages sent to `QueueCustomerOptimization1` | `OptimizationCustomerProcessing` progress fields |
| 6. Queue Execution | SQS delivery | `ProcessQueues` inside Lambda validates metadata, reloads queue + instance | `QueueCustomerOptimization1` Lambda executes | `OptimizationQueue`, `OptimizationInstance`, `RedisCacheHelper` (optional) |
| 7. Rate-Pool Assignment | Lambda loop | Rate pool calculation, assignment, result recording (`RecordResultsIfBetter`) | Same Lambda run | `OptimizationDeviceResult`, `OptimizationMobilityDeviceResult`, queue status |
| 8. Cleanup | All queues finished | `SimCardCostOptimizerCleanup.Function` selects winners, collates files, schedules last-step cleanup | `QueueLastStepOptCustomerCleanup` SQS | `OptimizationSharedPoolResult*`, `OptimizationInstanceResultFile`, `OptimizationCustomerProcessing` |
| 9. Notifications | Cleanup completion | `OptCustomerSendEmail`, `SendResponseToAMOP20`, `SendAllCustomersCrossProviderOptimizationSummaryEmail` | SES + AMOP proxies | `OptimizationCustomerProcessing`, `OptimizationCustomerEndProcess` |
| 10. UI Completion | UI polling, exports | UI fetches status badges, result file metadata, aggregated usage | — | `OptimizationInstanceResultFile`, `CrossProviderCustomerPool*` views, `OptimizationCustomerProcessing` |

---

## 4. Detailed Stage Notes
### Stage 1 — UI Initiation & Preflight
- **Inputs**: `RevCustomerId` or `AmopCustomerId`, billing period, `serviceProviderIds`, rate-pool scope, feature flags (e.g., `SkipLowerCostCheck`).
- **Validation**: Portal repository queries `OptimizationCustomerProcessing` for active rows. Any row with `IsProcessed = 0` disables the CTA and surfaces "Optimization already running" messaging.
- **Payload**: UI sends tenant metadata, `PortalType = CrossProvider`, SSO context, and per-run toggle state to the optimization API.

### Stage 2 — API Guard Rails & Session Creation
- Re-run the concurrency guard; fail fast if `IsProcessed = 0` for the same tenant/customer.
- `UpdateOptCustomerProcessing` upserts start time, tenant, identifiers, placeholder device counts.
- `crossProviderOptimizationRepository.GetBillingPeriod` ensures `CustomerBillingPeriod` row exists; missing data aborts.
- `StartOptimizationSession` inserts `OptimizationSession (PortalType = CrossProvider)`.
- `StartOptimizationInstanceWithBillingPeriod` invokes `usp_Optimization_CreateCrossProviderOptimizationInstance`, binding billing period, service providers, timestamps.

### Stage 3 — Device Discovery & Rate Data Staging
- SIM discovery via `GetCrossProviderCustomerSimCards` and stored procs like `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards` filtered by provider/pool/portal.
- `ProjectDataUsageAndSaveDeviceByPortalType` partitions devices (Mobility vs M2M) and inserts into `OptimizationDevice` / `OptimizationMobilityDevice` with usage projections.
- `GetCrossCustomerRatePlans` validates rate-plan metadata. Missing `CustomerRatePlanId` entries are logged and skipped; missing metadata is treated as fatal.

### Stage 4 — Communication Groups & Queue Seeding
- `CreateCommPlanGroup` creates `OptimizationCommGroup` rows (pooled vs unpooled vs bill-in-advance).
- `CreateQueue` adds `OptimizationQueue` per group with `RunStatusId = NotStarted`.
- `CreateQueueRatePlans` bulk-inserts queue ↔ plan permutations (`OptimizationQueue_RatePlan`). Cross-provider runs allow `ServiceProviderId = null` when `IsCrossProviderOptimization = 1`.

### Stage 5 — Enqueue & Progress Tracking
- `EnqueueOptimizationRunsAsync` batches queue IDs, ensuring `RunStatusId` is still `NotStarted`.
- Device counts and timestamps flow back to `OptimizationCustomerProcessing` for portal progress badges.
- `EnqueueCrossProviderCustomerOptimizationSqsAsync` obtains the queue URL and publishes messages with attributes such as `TENANT_ID`, `OPTIMIZATION_SESSION_ID`, `PORTAL_TYPE_ID`, `SERVICE_PROVIDER_IDS`, `AMOP_CUSTOMER_ID`, `CUSTOMER_BILLING_PERIOD_ID`, etc.
- Failures (queue lookup, send issues) are logged and bubbled to UI/API responses.

### Stage 6 — Queue Execution (`QueueCustomerOptimization1`)
- SimCardCostOptimizer.Function processes one record per invocation. Missing `QueueIds` logs + exits without work.
- `ProcessQueues` / `ProcessQueuesContinue` hydrate `OptimizationQueue` entries, ensure status is not `CleaningUp` or `Complete`, and load the owning `OptimizationInstance`.
- Device load: `GetSimCardsByPortalType` routes cross-provider requests to `crossProviderOptimizationRepository.GetCrossProviderOptimizationDevices`, which wraps `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId` (union of `OptimizationDevice` and `OptimizationMobilityDevice`).
- Optional Redis caches SIM lists / rate pool state via `RedisCacheHelper`; SQL fallback is automatic.
- Missing device payloads throw and mark the queue as error.

### Stage 7 — Rate-Pool Assignment & Result Recording
- First queue calculates average usage, builds `RatePoolCollection` with `RatePoolCalculator`/`RatePoolFactory` (cross-provider runs honor `NoGrouping`).
- `RatePoolAssigner.AssignSimCards` iterates permutations (NoGrouping, GroupByCommunicationPlan) respecting pooling constraints.
- Lambda timeout avoidance: if nearing timeout and Redis is available, `WrapUpCurrentInstance` serializes state for continuation; otherwise execution ends gracefully to avoid partial writes.
- `RecordResultsIfBetter` compares costs against historical best per `OptimizationCommGroupId`. `RecordResults` writes to `OptimizationDeviceResult` / `OptimizationMobilityDeviceResult`, capturing pooled memberships and charges.
- `StopQueue` marks `RunStatusId` as `CompleteWithSuccess` or `CompleteWithErrors` and fills `RunEndTime`.

### Stage 8 — Cleanup (`SimCardCostOptimizerCleanup.Function`)
- Validates the `OptimizationInstance` is not finalized and all queues report `RunEndTime` (`INSTANCE_FINISHED_STATUSES`).
- `GetWinningQueueId` selects the best queue for each `OptimizationCommGroup`.
- `CleanupDeviceResultsForCommGroup` deletes non-winning results; winning queue entries are retained and labeled complete.
- Result collation:
  - `WriteCrossProviderCustomerResults` calls `usp_Optimization_GetCrossProviderOptimizationDeviceResults` to union Mobility + M2M outcomes, joining `CrossProviderDeviceHistory`, `Device`, `JasperCustomerRatePlan`, etc.
  - Shared pool handling via `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults` and storage into `OptimizationSharedPoolResult` / `OptimizationMobilitySharedPoolResult`.
- File generation writes Excel/text bundles into `OptimizationInstanceResultFile` and per-customer/pool summaries (`M2MOptimizationResult*`).
- `UpdateProcessingCustomerOptimizationInstance` updates `OptimizationCustomerProcessing` with totals, linking session + instance IDs.
- `QueueLastStepOptCustomerCleanup` emits the final SQS message to trigger notifications.

### Stage 9 — Notifications & Final Processing
- `OptCustomerSendEmail` re-checks `OptimizationCustomerProcessing` to ensure all work is done. When clear, it constructs `OptimizationCustomerEndProcess` payloads and hits the AMOP proxy API.
- `SendResponseToAMOP20` posts progress/error telemetry to `/get_optimization_progress_bar_data` or `/get_optimization_error_details_data` (includes session IDs, device counts, optional JSON payloads).
- `SendAllCustomersCrossProviderOptimizationSummaryEmail` composes SES HTML summary emails using `BuildAllCustomersSummaryEmailSubject/Body`, showing eligible device counts per service provider/billing period and any errors.
- Once notifications finish, `OptimizationCustomerProcessing` rows are deleted, signaling completion.

### Stage 10 — UI Completion & Asset Retrieval
- Portal polls `OptimizationCustomerProcessing`; when `IsProcessed = 1` and `EndTime` exists, the run shows as complete with error timestamps/counters.
- Downloads pull metadata from `OptimizationInstanceResultFile` (or blob/S3 references) to fetch Excel/stat bundles created in Stage 8.
- Cross-provider tracking surfaces (via `crossProviderOptimizationRepository`) expose badges for shared pool notifications, summary emails, and downstream pushes.
- `CrossProviderCustomerRatePoolUsage` UI + export views call `GetCrossProviderCustomerPoolAggregatedUsage` → `usp_GetCrossProviderCustomerPoolAggregatedUsage` to show pooled usage vs allocation with tenant/site filters.

---

## 5. Data Contracts & Tables by Stage
| Stage | Key Tables | Purpose |
| --- | --- | --- |
| 1–2 | `OptimizationCustomerProcessing`, `CustomerBillingPeriod`, `OptimizationSession`, `OptimizationInstance` | Concurrency guard, billing resolution, session metadata |
| 3 | `OptimizationDevice`, `OptimizationMobilityDevice`, `CustomerRatePlan`, `CustomerRatePool`, `CrossProviderDeviceHistory`, `ServiceProvider` | Device staging, rate plan validation, historical context |
| 4 | `OptimizationCommGroup`, `OptimizationQueue`, `OptimizationQueue_RatePlan` | Communication groups, queue definitions, plan permutations |
| 5 | `OptimizationCustomerProcessing` | Progress counters surfaced to UI |
| 6 | `OptimizationQueue`, `OptimizationInstance`, `OptimizationDevice*`, `RedisCacheHelper` | Queue execution, device reloads |
| 7 | `OptimizationDeviceResult`, `OptimizationMobilityDeviceResult`, `OptimizationQueue` | Result persistence, queue status updates |
| 8 | `OptimizationCommGroup`, `OptimizationSharedPoolResult*`, `OptimizationInstanceResultFile`, `M2MOptimizationResult*` | Winner selection, file generation |
| 9 | `OptimizationCustomerProcessing`, `OptimizationCustomerEndProcess` | Notification gating, cleanup |
| 10 | `OptimizationInstanceResultFile`, repository views, `CustomerRatePool` usage tables | UI completion, exports, badges |

---

## 6. Failure Modes & Safeguards
| Area | Guard Rail |
| --- | --- |
| Concurrent Runs | UI and API both check `OptimizationCustomerProcessing.IsProcessed = 0` to block overlapping optimizations per tenant/customer. |
| Missing Billing Period | `GetBillingPeriod` aborts if `CustomerBillingPeriod` rows are absent. |
| Invalid SIM / Rate-plan Data | Stage 3 logs and skips SIMs without `CustomerRatePlanId`; missing plan metadata halts the run. |
| Queue Re-entrancy | Stage 6 rejects queues already `CleaningUp`/`Complete`, preventing double processing. |
| Lambda Timeouts | `WrapUpCurrentInstance` + Redis serialization enable continuation; otherwise the Lambda exits before partial writes. |
| Result Integrity | `RecordResultsIfBetter` ensures only cost-improving results persist; cleanup Lambda re-validates winning queues. |
| Notification Gaps | `OptCustomerSendEmail` re-checks `OptimizationCustomerProcessing` before sending emails to avoid premature deletions. |

---

## 7. Operational Checklist
**Before Starting**
- Confirm UI preflight passes (`OptimizationCustomerProcessing` has no active rows).
- Ensure the requested billing period exists in `CustomerBillingPeriod`.
- Validate `serviceProviderIds` comply with tenant permissions.

**During Execution**
- Monitor SQS delivery metrics and Lambda logs for `QueueCustomerOptimization1`.
- Track `OptimizationQueue` statuses to ensure queues progress toward `RunEndTime`.
- Inspect `OptimizationDeviceResult` for cost anomalies or missing pools.

**After Completion**
- Verify `OptimizationCustomerProcessing` row removed or `IsProcessed = 1` with `EndTime`.
- Confirm `OptimizationInstanceResultFile` entries exist and byte sizes match expectations.
- Check AMOP progress notifications (`SendResponseToAMOP20` logs) and SES summary email delivery.
- Review `CrossProviderCustomerRatePoolUsage` UI/export for accurate post-run reporting.

---

## 8. Stored Procedure Reference
| Procedure | Role |
| --- | --- |
| `usp_Optimization_CreateCrossProviderOptimizationInstance` | Inserts `OptimizationInstance` with cross-provider metadata, returns `InstanceId`. |
| `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards` | Retrieves eligible SIMs from shared pools filtered by providers/pools/site type. |
| `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId` | Returns staged Mobility/M2M devices for an instance, honoring portal filters. |
| `usp_Optimization_GetCrossProviderOptimizationDeviceResults` | Unions per-queue Mobility + M2M results with history lookups for ICCID/MSISDN. |
| `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults` | Aggregates shared-pool outcomes across Mobility/M2M. |
| `usp_GetCrossProviderCustomerPoolAggregatedUsage` | Produces pooled usage vs allocation per customer rate pool (historical + current). |

---

## 9. Messaging & Notification Details
### SQS Message Attribute Map
| Attribute | Source | Notes |
| --- | --- | --- |
| `TENANT_ID` | Portal/API | Stringified tenant ID |
| `OPTIMIZATION_SESSION_ID` | Session creation | Links queues back to session |
| `CUSTOMER_TYPE` | SiteType enum | Distinguishes REV vs AMOP |
| `IS_LAST_INSTANCE` | API flag | Drives cleanup decisions |
| `PORTAL_TYPE_ID` | PortalTypes.CrossProvider | Downstream routing |
| `SERVICE_PROVIDER_IDS` | Feature flag guard | JSON string; only for cross-provider runs |
| `AMOP_CUSTOMER_ID` | Optional | Provided for AMOP opt-in |
| `CUSTOMER_BILLING_PERIOD_ID` / `BILL_PERIOD_ID` | Billing resolution | Field name depends on feature toggle |
| `INTEGRATION_AUTHENTICATION_ID`, `CUSTOMER_ID` | REV-only fallback | Legacy integration support |

### Notification Touchpoints
- **AMOP 2.0**: `SendResponseToAMOP20` posts to `/get_optimization_progress_bar_data` or `/get_optimization_error_details_data` with session IDs, device counts, error text, progress percent, optional `AdditionalJson`.
- **Summary Emails**: `SendAllCustomersCrossProviderOptimizationSummaryEmail` computes recipients from `OptimizationSettings` and sends HTML tables summarizing eligible devices per provider/billing period; includes error blocks when present.
- **Portal Badges**: Derived from tracking tables via `crossProviderOptimizationRepository`, plus `OptimizationCustomerProcessing` counters, to show shared pool notifications, email completions, and downloadable assets.

---

## 10. Reporting & UX Extensions
### Cross-Provider Pooled Usage
- Controller: `CrossProviderCustomerRatePoolUsage`, `_CrossProviderCustomerRatePoolLineDetails`, `CrossProviderCustomerRatePoolUsageExport`.
- Repository: `GetCrossProviderCustomerPoolAggregatedUsage`, `GetCrossProviderCustomerPoolLineDetails`, `GetCrossProviderCustomerPoolLineExport`.
- Stored Procedure: `usp_GetCrossProviderCustomerPoolAggregatedUsage` (supports historical runs via `CrossProviderDeviceHistory` and current billing periods via `vw*M2M/Mobility`).
- Export: `ExcelUtilities.Export` builds multi-sheet datasets; non-admins receive restricted columns (Provider hidden) per least-privilege rules.

### Dropdowns & Filtering
- `CrossProviderCustomerDropdown` relies on `ListHelper.SiteOptimizationList` with permission-aware filters, service provider scoping, and portal context (REV vs AMOP).

### AMOP 2.0 Progress Hooks
- `SendResponseToAMOP20` keeps AMOP progress bars in sync with optimization state, providing progress %, device counts, and error payloads.

### Summary Emails
- `SendAllCustomersCrossProviderOptimizationSummaryEmail` builds subject `"{ServiceProviderName} Optimization Summary - All Customers ({TenantName})"` with environment suffix for non-prod; body is HTML with customer/device tables and optional error sections.

---

## 11. Troubleshooting Guide
1. **Stuck Runs**
   - Check `OptimizationCustomerProcessing` for `IsProcessed = 0` with stale timestamps. Use `RunStartTime` / `RunEndTime` deltas to identify blocked stages.
   - Inspect `OptimizationQueue` for rows lacking `RunEndTime`; correlate `QueueIds` with SQS/Lambda logs.
2. **Queue Failures**
   - Look at Lambda logs for `QueueCustomerOptimization1`; ensure `QueueIds` were passed and `RunStatusId` not already `CleaningUp`.
   - Verify `OptimizationDevice*` entries exist for the `OptimizationInstanceId` using `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId`.
3. **Missing Results / Files**
   - Confirm cleanup Lambda executed (`OptimizationCommGroup` winning queue reference populated).
   - Ensure `OptimizationInstanceResultFile` rows exist with non-null bytes + metadata; regenerate via cleanup Lambda if missing.
4. **Notification Issues**
   - Validate `OptCustomerSendEmail` logs for `OptimizationCustomerProcessing` state mismatches.
   - Re-run `SendResponseToAMOP20` if AMOP progress bars stale; confirm `OptimizationCustomerEndProcess` rows were produced.
5. **Usage View Discrepancies**
   - Compare `CrossProviderCustomerRatePoolUsage` UI output against `usp_GetCrossProviderCustomerPoolAggregatedUsage` results. Ensure tenant filters and site permissions align.

---

## 12. Extensibility & Best Practices
- Apply new validations at both UI and API to keep guard rails consistent.
- When adding post-processing steps, register both cleanup and notification dependencies so the Lambda chain remains linear (execution → cleanup → notification).
- Leverage Redis caching for SIM-heavy customers to avoid Lambda timeouts; adjust `RedisCacheHelper` TTL to remain below Lambda runtime limits.
- Email recipients must originate from `OptimizationSettings`; missing configuration should log and skip sends to avoid partial notification states.
- Non-admin exports must respect least-privilege column sets; extend `CrossProviderCustomerRatePoolUsageExport` accordingly when new data points are added.

---

## 13. Reference Appendices
### A. Queue & Rate-Plan Scaffolding Timeline
1. `CreateCommPlanGroup` → partitions devices (pooled/unpooled/BIA) and creates `OptimizationCommGroup` rows.
2. `CreateQueue` → 1..N `OptimizationQueue` per comm group, `RunStatusId = NotStarted`.
3. `CreateQueueRatePlans` → multiplies queue permutations across available rate plans, writing to `OptimizationQueue_RatePlan`.
4. `EnqueueOptimizationRunsAsync` → selects queue batches that remain `NotStarted` and publishes SQS messages.

### B. Result File Generation Highlights
- Cleanup Lambda loads winning queues, unions Mobility/M2M results, and calls `ExcelUtilities` (or equivalent) to create per-customer, per-pool Excel sheets.
- Files stored in `OptimizationInstanceResultFile` include byte payload, MIME metadata, and environment-safe filenames; UI references this table for downloads.

### C. Data Export Security Notes
- Portal enforces tenant/site permissions prior to building exports. For cross-provider views, provider columns are hidden unless the user is an admin or has explicit grants.
- Excel generation uses `ExcelUtilities.Export` with column suppression arrays; update these arrays whenever columns change.

---

_Last updated: 2025-12-01_
