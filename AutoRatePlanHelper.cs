using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Altaworx.SimCard.Cost.Optimizer.Core.Enumerations;
using Altaworx.SimCard.Cost.Optimizer.Core.Models;

namespace Altaworx.SimCard.Cost.Optimizer.Core.Helpers
{
    /// <summary>
    /// Auto-change rate plan strategy evaluator.
    ///
    /// Goal:
    /// - Build per-plan usage (avg usage + allocated MB)
    /// - Evaluate strategies (smallest->largest, largest->smallest, comm-scoped variants)
    /// - Compute total cost for each strategy
    /// - Return best (minimal total cost) + per-device assignment
    /// </summary>
    public static class AutoRatePlanHelper
    {
        public enum AutoAssignmentStrategy
        {
            SmallestToLargest,
            LargestToSmallest,
            CommSmallestToLargest,
            CommLargestToSmallest
        }

        public sealed class RatePlanCatalogItem
        {
            public int RatePlanId { get; init; }
            public string RatePlanCode { get; init; } // typically RatePlan.PlanName
            public string RatePlanName { get; init; } // typically RatePlan.PlanDisplayName

            public decimal AllocatedPlanMB { get; init; }

            public decimal RateCharge { get; init; } // per-device base charge
            public decimal DataPerOverageChargeMB { get; init; } // MB per overage charge unit
            public decimal OverageRate { get; init; } // $ per overage unit
        }

        public sealed class SimUsage
        {
            public vwOptimizationSimCard SimCard { get; init; }
            public string SimKey { get; init; } // for logging (id/iccid/etc)

            public string CommunicationPlan { get; init; }
            public decimal UsageMB { get; init; }

            public int? CurrentRatePlanId { get; init; }
            public decimal? CurrentAllocatedMB { get; init; }
        }

        public sealed class RatePlanUsage
        {
            public int RatePlanId { get; init; }
            public string RatePlanCode { get; init; }
            public string RatePlanName { get; init; }

            public int DeviceCount { get; init; }
            public decimal TotalUsageMB { get; init; }
            public decimal AverageUsageMB { get; init; }

            public decimal AllocatedPlanMB { get; init; }

            public bool IsOverAllocated => AverageUsageMB > AllocatedPlanMB;
        }

        public sealed class SimAssignment
        {
            public vwOptimizationSimCard SimCard { get; init; }
            public string SimKey { get; init; }
            public string CommunicationPlan { get; init; }

            public int OriginalRatePlanId { get; init; }
            public int AssignedRatePlanId { get; init; }

            public decimal UsageMB { get; init; }
            public decimal AllocatedPlanMB { get; init; }
            public decimal BaseCharge { get; init; }
            public decimal OverageCharge { get; init; }
            public decimal TotalCharge => BaseCharge + OverageCharge;
        }

        public sealed class StrategyResult
        {
            public AutoAssignmentStrategy Strategy { get; init; }
            public decimal TotalCost { get; init; }
            public IReadOnlyList<SimAssignment> Assignments { get; init; }

            public IReadOnlyList<RatePlanUsage> OriginalPlanUsages { get; init; }

            /// <summary>
            /// PlanUsage -> AssignedPlanId
            /// </summary>
            public IReadOnlyDictionary<int, int> PlanAssignmentMap { get; init; }

            /// <summary>
            /// Sum over assigned plans: min(totalUsage, includedMB).
            /// includedMB = AllocatedPlanMBPerDevice * DeviceCountAssignedToThatPlan
            /// </summary>
            public decimal UsedWithinAllocationMBTotal { get; init; }

            /// <summary>
            /// Sum over assigned plans: max(0, includedMB - totalUsage).
            /// </summary>
            public decimal UnusedMBTotal { get; init; }

            /// <summary>
            /// Sum over assigned plans: max(0, totalUsage - includedMB).
            /// </summary>
            public decimal ExcessMBTotal { get; init; }

            public IReadOnlyList<PlanTotals> PlanTotals { get; init; }
        }

        public sealed class PlanTotals
        {
            public int RatePlanId { get; init; }
            public string RatePlanCode { get; init; }
            public string RatePlanName { get; init; }

            public int DeviceCount { get; init; }
            public decimal AllocatedPlanMBPerDevice { get; init; }
            public decimal IncludedMBTotal { get; init; }

            public decimal TotalUsageMB { get; init; }
            public decimal UsedWithinAllocationMB { get; init; }
            public decimal UnusedMB { get; init; }
            public decimal ExcessMB { get; init; }
        }

