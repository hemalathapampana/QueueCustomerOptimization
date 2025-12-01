# Cross-Provider Customer Optimization Flow

Comprehensive walkthrough of the cross-provider customer optimization lifecycle, mapping UI events to API, SQS, Lambda, and database interactions. Use this as the authoritative reference when implementing new features, debugging stuck runs, or onboarding teammates.

---

## 1. High-Level Narrative

1. **Tenant user triggers optimization** from the portal UI, selecting customer, billing period, service providers, pool scope, and feature flags.
2. **API validates concurrency** via `OptimizationCustomerProcessing`, resolves billing context, creates `OptimizationSession`/`OptimizationInstance`, and stages SIM + rate data.
3. **Queue & rate-plan scaffolding** forms communication groups, queue definitions, and queue ↔ rate-plan mappings.
4. **Queue batches are pushed to SQS** (`QueueCustomerOptimization1` Lambda) where devices are reloaded, rate pools calculated, and results recorded per queue.
5. **Cleanup Lambda** finalizes winning queues, aggregates results into files, updates cross-provider tracking, and schedules the email workflow.
6. **Notification + UX completion**: SQS-triggered processes email stakeholders, purge `OptimizationCustomerProcessing`, and the UI exposes downloadable assets and status badges.

---

## 2. Component Responsibilities

| Layer | Responsibilities | Key Code / Procedures |
| --- | --- | --- |
| UI (Portal) | Input gathering, preflight status checks, progress polling, result download, aggregated usage views | `CrossProviderCustomerDropdown`, `CrossProviderCustomerRatePoolUsage`, `_CrossProviderCustomerRatePoolLineDetails`, export actions |
| API / MVC | Validation, session creation, staging, queue orchestration, SQS enqueues, AMOP 2.0 responses | `EnqueueCrossProviderCustomerOptimizationSqsAsync`, `SendResponseToAMOP20`, `SendAllCustomersCrossProviderOptimizationSummaryEmail` |
| Database | Stores optimization state, queues, devices, history, results, rate pools | `OptimizationCustomerProcessing`, `OptimizationSession`, `OptimizationInstance`, `OptimizationDevice*`, `OptimizationQueue*`, `CrossProviderDeviceHistory`, `OptimizationInstanceResultFile`, etc. |
| Stored Procedures | Bulk data access/manipulation for SIM discovery, result collation, aggregated usage views | `usp_Optimization_CreateCrossProviderOptimizationInstance`, `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards`, `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId`, `usp_Optimization_GetCrossProviderOptimizationDeviceResults`, `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults`, `usp_GetCrossProviderCustomerPoolAggregatedUsage` |
| Messaging | SQS queue for queue execution, follow-up cleanup queue | `QueueCustomerOptimization1` Lambda, `SimCardCostOptimizerCleanup.Function`, `QueueLastStepOptCustomerCleanup` |
| Notification | AMOP proxy, SES summary emails, UI badges | `OptCustomerSendEmail`, `SendAllCustomersCrossProviderOptimizationSummaryEmail`, AMOP 2.0 proxy |

---

## 3. Detailed Stage-by-Stage Flow

### Stage 1 — UI Initiation & Preflight Validations
- **Inputs**: `RevCustomerId` or `AmopCustomerId`, billing period, `serviceProviderIds`, rate-pool scope, flags (`SkipLowerCostCheck`, etc.).
- **Validation**: UI calls `CheckOptCustomerProcessing` (via repo) to ensure no `OptimizationCustomerProcessing` rows exist where `IsProcessed = 0` for the tenant/customer. Active rows disable the Optimize CTA.
- **Payload Assembly**: Portal bundles selections, tenant metadata, portal type (`CrossProvider`), and per-run feature toggles before invoking the optimization API.
- **Failure UX**: “Optimization already running” message surfaces if any conflicting `OptimizationCustomerProcessing` row remains unprocessed.

