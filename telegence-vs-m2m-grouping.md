# Telegence vs M2M “Grouping” (Core Difference)

## What “grouping” means (the core distinction)

### M2M customer optimization: **Comm Plans**
- Lines are grouped into **Commercial Plans (Comm Plans)** — the commercial “bucket” a line belongs to.
- Optimization typically occurs **within a Comm Plan** (or using Comm Plan–specific eligibility/constraints).
- Candidate rate plans are usually “plans that belong to / are allowed for this Comm Plan.”

### Telegence customer optimization: **Optimization Groups (OGs)**
- Lines are grouped into **Optimization Groups (OGs)** — the primary unit for optimization.
- A Comm Plan concept may be **absent, unreliable, or not available**, so the **OG replaces Comm Plan** as the key organizing bucket.
- Candidate rate plans are filtered **by OG**, making OG the main mechanism to:
  - control eligibility,
  - apply constraints,
  - drive candidate selection,
  - and support rollups/reporting.

## Quick mental model

- **M2M**: `Line → Comm Plan → Candidate Rate Plans → Best Plan`
- **Telegence**: `Line → Optimization Group → Candidate Rate Plans (by OG) → Best Plan`

---

## High-level flow (Telegence customer optimization)

### 1) Ingest & normalize data
- Current line inventory (BAN/account, subscriber/line identifiers, current rate plan, features/SOCs, contract/term status).
- Usage (MB/GB, SMS, voice, international/roaming if relevant) for the analysis window.
- Rate plan catalog with pricing + rules.
- Optimization Group definitions + mapping rules.

### 2) Assign an Optimization Group to each line
- Apply OG mapping (often based on current rate plan, product type, device class, account segment, network/carrier rules, etc.).
- Result: every line lands in an OG (or an “unmapped/exception” OG).

### 3) Build a candidate rate-plan set per Optimization Group
- Filter to rate plans allowed for that OG (and carrier/segment/eligibility).
- Apply compatibility constraints (required features, excluded SOCs, pooled vs non-pooled, etc.).

### 4) Cost simulation / scoring
- For each line (or pool, depending on OG rules), simulate monthly cost for each candidate plan using actual usage.
- Include recurring charges, usage-based charges/overages, and mandatory add-ons tied to the plan.

### 5) Select best option + generate recommendations
- Pick the lowest-cost eligible plan (or “best” via tie-breakers like minimizing change risk, keeping plan family, honoring contract constraints).
- Output plan-change recommendations with savings and rationale.

### 6) Exceptions + audit outputs
- Flag lines that are unmapped, missing usage, or contract-restricted with reason codes.
- Produce reviewable summaries by account, OG, and rate plan.

---

## Low-level flow (Telegence) — step-by-step mechanics

### 1) Inputs (typical logical datasets)

**Current state**
- Account/BAN hierarchy
- Line identifiers
- Current rate plan code
- Current features/SOCs
- Contract/eligibility flags

**Usage window**
- Aggregated usage by line for last _N_ cycles (often 1–3 months)
- Peak/average if used

**Catalog**
- Rate plan attributes (allowances, pricing, overage rates)
- Plan type (pooled/unpooled)
- Eligibility rules

**Optimization Group config**
- OG id/name
- OG → allowed plan list (or plan family)
- OG → constraints (min lines, pooling rules, etc.)
- Mapping from “line characteristics/current plan” → OG

### 2) Optimization Group assignment (key Telegence-specific step)

For each line:
- Determine OG via precedence rules, for example:
  - If current rate plan is in mapping table → assign that OG
  - Else if device/account segment matches → assign OG
  - Else → mark `OG_UNMAPPED` (exception)

Output (example fields):
- `line_id, current_plan, optimization_group, mapping_reason`

### 3) Candidate plan generation (OG-driven, not Comm Plan–driven)

For each line in an OG:
- Start with: **all plans allowed for the OG**
- Apply filters:
  - carrier/market eligibility
  - contract/grandfather restrictions
  - feature compatibility (required SOCs, forbidden SOCs)
  - pooling constraints (if OG is “pooled”, candidates must be pooled-compatible)

Output:
- `line_id, OG, candidate_plan_list`

### 4) Simulation (per candidate plan)

For each `line_id × candidate_plan`:
- Compute:
  - recurring monthly charge
  - included allowance (data/SMS/voice as applicable)
  - overage charges based on measured usage and plan rates
  - mandatory add-ons if the plan requires them
- Produce:
  - `estimated_monthly_cost`
  - `estimated_savings = current_estimated_cost - candidate_estimated_cost`

Output:
- a scored option matrix per line (or per pool if OG requires pooled scoring).

### 5) Selection & tie-breakers

Pick the recommended plan per line using:
- Primary: lowest estimated monthly cost
- Tie-breakers (typical):
  - prefer “closest” plan family within same OG
  - prefer fewer feature/SOC changes
  - prefer lower risk (avoid plans with special restrictions)
- Optional guardrails:
  - do not recommend if savings < `$X`
  - do not recommend if savings% < `Y%`

Output:
- `line_id, current_plan, recommended_plan, savings, reason_codes`

### 6) Exception handling (more important in Telegence)

Common exceptions:
- `OG_UNMAPPED` (no mapping match)
- no eligible candidates after constraints
- missing/insufficient usage history
- contract locked / plan not changeable
- data quality issues (duplicate line ids, mismatched catalog codes)

Output:
- exception report for manual review (with reason codes and supporting fields).

---

## Where Telegence differs from the M2M optimization you already have

### Primary grouping key
- **M2M**: Comm Plan is the top-level bucket.
- **Telegence**: Optimization Group replaces Comm Plan as the bucket.

### Mapping logic changes
- **M2M**: `line → comm plan` is often straightforward from commercial plan metadata.
- **Telegence**: you must maintain and apply an **OG mapping layer** (rate plan/device/segment → OG). This is often the biggest functional difference.

### Candidate plan filtering changes
- **M2M**: candidates are typically “plans in/for this comm plan.”
- **Telegence**: candidates are “plans allowed for this OG,” which can cut across what would have been multiple Comm Plans.

### Reporting rollups
- **M2M**: summarize savings/recommendations by Comm Plan.
- **Telegence**: summarize by Optimization Group (and then by account/BAN).

### Exception patterns
- Telegence tends to produce more unmapped/edge cases unless OG mappings are complete and kept current.

---

## Submission-ready one-liner
In **M2M**, **Comm Plan** is the organizer and optimization happens inside it; in **Telegence**, **Optimization Group (OG)** is the organizer and becomes the primary bucket for candidate selection, constraints, scoring, and rollups.

---

## Optional: tailor this to your actual OG rules
If you paste (even anonymized) one of the following:
- an OG definition snippet (OG → allowed plans/constraints), or
- a few current rate plan codes and their mapped OGs,

…I can rewrite the low-level flow using your exact OG naming and decision rules (so it matches what Lohitha will review).