        public sealed class BestStrategySelection
        {
            public StrategyResult Best { get; init; }
            public IReadOnlyList<StrategyResult> AllResults { get; init; }
        }

        // ----------------------------
        // PUBLIC ENTRYPOINTS
        // ----------------------------

        public static BestStrategySelection EvaluateBestStrategy(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            var planCatalog = BuildRatePlanCatalog(ratePlans).ToList();
            var simUsages = BuildSimUsages(simCards, planCatalog).ToList();

            // If allocation isn't available on RatePlan model, infer it from SIMs currently on that plan.
            planCatalog = HydratePlanAllocationsFromSimCards(planCatalog, simUsages);

            // STEP 1: Build usage (Avg Usage + Allocated MB)
            var planUsage = BuildRatePlanUsage(planCatalog, simUsages).ToList();

            // STEP 2: Validation (we keep invalid groups as-is; cost will include overage)
            // This is effectively handled in assignment selection by falling back to original.

            // STEP 3: Evaluate strategies
            var results = new List<StrategyResult>
            {
                EvaluateStrategy(AutoAssignmentStrategy.SmallestToLargest, planCatalog, simUsages, planUsage, chargeType),
                EvaluateStrategy(AutoAssignmentStrategy.LargestToSmallest, planCatalog, simUsages, planUsage, chargeType),
                EvaluateStrategy(AutoAssignmentStrategy.CommSmallestToLargest, planCatalog, simUsages, planUsage, chargeType),
                EvaluateStrategy(AutoAssignmentStrategy.CommLargestToSmallest, planCatalog, simUsages, planUsage, chargeType),
            };

            var best = results
                .OrderBy(r => r.TotalCost)
                .First();

            return new BestStrategySelection
            {
                Best = best,
                AllResults = results
            };
        }

        // ----------------------------
        // 4 explicit strategy functions
        // ----------------------------

        public static StrategyResult EvaluateSmallestToLargest(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            return EvaluateSingle(ratePlans, simCards, chargeType, AutoAssignmentStrategy.SmallestToLargest);
        }

        public static StrategyResult EvaluateLargestToSmallest(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            return EvaluateSingle(ratePlans, simCards, chargeType, AutoAssignmentStrategy.LargestToSmallest);
        }

        public static StrategyResult EvaluateCommSmallestToLargest(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            return EvaluateSingle(ratePlans, simCards, chargeType, AutoAssignmentStrategy.CommSmallestToLargest);
        }

        public static StrategyResult EvaluateCommLargestToSmallest(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            return EvaluateSingle(ratePlans, simCards, chargeType, AutoAssignmentStrategy.CommLargestToSmallest);
        }

        public static StrategyResult PickBestOfFour(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType)
        {
            var r1 = EvaluateSmallestToLargest(ratePlans, simCards, chargeType);
            var r2 = EvaluateLargestToSmallest(ratePlans, simCards, chargeType);
            var r3 = EvaluateCommSmallestToLargest(ratePlans, simCards, chargeType);
            var r4 = EvaluateCommLargestToSmallest(ratePlans, simCards, chargeType);

            return new[] { r1, r2, r3, r4 }.OrderBy(r => r.TotalCost).First();
        }

        private static StrategyResult EvaluateSingle(
            IEnumerable<RatePlan> ratePlans,
            IEnumerable<vwOptimizationSimCard> simCards,
            OptimizationChargeType chargeType,
            AutoAssignmentStrategy strategy)
        {
            var planCatalog = BuildRatePlanCatalog(ratePlans).ToList();
            var simUsages = BuildSimUsages(simCards, planCatalog).ToList();
            planCatalog = HydratePlanAllocationsFromSimCards(planCatalog, simUsages);
            var planUsage = BuildRatePlanUsage(planCatalog, simUsages).ToList();
            return EvaluateStrategy(strategy, planCatalog, simUsages, planUsage, chargeType);
        }