### Stage 2 — API Guard Rails & Session Creation
- **Concurrency Guard**: API repeats the `OptimizationCustomerProcessing` check; fails fast if `IsProcessed = 0`.
- **Session Tracking**: `UpdateOptCustomerProcessing` upserts (or updates) the row with start time, tenant, identifiers, and placeholder device counts.
- **Billing Period Resolution**: `crossProviderOptimizationRepository.GetBillingPeriod` ensures the requested cycle exists (`CustomerBillingPeriod`). Missing periods abort and log.
- **Session & Instance Records**:
  - `StartOptimizationSession` → inserts `OptimizationSession` (`PortalType = CrossProvider`).
  - `StartOptimizationInstanceWithBillingPeriod` → calls `usp_Optimization_CreateCrossProviderOptimizationInstance` to insert `OptimizationInstance` with billing period bindings, service provider set, and timestamps.

### Stage 3 — Device Discovery & Rate Data Staging
- **SIM Harvesting**: `crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards` leverages procedures such as `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards` to filter SIMs by service providers, pools, and portal type. SIMs without `CustomerRatePlanId` are logged and skipped.
- **Usage Projection**: `ProjectDataUsageAndSaveDeviceByPortalType` splits devices by portal (Mobility vs M2M). Inserts into `OptimizationDevice` and `OptimizationMobilityDevice` with usage projections and original `ServiceProviderId`.
- **Customer Rate Plans**: `GetCrossCustomerRatePlans` ensures plan metadata (name, pooling flags, overage rules) is present. Missing data halts the run with descriptive logs.

### Stage 4 — Communication Groups, Queue Seeding, Rate-Plan Mapping
- **Grouping**: `CreateCommPlanGroup` partitions devices into `OptimizationCommGroup` rows (pooled, unpooled, bill-in-advance).
- **Queue Creation**: `CreateQueue` generates `OptimizationQueue` entries for each comm group, defaulting to `RunStatusId = NotStarted`.
- **Queue ↔ Plan Mapping**: `CreateQueueRatePlans` bulk inserts into `OptimizationQueue_RatePlan`, capturing all plan permutations per queue. Cross-provider runs allow `ServiceProviderId` to be null when `IsCrossProviderOptimization = 1`.

### Stage 5 — Queue Enqueue & Progress Tracking
- **Preparation**: `EnqueueOptimizationRunsAsync` batches queue IDs, ensuring each queue is still `RunStatusId = NotStarted`. Device counts/time stamps update `OptimizationCustomerProcessing` for UI progress.
- **SQS Messages**: `EnqueueCrossProviderCustomerOptimizationSqsAsync` obtains the queue URL, then publishes messages with attributes such as `TENANT_ID`, `OPTIMIZATION_SESSION_ID`, `PORTAL_TYPE_ID`, `SERVICE_PROVIDER_IDS`, `AMOP_CUSTOMER_ID`, `CUSTOMER_BILLING_PERIOD_ID`, etc.
- **Failure Handling**: Queue lookup or send failures log errors and return message strings consumed by the UI/API.
- **UI Polling**: Portal polls `OptimizationCustomerProcessing` to render “in progress.”

### Stage 6 — Queue Execution (Lambda `QueueCustomerOptimization1`)
- **SQS Intake**: `SimCardCostOptimizer.Function.Handler` enforces one record per invocation. Missing `QueueIds` logs and exits.
- **Metadata Validation**: `ProcessQueues` / `ProcessQueuesContinue` hydrate `OptimizationQueue` entries, ensuring status is not already `CleaningUp` or `Complete` and retrieving owning `OptimizationInstance`.
- **Device Loading**: `GetSimCardsByPortalType` routes cross-provider runs to `crossProviderOptimizationRepository.GetCrossProviderOptimizationDevices`. Under the hood, `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId` unions `OptimizationDevice` + `OptimizationMobilityDevice`.
- **Redis (Optional)**: `RedisCacheHelper` attempts to hydrate cached SIM lists / `RatePoolAssigner` state; SQL fallback is automatic.
- **Error Handling**: Missing device payloads throw and mark queue as error.

