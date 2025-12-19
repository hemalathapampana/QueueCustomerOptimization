## Cross‑Provider flow: where “Device Count” comes from (and where it does **not**)

This repo snapshot only contains `Fuction.cs` (Lambda entrypoint). In this code, **there is no single `DeviceCount` variable that gets computed and returned at the end**.

What this Lambda *does* do is:

- **Fetch a list of devices (SIM cards)** for the Cross‑Provider customer + billing period + provider(s)
- Run the optimization workflow on those devices
- **Enqueue cleanup** (which is typically where “final results” are aggregated and sent back)

So, in *this* codebase, the only concrete/traceable source for **Device Count** is:

- **The number of device rows returned by the repository calls** (i.e., `List.Count`) after any filtering.

If you’re seeing a “Device Count” value *after completion of optimization* in another system (AMOP UI/API), that value is **not computed in this repo**—it must be computed downstream (likely cleanup / result aggregation / API trigger).

---

## 1) Cross‑Provider entrypoint (message routing)

Cross‑Provider optimization is chosen when `portalType != PortalTypes.M2M`.

- File: `Fuction.cs`
- Method: `ProcessEventRecord(...)`
- Cross‑Provider branch calls: `ProcessCrossProviderCustomerOptimization(...)`

Key point: at this stage, the code builds `additionalData` that includes a `DeviceCount = 0` placeholder.

This means **this Lambda is not populating DeviceCount in the payload**.

---

## 2) Cross‑Provider “device list” retrieval (this is the actual source of counts)

### 2.1 Devices used for optimization (with rate plan code)

The device list for Cross‑Provider optimization is retrieved here:

- File: `Fuction.cs`
- Method: `ProcessCrossProviderDevicesByCustomerRatePlans(...)`
- Exact retrieval call:

  - `crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards(...)`

Immediately after, the list is filtered:

- `optimizationSimCards = optimizationSimCards.Where(s => !string.IsNullOrWhiteSpace(s.CustomerRatePlanCode)).ToList();`

So the **count of devices that actually participate in optimization** (in this Lambda) is:

- `optimizationSimCards.Count` **after** that `.Where(...)` filter.

This is the closest thing to an “optimized device count” in the Cross‑Provider path.

### 2.2 Devices excluded from optimization (no rate plan code)

Devices without a rate plan code are handled in a separate step, which re-queries the device list and filters the opposite direction:

- File: `Fuction.cs`
- Method: `ProcessNoRatePlanCrossProviderDevices(...)`
- Retrieval call:

  - `crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards(...)`

Filtered to no-rate-plan devices:

- `Where(c => string.IsNullOrWhiteSpace(c.CustomerRatePlanCode))`

So the **count of devices excluded because they have no rate plan** is:

- `noRatePlanCodes.Count`

### 2.3 Total devices for that customer+period+providers (best estimate from this file)

Because the code re-fetches and filters in two different places, the practical “total device count” for the scope is:

- `TotalDevices ~= DevicesWithRatePlanCode + DevicesWithNoRatePlanCode`

Where:

- `DevicesWithRatePlanCode` is the count after `!IsNullOrWhiteSpace(CustomerRatePlanCode)`
- `DevicesWithNoRatePlanCode` is the count after `IsNullOrWhiteSpace(CustomerRatePlanCode)`

Both counts are based on the same repository method:

- `crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards(...)`

---

## 3) What happens “after completion” (why you don’t see DeviceCount here)

When Cross‑Provider optimization finishes successfully, this Lambda:

- Enqueues cleanup:
  - `EnqueueCleanup(context, instanceId, isCustomerOptimization: true, isLastInstance: isLastInstance);`

When it fails:

- Calls `OptimizationAmopApiTrigger.SendResponseToAMOP20(...)` with an error message.

Important:

- The `additionalData` JSON built in `ProcessEventRecord(...)` includes `DeviceCount = 0`.
- This file does **not** update that value later.

Therefore, if you’re getting a non-zero “Device Count” in a final response/UI, it must be coming from:

- Cleanup / aggregation code not present in this workspace, OR
- `OptimizationAmopApiTrigger` internals (from referenced libraries), OR
- A database view/table queried later by another component.

---

## 4) How to confirm device count in logs (quick instrumentation points)

If you want to log exactly what this Lambda considers “device count”, the two high-signal points are:

1) After fetching Cross‑Provider sim cards (before filtering), log:

- `allSimCards.Count`

2) After filtering devices with rate plan codes, log:

- `optimizationSimCards.Count`

3) In `ProcessNoRatePlanCrossProviderDevices(...)`, log:

- `noRatePlanCodes.Count`

These three numbers will tell you:

- Total devices retrieved for scope
- Devices participating in optimization
- Devices excluded due to missing rate plan code

---

## 5) Key “where to look next” (outside this repo)

If your question is specifically:

> “After optimization completes, from where is `DeviceCount` retrieved in the final response?”

Then you need to locate the component that:

- runs during/after `EnqueueCleanup(...)`, or
- calls `OptimizationAmopApiTrigger.SendResponseToAMOP20(...)` for success responses.

In this workspace snapshot, those implementations are **not present**, so the final response’s `DeviceCount` cannot be traced further here.