        /// <summary>
        /// Applies the assignments to the simcard objects via reflection:
        /// - CustomerRatePlanId (if present)
        /// - CustomerRatePlanMB (if present)
        /// - CustomerRatePlanCode (if present)
        /// </summary>
        public static void ApplyAssignmentsToSimCards(
            StrategyResult bestResult,
            IEnumerable<RatePlan> ratePlans)
        {
            var planNameById = ratePlans.ToDictionary(rp => rp.Id, rp => rp.PlanName);

            foreach (var assignment in bestResult.Assignments)
            {
                planNameById.TryGetValue(assignment.AssignedRatePlanId, out var planCode);

                // Most important field (if present) is the plan id.
                TrySetProperty(assignment.SimCard, "CustomerRatePlanId", assignment.AssignedRatePlanId);
                TrySetProperty(assignment.SimCard, "RatePlanId", assignment.AssignedRatePlanId);

                // Keep code in sync (usually same code for this planNameGroup, but safe).
                if (!string.IsNullOrWhiteSpace(planCode))
                {
                    TrySetProperty(assignment.SimCard, "CustomerRatePlanCode", planCode);
                }

                // Helps downstream logic and reporting.
                TrySetProperty(assignment.SimCard, "CustomerRatePlanMB", assignment.AllocatedPlanMB);
            }
        }

        // ----------------------------
        // STEP 1: BuildRatePlanUsage
        // ----------------------------

        public static IEnumerable<RatePlanUsage> BuildRatePlanUsage(
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<SimUsage> simUsages)
        {
            var planIds = planCatalog.Select(p => p.RatePlanId).Distinct().ToList();

            foreach (var planId in planIds)
            {
                var plan = planCatalog.First(p => p.RatePlanId == planId);

                var simsOnPlan = simUsages
                    .Where(s => s.CurrentRatePlanId == planId)
                    .ToList();

                var deviceCount = simsOnPlan.Count;
                var totalUsage = deviceCount > 0 ? simsOnPlan.Sum(s => s.UsageMB) : 0m;
                var avgUsage = deviceCount > 0 ? totalUsage / deviceCount : 0m;

                yield return new RatePlanUsage
                {
                    RatePlanId = plan.RatePlanId,
                    RatePlanCode = plan.RatePlanCode,
                    RatePlanName = plan.RatePlanName,
                    DeviceCount = deviceCount,
                    TotalUsageMB = totalUsage,
                    AverageUsageMB = avgUsage,
                    AllocatedPlanMB = plan.AllocatedPlanMB
                };
            }
        }

        // ----------------------------
        // STEP 3: Strategy evaluation
        // ----------------------------

        private static StrategyResult EvaluateStrategy(
            AutoAssignmentStrategy strategy,
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<SimUsage> simUsages,
            IReadOnlyList<RatePlanUsage> originalPlanUsage,
            OptimizationChargeType chargeType)
        {
            // Build a plan-usage -> target plan mapping.
            var planAssignment = strategy switch
            {
                AutoAssignmentStrategy.SmallestToLargest => BuildAssignmentMap_SmallestToLargest(planCatalog, originalPlanUsage),
                AutoAssignmentStrategy.LargestToSmallest => BuildAssignmentMap_LargestToSmallest(planCatalog, originalPlanUsage),
                AutoAssignmentStrategy.CommSmallestToLargest => BuildAssignmentMap_CommScoped(planCatalog, simUsages, originalPlanUsage, smallestToLargest: true),
                AutoAssignmentStrategy.CommLargestToSmallest => BuildAssignmentMap_CommScoped(planCatalog, simUsages, originalPlanUsage, smallestToLargest: false),
                _ => originalPlanUsage.ToDictionary(u => u.RatePlanId, u => u.RatePlanId)
            };

            // Apply mapping to each SIM.
            var planById = planCatalog.ToDictionary(p => p.RatePlanId, p => p);
            var assignments = new List<SimAssignment>(simUsages.Count);

            foreach (var sim in simUsages)
            {
                var originalPlanId = sim.CurrentRatePlanId ?? planCatalog.First().RatePlanId;
                if (!planAssignment.TryGetValue(originalPlanId, out var assignedPlanId))
                {
                    assignedPlanId = originalPlanId;
                }

                if (!planById.TryGetValue(assignedPlanId, out var assignedPlan))
                {
                    assignedPlanId = originalPlanId;
                    assignedPlan = planById[originalPlanId];
                }

                var baseCharge = chargeType == OptimizationChargeType.OverageOnly ? 0m : assignedPlan.RateCharge;
                var overageCharge = CalculateOverageCharge(sim.UsageMB, assignedPlan.AllocatedPlanMB, assignedPlan.DataPerOverageChargeMB, assignedPlan.OverageRate);

                assignments.Add(new SimAssignment
                {
                    SimCard = sim.SimCard,
                    SimKey = sim.SimKey,
                    CommunicationPlan = sim.CommunicationPlan,
                    OriginalRatePlanId = originalPlanId,
                    AssignedRatePlanId = assignedPlanId,
                    UsageMB = sim.UsageMB,
                    AllocatedPlanMB = assignedPlan.AllocatedPlanMB,
                    BaseCharge = baseCharge,
                    OverageCharge = overageCharge
                });
            }

            var planTotals = BuildPlanTotals(planCatalog, assignments);

            return new StrategyResult
            {
                Strategy = strategy,
                TotalCost = assignments.Sum(a => a.TotalCharge),
                Assignments = assignments,
                OriginalPlanUsages = originalPlanUsage,
                PlanAssignmentMap = planAssignment,
                UsedWithinAllocationMBTotal = planTotals.Sum(t => t.UsedWithinAllocationMB),
                UnusedMBTotal = planTotals.Sum(t => t.UnusedMB),
                ExcessMBTotal = planTotals.Sum(t => t.ExcessMB),
                PlanTotals = planTotals
            };
        }