### Stage 7 — Rate-Pool Assignment & Result Recording
- **Preparation**: First queue computes average usage and builds `RatePoolCollection` via `RatePoolCalculator`/`RatePoolFactory`. Grouping modes honor cross-provider restrictions (customer opt runs enforce `NoGrouping`).
- **Assignment Loop**: `RatePoolAssigner.AssignSimCards` evaluates permutations (`NoGrouping`, `GroupByCommunicationPlan`) respecting forced pooling rules.
- **Timeout Strategy**: If Lambda processing nears timeout and Redis is available, `WrapUpCurrentInstance` persists partial state for continuation; otherwise, execution halts to avoid inconsistent writes.
- **Result Persistence**: `RecordResultsIfBetter` checks new cost vs. historical best for the `CommPlanGroupId`. `RecordResults` writes to `OptimizationDeviceResult` / `OptimizationMobilityDeviceResult`, including pooled memberships and charges.
- **Queue Finalization**: `StopQueue` updates `RunStatusId` to `CompleteWithSuccess` or `CompleteWithErrors`, stamping `RunEndTime`.

### Stage 8 — Cleanup Lambda (`SimCardCostOptimizerCleanup.Function`)
- **Instance Validation**: Confirms `OptimizationInstance` is not finalized and all queues possess `RunEndTime` (`INSTANCE_FINISHED_STATUSES`).
- **Winning Queue Selection**: `GetWinningQueueId` determines the best queue per `OptimizationCommGroup`. `CleanupDeviceResultsForCommGroup` removes non-winning results, marking remaining queues as complete.
- **Result Collation**:
  - Non-shared: `WriteCrossProviderCustomerResults` calls `usp_Optimization_GetCrossProviderOptimizationDeviceResults` to union Mobility/M2M results, joining `CrossProviderDeviceHistory`, `Device`, `JasperCustomerRatePlan`, etc.
  - Shared pools: `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults` handles pooled outputs (`OptimizationSharedPoolResult`, `OptimizationMobilitySharedPoolResult`).
  - Files: Generates Excel/text bundles, storing bytes + metadata in `OptimizationInstanceResultFile` and per-customer/pool summaries (`M2MOptimizationResult*`).
- **Tracking Update**: `crossProviderOptimizationRepository.UpdateProcessingCustomerOptimizationInstance` updates `OptimizationCustomerProcessing` with totals, linking session/instance + REV/AMOP identifiers.
- **Post-Processing Trigger**: `QueueLastStepOptCustomerCleanup` emits an SQS message that ultimately invokes `OptCustomerSendEmail`.

### Stage 9 — Notifications & Final Processing
- **Email Workflow**: `OptCustomerSendEmail` validates no outstanding work via `OptimizationCustomerProcessing`. On success, it constructs `OptimizationCustomerEndProcess` payloads and calls the AMOP proxy API.
- **AMOP Progress/Error API**: `SendResponseToAMOP20` posts progress/error telemetry to `/get_optimization_progress_bar_data` or `/get_optimization_error_details_data`, including session IDs, device counts, errors, and optional JSON payloads.
- **Summary Emails**: `SendAllCustomersCrossProviderOptimizationSummaryEmail` builds SES messages summarizing eligible device counts per service provider/billing period using `BuildAllCustomersSummaryEmailSubject/Body`.
- **Row Cleanup**: Once notifications complete, `OptimizationCustomerProcessing` rows are deleted, signaling full completion.

### Stage 10 — UI Completion & Result Retrieval
- **Status Badges**: The UI (or background job) polls `OptimizationCustomerProcessing`. When `IsProcessed = 1` and `EndTime` is populated, the run shows as complete, including error timestamps/counters.
- **Result Downloads**: UX requests metadata from `OptimizationInstanceResultFile` (or blob/S3 references) to surface Excel/stat bundles generated in Stage 8.
- **Additional Badges**: Cross-provider tracking tables (via `crossProviderOptimizationRepository`) expose badges for shared pool notifications, email sends, and downstream pushes.
- **Aggregated Usage Screens**: `CrossProviderCustomerRatePoolUsage` displays pooled usage vs. allocation, calling `GetCrossProviderCustomerPoolAggregatedUsage` → `usp_GetCrossProviderCustomerPoolAggregatedUsage`. Filters respect tenant/site permissions, and exports use `ExcelUtilities.Export` with multi-sheet datasets.

---

## 4. Data Contracts & Tables (by Stage)

