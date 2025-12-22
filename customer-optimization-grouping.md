# Customer Optimization: Grouping (Comm Plans vs Optimization Groups)

## What is the “grouping” bucket?

“Grouping” is the primary bucket used to decide which rate plans a line is allowed to consider during optimization.

- **M2M customer optimization**: the primary bucket is the **carrier/customer-defined Commercial Plan / Comm Plan context** (often represented in the data as **rate pools**, **rate plan codes**, and carrier-side eligibility). Optimization is typically run **within** that bucket and under its constraints.
- **Telegence customer optimization**: the primary bucket is the **Optimization Group (OG)**. A Comm Plan concept may not exist or may not be reliable/available, so the OG becomes the bucket used to filter candidate rate plans and apply constraints/rollups.

## Mental model

- **M2M**: Line → **Comm Plan context** (e.g., rate pool / plan code / carrier eligibility) → Candidate Rate Plans → Best Plan
- **Telegence**: Line → **Optimization Group (OG)** → Candidate Rate Plans (by OG) → Best Plan

## Answer to the doc mismatch: do we “create” Comm Plans by grouping rate plans?

**The correct model for M2M is: we start from the carrier/customer’s existing Comm Plan context and evaluate candidate rate plans inside that context.**

What can look confusing in the implementation is that the optimizer may do **internal grouping** after fetching the customer’s rate plans (for example, grouping by “auto change vs pooled”, by rate pool id, by plan code, etc.) to drive processing. That internal grouping:

- **does not mean the optimizer is inventing carrier Comm Plans**, and
- **should not be described as “fetch all rate plans then group them into Comm Plans.”**

Instead, the accurate wording is:

- **M2M**: **Fetch the customer’s rate plans already scoped to the customer’s carrier-defined buckets** (rate pools/plan codes/eligibility), then optionally **sub-group** those rate plans for algorithm execution and queueing.
- **Telegence**: **Fetch candidate rate plans by Optimization Group (OG)** and optimize within OG.

## Suggested doc wording (drop-in replacement)

If the current customer optimization flow says “fetch rate plans then group them as Comm Plans (auto change, pooling, etc.)”, replace it with:

> Fetch the customer’s eligible rate plans **within the carrier-provided Comm Plan context** (e.g., rate pool / plan code / eligibility).  
> Then, for processing, the optimizer may **sub-group** those rate plans (e.g., pooled vs auto-change) and run optimization within that bucket.