        private static List<PlanTotals> BuildPlanTotals(
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<SimAssignment> assignments)
        {
            var planById = planCatalog.ToDictionary(p => p.RatePlanId, p => p);

            // Only consider plans that actually received devices (so UnusedMB reflects "unused within used plans").
            // If you want "unused plans" too, compute that outside by checking plan ids not present here.
            return assignments
                .GroupBy(a => a.AssignedRatePlanId)
                .Select(g =>
                {
                    var plan = planById[g.Key];
                    var deviceCount = g.Count();
                    var totalUsage = g.Sum(x => x.UsageMB);
                    var includedTotal = plan.AllocatedPlanMB * deviceCount;
                    var used = Math.Min(totalUsage, includedTotal);
                    var unused = Math.Max(0m, includedTotal - totalUsage);
                    var excess = Math.Max(0m, totalUsage - includedTotal);

                    return new PlanTotals
                    {
                        RatePlanId = plan.RatePlanId,
                        RatePlanCode = plan.RatePlanCode,
                        RatePlanName = plan.RatePlanName,
                        DeviceCount = deviceCount,
                        AllocatedPlanMBPerDevice = plan.AllocatedPlanMB,
                        IncludedMBTotal = includedTotal,
                        TotalUsageMB = totalUsage,
                        UsedWithinAllocationMB = used,
                        UnusedMB = unused,
                        ExcessMB = excess
                    };
                })
                .OrderBy(t => t.AllocatedPlanMBPerDevice)
                .ToList();
        }

        private static IReadOnlyDictionary<int, int> BuildAssignmentMap_SmallestToLargest(
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<RatePlanUsage> usages)
        {
            var byAllocatedAsc = planCatalog
                .OrderBy(p => p.AllocatedPlanMB)
                .ThenBy(p => p.RateCharge)
                .ToList();

            var map = new Dictionary<int, int>();

            foreach (var usage in usages)
            {
                // Validation: if current plan fits avg usage, keep it.
                if (usage.AverageUsageMB <= usage.AllocatedPlanMB)
                {
                    map[usage.RatePlanId] = usage.RatePlanId;
                    continue;
                }

                // Assign to smallest plan that can handle average usage.
                var target = byAllocatedAsc.FirstOrDefault(p => p.AllocatedPlanMB >= usage.AverageUsageMB);
                map[usage.RatePlanId] = target?.RatePlanId ?? usage.RatePlanId;
            }

            return map;
        }

        private static IReadOnlyDictionary<int, int> BuildAssignmentMap_LargestToSmallest(
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<RatePlanUsage> usages)
        {
            var byAllocatedDesc = planCatalog
                .OrderByDescending(p => p.AllocatedPlanMB)
                .ThenBy(p => p.RateCharge)
                .ToList();

            var map = new Dictionary<int, int>();

            foreach (var usage in usages)
            {
                if (usage.AverageUsageMB <= usage.AllocatedPlanMB)
                {
                    map[usage.RatePlanId] = usage.RatePlanId;
                    continue;
                }

                // Assign to the largest plan that can still handle the average usage.
                var target = byAllocatedDesc.FirstOrDefault(p => p.AllocatedPlanMB >= usage.AverageUsageMB);
                map[usage.RatePlanId] = target?.RatePlanId ?? usage.RatePlanId;
            }

            return map;
        }