| Stage | Key Tables | Purpose |
| --- | --- | --- |
| 1-2 | `OptimizationCustomerProcessing`, `CustomerBillingPeriod`, `OptimizationSession`, `OptimizationInstance` | Concurrency guard, billing resolution, run/session metadata |
| 3 | `OptimizationDevice`, `OptimizationMobilityDevice`, `CustomerRatePlan`, `CustomerRatePool`, `CrossProviderDeviceHistory`, `ServiceProvider` | Device staging, rate plan validation, historical context |
| 4 | `OptimizationCommGroup`, `OptimizationQueue`, `OptimizationQueue_RatePlan` | Group partitioning, queue definitions, plan permutations |
| 5 | `OptimizationCustomerProcessing` (counts/timestamps) | Progress tracking surfaced to UI |
| 6 | `OptimizationQueue`, `OptimizationInstance`, `OptimizationDevice*`, `RedisCacheHelper` | Queue execution, device reloads |
| 7 | `OptimizationDeviceResult`, `OptimizationMobilityDeviceResult`, `OptimizationQueue` | Result persistence, queue status updates |
| 8 | `OptimizationCommGroup`, `OptimizationSharedPoolResult*`, `OptimizationInstanceResultFile`, `M2MOptimizationResult*` | Winner selection, file generation, shared pool outputs |
| 9 | `OptimizationCustomerProcessing`, `OptimizationCustomerEndProcess` | Notification gating, cleanup |
| 10 | `OptimizationInstanceResultFile`, `crossProviderOptimizationRepository` views, `CustomerRatePool` usage tables | UI completion, result downloads, badges |

---

## 5. Failure Modes & Safeguards

| Area | Guard Rail |
| --- | --- |
| **Concurrent Runs** | Dual checks (UI + API) on `OptimizationCustomerProcessing.IsProcessed = 0` prevent overlapping optimizations per tenant/customer. |
| **Billing Period Missing** | `GetBillingPeriod` aborts early if `CustomerBillingPeriod` rows absent for the customer. |
| **Invalid SIM/Rate Plan Data** | Stage 3 logging + discard logic skip SIMs lacking `CustomerRatePlanId`; missing metadata halts run. |
| **Queue Re-entrancy** | Stage 6 rejects queues already `CleaningUp`/`Complete`, preventing double processing. |
| **Lambda Timeouts** | `WrapUpCurrentInstance` + Redis serialization enable continuation without data loss; otherwise Lambda aborts before partial writes. |
| **Result Integrity** | `RecordResultsIfBetter` ensures only cost-improving results overwrite history; cleanup Lambda re-validates winning queues. |
| **Notification Gaps** | `OptCustomerSendEmail` re-checks `OptimizationCustomerProcessing` before sending emails to avoid premature deletions. |

---

## 6. Reporting & UX Extensions

- **Cross-Provider Pooled Usage (UI/Export)**
  - Controller: `CrossProviderCustomerRatePoolUsage`, `_CrossProviderCustomerRatePoolLineDetails`, `CrossProviderCustomerRatePoolUsageExport`.
  - Repository: `GetCrossProviderCustomerPoolAggregatedUsage`, `GetCrossProviderCustomerPoolLineDetails`, `GetCrossProviderCustomerPoolLineExport`.
  - Stored Procedure: `usp_GetCrossProviderCustomerPoolAggregatedUsage` (handles both historical runs via `CrossProviderDeviceHistory` and current billing period via `vw*M2M/Mobility`).
  - Export: `ExcelUtilities.Export` formats datasets (per-line + summary) with admin-aware column suppression.

- **Dropdowns & Filtering**
  - `CrossProviderCustomerDropdown` → `ListHelper.SiteOptimizationList` respects permission filters, service provider selections, and portal (REV vs AMOP) contexts.

- **AMOP 2.0 Progress Hooks**
  - `SendResponseToAMOP20` posts progress/errors to configured endpoints, ensuring external systems reflect optimization state.

- **Summary Emails**
  - `SendAllCustomersCrossProviderOptimizationSummaryEmail` derives recipients from `OptimizationSettings` table and composes HTML summary with error sections when needed.

---

## 7. Operational Checklist

1. **Before starting a run**
   - Confirm UI preflight passes (no active `OptimizationCustomerProcessing` row).
   - Ensure billing period exists in `CustomerBillingPeriod`.
   - Validate `serviceProviderIds` align with tenant permissions.

2. **During execution**
   - Monitor SQS delivery metrics and Lambda logs for `QueueCustomerOptimization1`.
   - Track `OptimizationQueue` statuses to ensure progress toward `RunEndTime`.
   - Inspect `OptimizationDeviceResult` for cost anomalies or missing pools.

