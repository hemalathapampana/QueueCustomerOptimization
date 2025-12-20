# Telegence vs M2M: What “Grouping” Means (Core Difference)

## Core idea
**“Grouping” is the primary bucket used to decide which rate plans a line is allowed to consider during optimization.**

- **M2M customer optimization**: lines are grouped into **Commercial Plans (Comm Plans)**. Optimization typically happens *within* a Comm Plan (or under Comm Plan–specific eligibility/constraints).
- **Telegence customer optimization**: lines are grouped into **Optimization Groups (OGs)**. A Comm Plan concept may not exist or may not be reliable/available, so the **OG becomes the primary unit of optimization** and the primary way to filter candidate rate plans.

### Mental model
- **M2M**: `Line → Comm Plan → Candidate Rate Plans → Best Plan`
- **Telegence**: `Line → Optimization Group → Candidate Rate Plans (by OG) → Best Plan`

In M2M, **Comm Plan is the organizer** and rate plans are optimized inside it.  
In Telegence, **Optimization Group is the organizer** and it acts like the Comm Plan bucket for candidate selection, constraints, scoring, and rollups.

---

## High-level flow (Telegence customer optimization)
### 1) Ingest & normalize data
- **Current line inventory**: BAN/account, subscriber/line identifiers, current rate plan, features/SOCs, contract/term status.
- **Usage**: MB/GB, SMS, voice, and optionally international/roaming for the analysis window.
- **Rate plan catalog**: pricing + rules.
- **Optimization Group definitions** + **mapping rules**.

### 2) Assign an Optimization Group (OG) to each line
- Apply OG mapping rules (often based on current rate plan, product type, device class, account segment, carrier/network rules, etc.).
- Ensure every line lands in an OG, otherwise tag as **unmapped/exception**.

### 3) Build candidate rate-plan set per Optimization Group
- Filter to rate plans **allowed for the OG** (and carrier/segment/eligibility).
- Apply compatibility constraints (required features/SOCs, excluded SOCs, pooled vs non-pooled rules, etc.).

### 4) Cost simulation / scoring
- For each line (or pool, depending on OG rules), simulate monthly cost on each candidate rate plan using actual usage.
- Include recurring charges, usage-based charges/overages, and any mandatory add-ons tied to the plan.

### 5) Select best option + generate recommendations
- Pick the lowest-cost eligible plan (or “best” using tie-breakers like minimizing change risk, staying in plan family, contract constraints).
- Output recommended changes with savings + rationale.

### 6) Exceptions + audit outputs
- Flag lines that can’t be mapped, have missing usage, or are contract-restricted (with reasons).
- Produce rollups by account/BAN, OG, and rate plan.

---

## Low-level flow (Telegence) — step-by-step mechanics
### 1) Inputs (typical logical datasets)
**Current state**
- Account/BAN hierarchy, line identifiers, current rate plan code
- Current features/SOCs
- Contract / eligibility flags

**Usage window**
- Aggregated usage by line for last **N** cycles (often 1–3 months)
- Optional peak/average metrics if your model uses them

**Catalog**
- Rate plan attributes: included allowances, pricing, overage rates
- Plan type: pooled vs unpooled
- Eligibility rules

**Optimization Group config**
- `og_id`, `og_name`
- OG → allowed plan list (or plan family)
- OG → constraints (min lines, pooling rules, etc.)
- Mapping rules: “line characteristics / current plan” → OG

### 2) Optimization Group assignment (key Telegence-specific step)
For each line, determine OG via precedence rules (example pattern):
1. If current rate plan is in OG mapping table → assign that OG
2. Else if device / account segment matches → assign OG
3. Else → mark `OG_UNMAPPED` (exception)

Output fields (example):
- `line_id`, `current_plan`, `optimization_group`, `mapping_reason`

### 3) Candidate plan generation (OG-driven, not Comm Plan–driven)
For each line in an OG:
- Start candidates = **all plans allowed for that OG**
- Apply filters:
  - Carrier/market eligibility
  - Contract/grandfather restrictions
  - Feature compatibility (required SOCs, forbidden SOCs)
  - Pooling constraints (if OG is pooled, candidates must be pooled-compatible)

Output fields (example):
- `line_id`, `og_id`, `candidate_plan_list`

### 4) Simulation (per candidate plan)
For each `(line_id × candidate_plan)`:
- Compute:
  - Recurring monthly charge
  - Included allowance (data/SMS/voice as applicable)
  - Overage charges based on measured usage and plan rates
  - Mandatory add-ons required by the plan
- Produce:
  - `estimated_monthly_cost`
  - `estimated_savings = current_estimated_cost - candidate_estimated_cost`

Output: scored options per line (matrix of candidate plans).

### 5) Selection & tie-breakers
Select the recommendation per line using:
- **Primary**: lowest estimated monthly cost
- **Tie-breakers (typical)**:
  - Prefer “closest” plan family within same OG
  - Prefer fewer feature changes
  - Prefer lower risk (avoid plans with special restrictions)
- Optional guardrails:
  - Don’t recommend if savings < `$X`
  - Don’t recommend if savings% < `Y%`

Output fields (example):
- `line_id`, `current_plan`, `recommended_plan`, `savings`, `reason_codes`

### 6) Exception handling (more important in Telegence)
Common exception buckets:
- Unmapped OG (`OG_UNMAPPED`)
- No eligible candidates after constraints
- Missing/insufficient usage history
- Contract locked / plan not changeable
- Data quality issues (duplicate line IDs, mismatched catalog codes)

Output: exception report for manual review.

---

## Where Telegence differs from the M2M optimization you already have
### 1) Primary grouping key
- **M2M**: Comm Plan is the top-level bucket.
- **Telegence**: Optimization Group replaces Comm Plan as the bucket.

### 2) Mapping logic changes
- **M2M**: `line → comm plan` is often straightforward from commercial plan metadata.
- **Telegence**: you must maintain and apply an **OG mapping layer** (rate plan / device / segment → OG). This is often the biggest functional difference.

### 3) Candidate plan filtering changes
- **M2M**: candidates are typically “plans in/for this comm plan.”
- **Telegence**: candidates are “plans allowed for this OG,” which can cut across what would have been multiple comm plans.

### 4) Reporting rollups
- **M2M**: summarize savings and recommendations by Comm Plan.
- **Telegence**: summarize by Optimization Group (then by account/BAN).

### 5) Exception patterns
- Telegence tends to produce more unmapped/edge cases unless OG mappings are complete and kept current.

---

## Quick phrasing for a submission
**M2M uses Comm Plans as the organizing bucket for optimization; Telegence uses Optimization Groups instead.** Because Comm Plan metadata may be missing or unreliable in Telegence, the Optimization Group becomes the controlling unit for candidate plan eligibility, constraints, scoring, and reporting.