        private static IReadOnlyDictionary<int, int> BuildAssignmentMap_CommScoped(
            IReadOnlyList<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<SimUsage> simUsages,
            IReadOnlyList<RatePlanUsage> usages,
            bool smallestToLargest)
        {
            // Comm-scoped variant:
            // - For each communication plan group, only allow target plans that are currently used in that comm group
            //   (prevents moving a comm group into a plan that wasn't already part of that comm group set).
            var map = new Dictionary<int, int>();

            var simsByComm = simUsages
                .GroupBy(s => s.CommunicationPlan ?? string.Empty)
                .ToList();

            foreach (var comm in simsByComm)
            {
                var usedPlanIdsInComm = comm
                    .Select(s => s.CurrentRatePlanId)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .Distinct()
                    .ToHashSet();

                // If we can't infer current plan ids for this comm group, fall back to global plan set.
                var commPlanCatalog = usedPlanIdsInComm.Count > 0
                    ? planCatalog.Where(p => usedPlanIdsInComm.Contains(p.RatePlanId)).ToList()
                    : planCatalog.ToList();

                var commUsage = usages.Where(u => usedPlanIdsInComm.Contains(u.RatePlanId)).ToList();
                if (commUsage.Count == 0)
                {
                    // If no plan usage is tied to this comm group (e.g., missing current plan ids),
                    // we apply global mapping later by leaving entries missing here.
                    continue;
                }

                var commMap = smallestToLargest
                    ? BuildAssignmentMap_SmallestToLargest(commPlanCatalog, commUsage)
                    : BuildAssignmentMap_LargestToSmallest(commPlanCatalog, commUsage);

                foreach (var kvp in commMap)
                {
                    map[kvp.Key] = kvp.Value;
                }
            }

            // For any plan not covered by a comm group map, keep original.
            foreach (var usage in usages)
            {
                if (!map.ContainsKey(usage.RatePlanId))
                {
                    map[usage.RatePlanId] = usage.RatePlanId;
                }
            }

            return map;
        }

        // ----------------------------
        // Cost helpers
        // ----------------------------

        private static decimal CalculateOverageCharge(decimal usageMb, decimal allocatedMb, decimal dataPerOverageChargeMb, decimal overageRate)
        {
            var excess = usageMb - allocatedMb;
            if (excess <= 0m)
            {
                return 0m;
            }

            if (dataPerOverageChargeMb <= 0m || overageRate <= 0m)
            {
                return 0m;
            }

            var units = (decimal)Math.Ceiling((double)(excess / dataPerOverageChargeMb));
            return units * overageRate;
        }

        // ----------------------------
        // Reflection-based readers (safe across model variations)
        // ----------------------------

        private static IEnumerable<RatePlanCatalogItem> BuildRatePlanCatalog(IEnumerable<RatePlan> ratePlans)
        {
            foreach (var rp in ratePlans)
            {
                var allocatedMb =
                    GetDecimalProperty(rp, "AllocatedPlanMB", "PlanMB", "IncludedDataMB", "IncludedMB", "DataMB", "PlanDataMB", "DataLimitMB", "IncludedData") ??
                    0m;

                // Fallback: if RatePlan doesn't expose allocation, we'll try to infer later from simcards.
                var rateCharge =
                    GetDecimalProperty(rp, "RateCharge", "MonthlyCharge", "MonthlyRate", "PlanCharge", "BaseCharge", "MRC", "Mrc", "AccessCharge") ??
                    0m;

                var dataPerOverageMb =
                    GetDecimalProperty(rp, "DataPerOverageCharge", "DataPerOverageChargeMB", "OverageIncrementMB") ??
                    0m;

                var overageRate =
                    GetDecimalProperty(rp, "OverageRate", "OverageCharge", "OverageCost") ??
                    0m;

                yield return new RatePlanCatalogItem
                {
                    RatePlanId = rp.Id,
                    RatePlanCode = GetStringProperty(rp, "PlanName", "RatePlanCode", "Code") ?? string.Empty,
                    RatePlanName = GetStringProperty(rp, "PlanDisplayName", "DisplayName", "Name") ?? string.Empty,
                    AllocatedPlanMB = allocatedMb,
                    RateCharge = rateCharge,
                    DataPerOverageChargeMB = dataPerOverageMb,
                    OverageRate = overageRate
                };
            }
        }