3. **After completion**
   - Verify `OptimizationCustomerProcessing` row removed (or `IsProcessed = 1` with `EndTime`).
   - Check `OptimizationInstanceResultFile` entries exist and match expected byte sizes.
   - Confirm AMOP progress notifications succeeded (via `SendResponseToAMOP20` logs) and SES summary emails delivered.
   - Review `CrossProviderCustomerRatePoolUsage` UI/export for accurate post-run reporting.

---

## 8. Reference: Stored Procedures & Key Outputs

| Procedure | Role |
| --- | --- |
| `usp_Optimization_CreateCrossProviderOptimizationInstance` | Inserts `OptimizationInstance` with cross-provider metadata, returning `InstanceId`. |
| `usp_Optimization_GetCrossProviderCustomerSharedPoolSimCards` | Retrieves eligible SIMs from shared pools, filtering by providers/pools/site type using temp tables for input splitting. |
| `usp_Optimization_GetCrossProviderOptimizationDevicesByInstanceId` | Returns staged Mobility/M2M devices for a specific instance, honoring portal type filters. |
| `usp_Optimization_GetCrossProviderOptimizationDeviceResults` | Unions per-queue Mobility + M2M results with history lookups for ICCID/MSISDN. |
| `usp_Optimization_GetCrossProviderOptimizationSharedPoolDeviceResults` | Aggregates shared-pool results across Mobility/M2M. |
| `usp_GetCrossProviderCustomerPoolAggregatedUsage` | Produces pooled usage vs. allocation per customer rate pool, supporting historical (billing-period) and current-cycle contexts. |

---

## 9. Appendices

### A. SQS Message Attribute Map

| Attribute | Source | Notes |
| --- | --- | --- |
| `TENANT_ID` | Portal/API | Tenant identifier (stringified int). |
| `OPTIMIZATION_SESSION_ID` | `optimizationSessionId` | Links queues back to session. |
| `CUSTOMER_TYPE` | `SiteType` enum | Distinguishes REV vs AMOP. |
| `IS_LAST_INSTANCE` | API flag | Drives cleanup decisions. |
| `PORTAL_TYPE_ID` | `PortalTypes.CrossProvider` | Downstream flag. |
| `SERVICE_PROVIDER_IDS` | JSON string | Included only when cross-provider feature flag enabled. |
| `AMOP_CUSTOMER_ID` | Optional | Provided with cross-provider opt-in. |
| `CUSTOMER_BILLING_PERIOD_ID` / `BILL_PERIOD_ID` | Billing context | Field names depend on cross-provider feature toggle. |
| `INTEGRATION_AUTHENTICATION_ID`, `CUSTOMER_ID` | REV-only fallback path. |

### B. Notification Touchpoints

1. **AMOP 2.0 API (`SendResponseToAMOP20`)**
   - Paths: `/get_optimization_progress_bar_data` or `/get_optimization_error_details_data`.
   - Payload: session identifiers, device count, error text, progress percent, optional `AdditionalJson`.
2. **Summary Email (SES)**
   - Subject format: `{ServiceProviderName} Optimization Summary - All Customers ({TenantName})` with environment suffix for non-prod.
   - Body: HTML table of customers + eligible device count, optional errors block.
3. **Portal UI badges**
   - Source: cross-provider tracking tables + `OptimizationCustomerProcessing` counters.

---

## 10. Usage Notes

- **Extensibility**: When adding new validation or post-processing steps, ensure both UI and API layers share the guard rail to maintain consistent UX.
- **Troubleshooting**: Start with `OptimizationCustomerProcessing` for stuck statuses, then inspect `OptimizationQueue` and Lambda logs based on `QueueIds` referenced in SQS messages.
- **Performance**: Shared-pool heavy customers benefit from Redis caching; ensure `RedisCacheHelper` settings stay aligned with Lambda timeout thresholds.
- **Security**: All email addresses originate from `OptimizationSettings`; missing config should log errors and avoid partial sends.
- **Exports**: Non-admin users receive restricted columns (Provider removed) per `CrossProviderCustomerRatePoolUsageExport` to maintain least privilege.

---

_Last updated: Dec 1, 2025._