        private static IEnumerable<SimUsage> BuildSimUsages(
            IEnumerable<vwOptimizationSimCard> simCards,
            IReadOnlyList<RatePlanCatalogItem> planCatalog)
        {
            foreach (var sim in simCards)
            {
                var usage =
                    GetDecimalProperty(sim, "CycleDataUsageMB", "DataUsageMB", "CycleUsageMB", "UsageMB") ??
                    0m;

                var commPlan =
                    GetStringProperty(sim, "CommunicationPlan", "CommPlan", "CommunicationPlanName") ??
                    string.Empty;

                var simKey =
                    GetStringProperty(sim, "SimCardId", "Id", "ICCID", "Iccid", "Imei", "PhoneNumber", "Msisdn") ??
                    string.Empty;

                int? currentPlanId = null;
                var planIdRaw = GetIntProperty(sim, "CustomerRatePlanId", "RatePlanId", "CurrentRatePlanId");
                if (planIdRaw.HasValue && planCatalog.Any(p => p.RatePlanId == planIdRaw.Value))
                {
                    currentPlanId = planIdRaw.Value;
                }

                decimal? currentAllocatedMb = GetDecimalProperty(sim, "CustomerRatePlanMB", "RatePlanMB", "AllocatedPlanMB");

                // If we can't infer current plan id, attempt to match by allocated MB.
                if (!currentPlanId.HasValue)
                {
                    if (currentAllocatedMb.HasValue)
                    {
                        var matched = planCatalog.FirstOrDefault(p => p.AllocatedPlanMB == currentAllocatedMb.Value);
                        if (matched != null)
                        {
                            currentPlanId = matched.RatePlanId;
                        }
                    }
                }

                // Final fallback: pick the smallest plan (stable default).
                currentPlanId ??= planCatalog.OrderBy(p => p.AllocatedPlanMB).First().RatePlanId;

                // If allocation is missing on the plan model, infer it from the sim field for the plan it matched to.
                if (currentAllocatedMb.HasValue)
                {
                    var plan = planCatalog.FirstOrDefault(p => p.RatePlanId == currentPlanId.Value);
                    if (plan != null && plan.AllocatedPlanMB <= 0m && currentAllocatedMb.Value > 0m)
                    {
                        // We can't mutate planCatalog items (readonly input), but usage calc uses planCatalog allocation,
                        // so choose to keep sim-side allocation by storing on SimUsage for logging/diagnostics.
                    }
                }

                yield return new SimUsage
                {
                    SimCard = sim,
                    SimKey = simKey,
                    CommunicationPlan = commPlan,
                    UsageMB = usage,
                    CurrentRatePlanId = currentPlanId,
                    CurrentAllocatedMB = currentAllocatedMb
                };
            }
        }

        private static List<RatePlanCatalogItem> HydratePlanAllocationsFromSimCards(
            List<RatePlanCatalogItem> planCatalog,
            IReadOnlyList<SimUsage> simUsages)
        {
            var inferredByPlanId = simUsages
                .Where(s => s.CurrentRatePlanId.HasValue && s.CurrentAllocatedMB.HasValue && s.CurrentAllocatedMB.Value > 0m)
                .GroupBy(s => s.CurrentRatePlanId.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CurrentAllocatedMB!.Value).First());

            return planCatalog
                .Select(p =>
                {
                    if (p.AllocatedPlanMB > 0m) return p;
                    return inferredByPlanId.TryGetValue(p.RatePlanId, out var mb)
                        ? new RatePlanCatalogItem
                        {
                            RatePlanId = p.RatePlanId,
                            RatePlanCode = p.RatePlanCode,
                            RatePlanName = p.RatePlanName,
                            AllocatedPlanMB = mb,
                            RateCharge = p.RateCharge,
                            DataPerOverageChargeMB = p.DataPerOverageChargeMB,
                            OverageRate = p.OverageRate
                        }
                        : p;
                })
                .ToList();
        }

        private static decimal? GetDecimalProperty(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                try
                {
                    return Convert.ToDecimal(v);
                }
                catch
                {
                    // ignore and try next
                }
            }
            return null;
        }

        private static int? GetIntProperty(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                try
                {
                    return Convert.ToInt32(v);
                }
                catch
                {
                    // ignore and try next
                }
            }
            return null;
        }

        private static string GetStringProperty(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                return v.ToString();
            }
            return null;
        }

        private static bool TrySetProperty(object obj, string propertyName, object value)
        {
            if (obj == null) return false;
            var p = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null || !p.CanWrite) return false;

            try
            {
                if (value == null)
                {
                    p.SetValue(obj, null);
                    return true;
                }

                var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var coerced = Convert.ChangeType(value, targetType);
                p.SetValue(obj, coerced);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

