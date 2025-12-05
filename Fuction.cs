using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Altaworx.SimCard.Cost.Optimizer.Core;
using Altaworx.SimCard.Cost.Optimizer.Core.Enumerations;
using Altaworx.SimCard.Cost.Optimizer.Core.Factories;
using Altaworx.SimCard.Cost.Optimizer.Core.Helpers;
using Altaworx.SimCard.Cost.Optimizer.Core.Models;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using System.Collections.Generic;
using Amop.Core.Models;
using Altaworx.AWS.Core.Helpers.Constants;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using Amop.Core.Logger;
using System.Net.Http;
using System.Text;
using Amop.Core.Helpers.Pond;
using Amop.Core.Repositories.Environment;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace Altaworx.SimCard.Cost.QueueCustomerOptimization
{
    public class Function : AwsFunctionBase
    {
        private readonly int DEFAULT_QUEUES_PER_INSTANCE = 5;
        private bool IsUsingRedisCache = false;
        private int QueuesPerInstance = Convert.ToInt32(Environment.GetEnvironmentVariable("QueuesPerInstance"));
        private string ErrorNotificationEmailReceiver = Environment.GetEnvironmentVariable("ErrorNotificationEmailReceiver");

        // Defaulted to M2M portal type. This lambda also support Cross-Provider customer optimization
        public Function() : base(PortalTypes.M2M)
        {
        }

        /// <summary>
        /// M2M Customer Optimization
        /// </summary>
        /// <param name="sqsEvent"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);
                InitializeRepositories(context, keysysContext);

                if (QueuesPerInstance == 0)
                {
                    QueuesPerInstance = DEFAULT_QUEUES_PER_INSTANCE;
                    ErrorNotificationEmailReceiver = context.ClientContext.Environment["ErrorNotificationEmailReceiver"];
                }
                //if a redis cache connection string is set but not reachable => considered as an error & continue without cache
                IsUsingRedisCache = keysysContext.TestRedisConnection();

                await ProcessEvent(keysysContext, sqsEvent);
            }
            catch (Exception ex)
            {
                LogInfo(keysysContext, "EXCEPTION", ex.Message);
            }

            CleanUp(keysysContext);
        }

        private async Task ProcessEvent(KeySysLambdaContext context, SQSEvent sqsEvent)
        {
            LogInfo(context, "SUB", "ProcessEvent");
            if (sqsEvent.Records.Count > 0)
            {
                if (sqsEvent.Records.Count == 1)
                {
                    await ProcessEventRecord(context, sqsEvent.Records[0]);
                }
                else
                {
                    LogInfo(context, "EXCEPTION", $"Expected a single message, received {sqsEvent.Records.Count}");
                }
            }
        }

        private async Task ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
        {
            LogInfo(context, "SUB", "ProcessEventRecord");
            if (!message.MessageAttributes.ContainsKey("CustomerType"))
            {
                LogInfo(context, "EXCEPTION", "No Customer Type provided in message");
                return;
            }
            var isLastInstance = false;
            if (message.MessageAttributes.ContainsKey("IsLastInstance"))
            {
                isLastInstance = Convert.ToBoolean(message.MessageAttributes["IsLastInstance"].StringValue);
            }

            int tenantId = int.Parse(message.MessageAttributes["TenantId"].StringValue);
            SiteTypes customerType = (SiteTypes)int.Parse(message.MessageAttributes["CustomerType"].StringValue);
            var messageId = message.MessageId;
            var optimizationSessionId = long.Parse(message.MessageAttributes["OptimizationSessionId"].StringValue);
            var additionalDataObject = new
            {
                data = new
                {
                    BillPeriodId = "",
                    SiteId = 0,
                    ServiceProviderId = "",
                    OptimizationType = 0,
                    OptimizationFrom = "group",
                    BillingPeriodStartDate = "",
                    BillingPeriodEndDate = "",
                    DeviceCount = 0,
                    TenantId = tenantId,
                }
            };
            string additionalData = Newtonsoft.Json.JsonConvert.SerializeObject(additionalDataObject);
            // AWXPORT-1210 - Proration only applies to carrier optimization
            bool usesProration = false;

            PortalTypes portalType = PortalTypes.M2M;
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.PORTAL_TYPE_ID))
            {
                portalType = (PortalTypes)Convert.ToInt32(message.MessageAttributes[SQSMessageKeyConstant.PORTAL_TYPE_ID].StringValue);
            }
            else
            {
                LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.SQS_MESSAGE_ATTRIBUTE_NOT_FOUND, SQSMessageKeyConstant.PORTAL_TYPE_ID) + string.Format(LogCommonStrings.DEFAULTING_SQS_MESSAGE_VALUE_MESSAGE, PortalTypes.M2M.ToString()));
            }
            if (portalType == PortalTypes.M2M)
            {
                await ProcessCustomerOptimizationByPortalType(context, message, isLastInstance, tenantId, customerType, messageId, optimizationSessionId, usesProration, additionalData);
            }
            else
            {
                // Run Cross-Provider Customer Optimization
                await ProcessCrossProviderCustomerOptimization(context, message, isLastInstance, tenantId, customerType, optimizationSessionId, additionalData);
            }
        }

        private async Task ProcessCustomerOptimizationByPortalType(KeySysLambdaContext context, SQSEvent.SQSMessage message, bool isLastInstance, int tenantId, SiteTypes customerType, string messageId, long optimizationSessionId, bool usesProration, string additionalData)
        {

            if (!message.MessageAttributes.ContainsKey("CustomerId") && !message.MessageAttributes.ContainsKey("AMOPCustomerId"))
            {
                LogInfo(context, "EXCEPTION", "No Customer Id provided in message");
                return;
            }
            if (!message.MessageAttributes.ContainsKey("BillPeriodId") && !(message.MessageAttributes.ContainsKey("BillYear") && message.MessageAttributes.ContainsKey("BillMonth")))
            {
                LogInfo(context, "EXCEPTION", "No Billing Period provided in message");
                return;
            }
            Guid customerId = Guid.Empty;
            if (message.MessageAttributes.ContainsKey("CustomerId"))
            {
                customerId = Guid.Parse(message.MessageAttributes["CustomerId"].StringValue);
            }
            if (customerType == SiteTypes.Rev && (string.IsNullOrEmpty(customerId.ToString()) || customerId == Guid.Empty))
            {
                LogInfo(context, "EXCEPTION", "Blank Customer Id provided in message");
                return;
            }

            int? amopCustomerId = null;
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.AMOP_CUSTOMER_ID))
            {
                amopCustomerId = int.Parse(message.MessageAttributes[SQSMessageKeyConstant.AMOP_CUSTOMER_ID].StringValue);
            }

            int? billingPeriodId = null;
            if (message.MessageAttributes.ContainsKey("BillPeriodId"))
            {
                if (!int.TryParse(message.MessageAttributes["BillPeriodId"].StringValue, out var sqsBillingPeriodId))
                {
                    LogInfo(context, "EXCEPTION", "Invalid Billing Period provided in message");
                    return;
                }

                billingPeriodId = sqsBillingPeriodId;
            }
            var serviceProviderId = GetServiceProviderId(message) ?? GetServiceProviderIdFromBillingPeriod(context, billingPeriodId);
            try
            {
                if (customerType == SiteTypes.Rev)
                {
                    var integrationAuthenticationId = int.Parse(message.MessageAttributes["IntegrationAuthenticationId"].StringValue);
                    await ProcessCustomerId(context, tenantId, customerId, serviceProviderId, billingPeriodId, messageId, integrationAuthenticationId, optimizationSessionId, usesProration, isLastInstance, customerType, additionalData);
                }
                else
                {
                    ArgumentNullException.ThrowIfNull(amopCustomerId);
                    await ProcessAMOPCustomerId(context, tenantId, customerType, amopCustomerId.Value, serviceProviderId, billingPeriodId, messageId, optimizationSessionId, usesProration, isLastInstance, additionalData);
                }
            }
            finally
            {
                optimizationRepository.MarkProcessedOptimizationInstanceTrackingRecord(context, optimizationSessionId, customerId, amopCustomerId);
            }
        }

        private async Task ProcessCrossProviderCustomerOptimization(KeySysLambdaContext context, SQSEvent.SQSMessage message, bool isLastInstance, int tenantId, SiteTypes customerType, long optimizationSessionId, string additionalData)
        {
            LogInfo(context, CommonConstants.SUB, $"({message.MessageId},{tenantId},{customerType})");

            SetPortalType(PortalTypes.CrossProvider);

            var customerIdentifier = 0;
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.AMOP_CUSTOMER_ID))
            {
                customerIdentifier = int.Parse(message.MessageAttributes[SQSMessageKeyConstant.AMOP_CUSTOMER_ID].StringValue);
            }
            else
            {
                LogInfo(context, CommonConstants.ERROR, $"No Customer Id found. Stopping Cross-Provider Customer Optimization.");
            }
            LogVariableValue(context, nameof(customerIdentifier), customerIdentifier);
            // Get Service Provider Ids
            var serviceProviderIds = string.Empty;
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.SERVICE_PROVIDER_IDS))
            {
                serviceProviderIds = message.MessageAttributes[SQSMessageKeyConstant.SERVICE_PROVIDER_IDS].StringValue;
            }
            else
            {
                LogInfo(context, CommonConstants.INFO, $"No service provider specified. Running Cross-Provider Customer Optimization for all service provider");
            }
            LogVariableValue(context, nameof(serviceProviderIds), serviceProviderIds);

            // Get billing cycle end date
            int customerBillingPeriodId = 0;
            if (!message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.CUSTOMER_BILLING_PERIOD_ID)
                || !int.TryParse(message.MessageAttributes[SQSMessageKeyConstant.CUSTOMER_BILLING_PERIOD_ID].StringValue, out customerBillingPeriodId))
            {
                LogInfo(context, CommonConstants.ERROR, $"No customer billing period id found");
            }
            LogVariableValue(context, nameof(customerBillingPeriodId), customerBillingPeriodId);
            try
            {
                await RunCrossProviderCustomerOptimization(context, tenantId, customerIdentifier, customerType, serviceProviderIds, customerBillingPeriodId, message.MessageId, optimizationSessionId, isLastInstance, additionalData);
            }
            finally
            {
                optimizationRepository.MarkProcessedOptimizationInstanceTrackingRecord(context, optimizationSessionId, revCustomerId: null, customerIdentifier);
            }
        }

        private int? GetServiceProviderIdFromBillingPeriod(KeySysLambdaContext context, int? billingPeriodId)
        {
            int? serviceProviderId = null;
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("SELECT ServiceProviderId FROM BillingPeriod bp WHERE bp.id = @billingPeriodId", conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@billingPeriodId", billingPeriodId);
                    conn.Open();

                    SqlDataReader rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        serviceProviderId = int.Parse(rdr[0].ToString());
                    }

                    conn.Close();
                }
            }

            return serviceProviderId;
        }

        private async Task ProcessCustomerId(KeySysLambdaContext context, int tenantId, Guid customerId,
            int? serviceProviderId, int? billingPeriodId, string messageId, int integrationAuthenticationId,
            long optimizationSessionId, bool usesProration, bool isLastInstance, SiteTypes customerType, string additionalData)
        {
            LogInfo(context, "SUB", $"ProcessCustomerId({tenantId},{customerId},{serviceProviderId},{billingPeriodId},{messageId},{integrationAuthenticationId})");

            // get customer account number
            var revAccountNumber = GetRevAccountNumber(context, customerId);

            // get customer rate plans
            var ratePlans = GetCustomerRatePlans(context, customerId, (int)billingPeriodId, serviceProviderId, tenantId);

            var useBillInAdvance = ratePlans.Count(x => x.IsBillInAdvanceEligible) > 0;
            //Disable bill in advance logic until new logic is defined (PORT-166)
            useBillInAdvance = false;

            LogInfo(context, "INFO", $"Use Bill In Advance: {useBillInAdvance}");

            // start instance
            if (billingPeriodId.HasValue)
            {
                var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
                BillingPeriod nextBillingPeriod = null;
                if (billingPeriod != null)
                {
                    nextBillingPeriod = GetNextBillingPeriod(context, billingPeriod.ServiceProviderId, billingPeriod.BillingPeriodEnd);
                }

                var billInAdvanceBillingPeriodId = nextBillingPeriod?.Id;

                LogInfo(context, "INFO", $"Bill In Advance Billing Period Id: {billInAdvanceBillingPeriodId}");

                if (useBillInAdvance && (billInAdvanceBillingPeriodId == null || billingPeriod == null))
                {
                    LogInfo(context, "ERROR", $"A Billing Period past Billing Period Id = {billingPeriodId.Value} could not be found for this Customer. So, billing in advance is not possible at this time. Optimization not run.");
                    return;
                }

                if (ratePlans.Count > 0)
                {
                    LogInfo(context, "INFO", $"Service Provider: {billingPeriod.ServiceProviderId}, Bill Period: {billingPeriod.BillingPeriodStart} - {billingPeriod.BillingPeriodEnd}");

                    var instanceId = StartOptimizationInstanceWithBillingPeriod(context, tenantId, messageId,
                        billingPeriod.Id, customerId, integrationAuthenticationId, PortalTypes.M2M, optimizationSessionId,
                        useBillInAdvance, billInAdvanceBillingPeriodId);

                    // what kind of charge type should we use?
                    var chargeType = OptimizationChargeType.RateChargeAndOverage;
                    if (useBillInAdvance)
                    {
                        chargeType = OptimizationChargeType.OverageOnly;
                    }

                    LogInfo(context, "INFO", $"Charge Type: {chargeType}");

                    // check cache and send email if redis cache is unreachable but is a valid connection string 
                    if (context.IsRedisConnectionStringValid && !IsUsingRedisCache)
                    {
                        await LogAndSendConfigurationIssueEmailAsync(context, ErrorNotificationEmailReceiver, optimizationSessionId, instanceId);
                    }

                    var isError = await ProcessDevicesByCustomerRatePlans(context, integrationAuthenticationId, usesProration, revAccountNumber, null, ratePlans, billingPeriod, nextBillingPeriod, instanceId, chargeType, customerType, tenantId);

                    if (!isError)
                    {
                        // Enqueue cleanup method
                        // Set to 15 seconds delay to match mobility customer optimization. This helps runs with small number of SIMs complete faster
                        EnqueueCleanup(context, instanceId, deliveryDelay: CommonConstants.DELAY_IN_SECONDS_FIFTEEN_SECONDS, serviceProviderId: (int)serviceProviderId, isCustomerOptimization: true, isLastInstance: isLastInstance);
                    }
                    else
                    {
                        var errorMessage = "There is an error in Processing Customer Rate Plans";
                        UpdateCustomerOptimization(context, optimizationSessionId, errorMessage, serviceProviderId.Value, revAccountNumber);
                        StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                        //triggger AMOP2.0 to send error message
                        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, revAccountNumber, additionalData);
                    }

                    // (Optional) Calculate Bill in Advance Charges
                    if (useBillInAdvance)
                    {
                        // Original logic here was using Mobility customer Optimization Bill in advance logic, but that does not take into account the changing rate plans logic of M2M Customer Optimization so we need to reimplement(PORT-655)
                        LogInfo(context, LogTypeConstant.Info, "Bill In Advance calculation logic is not implemented for Optimization with Auto Change Rate Plan enabled.");
                    }

                    // Record charges for devices with no rate plans
                    ProcessNoRatePlanDevices(context, serviceProviderId, billingPeriodId, integrationAuthenticationId, usesProration, revAccountNumber, billingPeriod, instanceId, customerType, null, tenantId);
                }
                else
                {
                    var minStart = billingPeriod.BillingPeriodStart;
                    var maxEnd = billingPeriod.BillingPeriodEnd;
                    long instanceId;
                    if (billingPeriod != null)
                    {
                        instanceId = StartOptimizationInstanceWithBillingPeriod(context, tenantId, messageId,
                            billingPeriod.Id, customerId, integrationAuthenticationId, PortalTypes.M2M,
                            optimizationSessionId, useBillInAdvance, billInAdvanceBillingPeriodId);
                    }
                    else
                    {
                        instanceId = StartOptimizationInstance(context, tenantId, billingPeriod.ServiceProviderId,
                            customerId, messageId, integrationAuthenticationId, minStart, maxEnd, PortalTypes.M2M,
                            optimizationSessionId, billingPeriodId.Value, useBillInAdvance, billInAdvanceBillingPeriodId);
                    }

                    var errorMessage = "No Comm Groups and/or Rate Plans for this Instance";
                    LogInfo(context, "ERROR", errorMessage);
                    UpdateCustomerOptimization(context, optimizationSessionId, errorMessage, serviceProviderId.Value, revAccountNumber);
                    StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                    //triggger AMOP2.0 to send error message
                    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, revAccountNumber, additionalData);
                }
            }
            else
            {
                LogInfo(context, "ERROR", "No Billing Periods for this Customer");
            }
        }


        private async Task ProcessAMOPCustomerId(KeySysLambdaContext context, int tenantId, SiteTypes customerType, int AMOPCustomerId,
            int? serviceProviderId, int? billingPeriodId, string messageId,
            long optimizationSessionId, bool usesProration, bool isLastInstance, string additionalData)
        {
            LogInfo(context, "SUB", $"ProcessAMOPCustomerId({tenantId},{AMOPCustomerId},{serviceProviderId},{billingPeriodId},{messageId})");

            // get customer rate plans
            var ratePlans = GetCustomerRatePlans(context, Guid.Empty, (int)billingPeriodId, serviceProviderId, tenantId, customerType, AMOPCustomerId);
            var useBillInAdvance = ratePlans.Count(x => x.IsBillInAdvanceEligible) > 0;

            LogInfo(context, "INFO", $"Use Bill In Advance: {useBillInAdvance}");

            // start instance
            if (billingPeriodId.HasValue)
            {
                var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
                BillingPeriod nextBillingPeriod = null;
                if (billingPeriod != null)
                {
                    nextBillingPeriod = GetNextBillingPeriod(context, billingPeriod.ServiceProviderId, billingPeriod.BillingPeriodEnd);
                }

                var billInAdvanceBillingPeriodId = nextBillingPeriod?.Id;

                LogInfo(context, "INFO", $"Bill In Advance Billing Period Id: {billInAdvanceBillingPeriodId}");

                if (useBillInAdvance && (billInAdvanceBillingPeriodId == null || billingPeriod == null))
                {
                    LogInfo(context, "ERROR", $"A Billing Period past Billing Period Id = {billingPeriodId.Value} could not be found for this Customer. So, billing in advance is not possible at this time. Optimization not run.");
                    return;
                }

                if (ratePlans.Count > 0)
                {
                    LogInfo(context, "INFO", $"Service Provider: {billingPeriod.ServiceProviderId}, Bill Period: {billingPeriod.BillingPeriodStart} - {billingPeriod.BillingPeriodEnd}");

                    var instanceId = StartOptimizationInstanceWithBillingPeriod(context, tenantId, messageId,
                        billingPeriod.Id, null, null, PortalTypes.M2M, optimizationSessionId,
                        useBillInAdvance, billInAdvanceBillingPeriodId, AMOPCustomerId);

                    // what kind of charge type should we use?
                    var chargeType = OptimizationChargeType.RateChargeAndOverage;
                    if (useBillInAdvance)
                    {
                        chargeType = OptimizationChargeType.OverageOnly;
                    }

                    LogInfo(context, "INFO", $"Charge Type: {chargeType}");

                    // check cache and send email if redis cache is unreachable but is a valid connection string 
                    if (context.IsRedisConnectionStringValid && !IsUsingRedisCache)
                    {
                        await LogAndSendConfigurationIssueEmailAsync(context, ErrorNotificationEmailReceiver, optimizationSessionId, instanceId);
                    }

                    var isError = await ProcessDevicesByCustomerRatePlans(context, null, usesProration, null, AMOPCustomerId, ratePlans, billingPeriod, nextBillingPeriod, instanceId, chargeType, customerType, tenantId);


                    if (!isError)
                    {
                        // enqueue cleanup method
                        EnqueueCleanup(context, instanceId, serviceProviderId: (int)serviceProviderId, isCustomerOptimization: true, isLastInstance: isLastInstance);
                    }
                    else
                    {
                        var errorMessage = "There is an error in Processing Customer Rate Plans";
                        UpdateCustomerOptimization(context, optimizationSessionId, errorMessage, serviceProviderId.Value, "", AMOPCustomerId);
                        StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                        //triggger AMOP2.0 to send error message
                        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, AMOPCustomerId.ToString(), additionalData);
                    }

                    // (Optional) Calculate Bill in Advance Charges
                    if (useBillInAdvance)
                    {
                        LogInfo(context, LogTypeConstant.Info, "Bill In Advance calculation logic is not implemented for Optimization with Auto Change Rate Plan enabled.");
                    }

                    // Record charges for devices with no rate plans
                    ProcessNoRatePlanDevices(context, serviceProviderId, billingPeriodId, null, usesProration, null, billingPeriod, instanceId, customerType, AMOPCustomerId, tenantId);
                }
                else
                {
                    var minStart = billingPeriod.BillingPeriodStart;
                    var maxEnd = billingPeriod.BillingPeriodEnd;
                    long instanceId;
                    if (billingPeriod != null)
                    {
                        instanceId = StartOptimizationInstanceWithBillingPeriod(context, tenantId, messageId,
                            billingPeriod.Id, Guid.Empty, null, PortalTypes.M2M,
                            optimizationSessionId, useBillInAdvance, billInAdvanceBillingPeriodId, AMOPCustomerId);
                    }
                    else
                    {
                        instanceId = StartOptimizationInstance(context, tenantId, billingPeriod.ServiceProviderId,
                            null, messageId, null, minStart, maxEnd, PortalTypes.M2M,
                            optimizationSessionId, billingPeriodId.Value, useBillInAdvance, billInAdvanceBillingPeriodId, AMOPCustomerId);
                    }

                    var errorMessage = "No Comm Groups and/or Rate Plans for this Instance";
                    LogInfo(context, "ERROR", errorMessage);
                    UpdateCustomerOptimization(context, optimizationSessionId, errorMessage, serviceProviderId.Value, "", AMOPCustomerId);
                    StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                    //triggger AMOP2.0 to send error message
                    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, AMOPCustomerId.ToString(), additionalData);
                }
            }
            else
            {
                LogInfo(context, "ERROR", "No Billing Periods for this Customer");
            }
        }

        private async Task<bool> ProcessDevicesByCustomerRatePlans(KeySysLambdaContext context, int? integrationAuthenticationId, bool usesProration, string revAccountNumber, int? AMOPCustomerId, List<RatePlan> ratePlans, BillingPeriod billingPeriod, BillingPeriod nextBillingPeriod, long instanceId, OptimizationChargeType chargeType, SiteTypes customerType, int tenantId)
        {
            var optimizationSimCards = GetOptimizationSimCards(context, null, billingPeriod.ServiceProviderId, revAccountNumber, integrationAuthenticationId, billingPeriod.Id, tenantId, customerType, AMOPCustomerId);
            if (revAccountNumber != null || AMOPCustomerId != null)
            {
                optimizationSimCards = optimizationSimCards.Where(s => !string.IsNullOrWhiteSpace(s.CustomerRatePlanCode)).ToList();
            }

            // process Pooled by Customer Rate Pool
            var ratePlansByCustomerRatePool = ratePlans.Where(ratePlan => !ratePlan.AutoChangeRatePlan).ToList();
            if (ratePlansByCustomerRatePool.Any())
            {
                if (CheckZeroValueRatePlans(context, instanceId, ratePlansByCustomerRatePool, shouldStopInstance: true))
                {
                    return true;
                }
                else
                {
                    // process and return the remaining devices for optimization with algorithm
                    optimizationSimCards = ProcessDevicesWithAutoChangeDisabledRatePlans(context, integrationAuthenticationId, usesProration, revAccountNumber, AMOPCustomerId, billingPeriod, nextBillingPeriod, instanceId, optimizationSimCards, ratePlansByCustomerRatePool, tenantId);
                }
            }

            var simCardsByRatePoolIds = optimizationSimCards.GroupBy(x => x.CustomerRatePoolId).Distinct();

            foreach (var simCardsByRatePoolId in simCardsByRatePoolIds)
            {
                LogInfo(context, CommonConstants.INFO, $"RatePoolId: {simCardsByRatePoolId}");
                // Get all rate plan codes from the devices
                var ratePlanCodes = simCardsByRatePoolId.Select(x => x.CustomerRatePlanCode).Distinct();
                var isError = false;
                if (simCardsByRatePoolId.Key != null)
                {
                    // Get all rate plans with matching rate plan codes
                    var ratePlansForPool = ratePlans.Where(x => ratePlanCodes.Contains(x.PlanName));
                    isError = await ProcessRatePoolGroup(context, integrationAuthenticationId, usesProration, revAccountNumber, AMOPCustomerId, billingPeriod, instanceId, chargeType, ratePlansForPool, simCardsByRatePoolId.ToList(), simCardsByRatePoolId?.Key, queuesPerInstance: QueuesPerInstance);
                }
                else
                {
                    // Group rate plans by rate plan code and run auto change optimization logic for this group of devices
                    var ratePlansByCodes = ratePlans.Where(ratePlan => ratePlan.AutoChangeRatePlan && ratePlanCodes.Contains(ratePlan.PlanName)).GroupBy(x => x.PlanName);
                    foreach (var ratePlansByCode in ratePlansByCodes)
                    {
                        isError = await ProcessPlanNameGroup(context, integrationAuthenticationId, usesProration, revAccountNumber, AMOPCustomerId, billingPeriod, instanceId, chargeType, ratePlansByCode, simCardsByRatePoolId.ToList());
                    }
                }

                // If error, stop optimization midway
                if (isError)
                {
                    return isError;
                }
            }
            return false;
        }

        private async Task<bool> ProcessPlanNameGroup(KeySysLambdaContext context, int? integrationAuthenticationId, bool usesProration, string revAccountNumber, int? AMOPCustomerId, BillingPeriod billingPeriod, long instanceId, OptimizationChargeType chargeType, IGrouping<string, RatePlan> planNameGroup, List<vwOptimizationSimCard> optimizationSimCards)
        {
            foreach (var ratePlanGroup in planNameGroup.GroupBy(x => x.AllowsSimPooling))
            {
                LogInfo(context, LogTypeConstant.Info, $"Allows SIM Pooling: {ratePlanGroup.Key}");

                // get rate plans for group
                var groupRatePlans = ratePlanGroup.ToList();
                var zeroValueRatePlans = groupRatePlans.FindAll(x => x.DataPerOverageCharge == 0.0M || x.OverageRate == 0.0M);
                if (zeroValueRatePlans.Count > 0)
                {
                    LogInfo(context, LogTypeConstant.Exception, $"The following rate plans in '{planNameGroup.Key}' has Data per Overage Charge or Overage Rate of 0. Please update to a non-zero value.{Environment.NewLine} {string.Join(',', zeroValueRatePlans.Select(ratePlan => ratePlan.PlanDisplayName))}");
                    return true;
                }

                // filter rate plans that are used for auto change rate plan
                if (optimizationSimCards.Count == 0)
                {
                    // No more devices to process the next steps for this rate plan group
                    // if there are devices but no rate plans, the devices could be unassigned devices so it is expected
                    LogInfo(context, LogTypeConstant.Info, $"No more device to optimize for rate plan in group with rate plan code '{planNameGroup.Key}', AllowsSimPooling: {ratePlanGroup.Key}.");
                    continue;
                }
                // create new comm plan group
                var commPlanGroupId = CreateCommPlanGroup(context, instanceId);
                var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
                var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, chargeType);
                var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools);

                var baseAssignedSimCardsCount = BaseDeviceAssignment(context, instanceId, commPlanGroupId, billingPeriod.ServiceProviderId,
                    revAccountNumber, integrationAuthenticationId, null, ratePoolCollection, ratePools, optimizationSimCards, billingPeriod, usesProration, AMOPCustomerId);
                // add rate plans to comm plan group
                var commGroupRatePlanTable = AddCustomerRatePlansToCommPlanGroup(context, instanceId, commPlanGroupId, calculatedPlans);

                // zero sim card => no need to run optimizer
                // one sim card => swapping between rate plans would be the same as base device assignment
                //              => already calculate that => no need to run optimizer
                if (baseAssignedSimCardsCount > OptimizationConstant.BaseAssignedDeviceLimit)
                {
                    // permute rate plans
                    if (calculatedPlans.Count > OptimizationConstant.RatePlanLimit)
                    {
                        LogInfo(context, LogTypeConstant.Exception, $"The rate plan count exceeds the limit of 15 for this Rate Plan Code {ratePlanGroup.Key}. Please cut down the options to 15 or less for this Rate Plan Code.");
                        continue;
                    }
                    GeneratePermutationQueueRatePlans(context, usesProration, billingPeriod, instanceId, commPlanGroupId, ratePoolCollection, commGroupRatePlanTable);

                    // enqueue rate plan permutations
                    await EnqueueOptimizationRunsAsync(context, instanceId, new List<long>() { commPlanGroupId }, chargeType, QueuesPerInstance, skipLowerCostCheck: true, isCustomerOptimization: true);
                }
                else
                {
                    LogInfo(context, LogTypeConstant.Info, $"Plan name group for the rate plans {string.Join(',', ratePlanGroup.Select(plan => plan.Id).ToList())} only have {baseAssignedSimCardsCount} devices. The optimization by permutation logic will not be triggered.");
                }
            }
            return false;
        }

        private void GeneratePermutationQueueRatePlans(KeySysLambdaContext context, bool usesProration, BillingPeriod billingPeriod, long instanceId, long commPlanGroupId, RatePoolCollection ratePoolCollection, DataTable commGroupRatePlanTable)
        {
            LogInfo(context, LogTypeConstant.Sub, detail: $"Start GenerateRatePoolSequences for {ratePoolCollection.RatePools.Count} Rate Plans");
            var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);
            var permutationCount = ratePoolSequences?.Count() ?? 0;
            LogInfo(context, LogTypeConstant.Info, $"Generated {permutationCount} rate plan permutations for CommPlanGroupId {commPlanGroupId}.");
            LogInfo(context, LogTypeConstant.Sub, "End GenerateRatePoolSequences");

            var dtQueueRatePlan = new DataTable();
            dtQueueRatePlan.Columns.Add("QueueId", typeof(long));
            dtQueueRatePlan.Columns.Add("CommGroup_RatePlanId", typeof(long));
            dtQueueRatePlan.Columns.Add("SequenceOrder", typeof(int));
            dtQueueRatePlan.Columns.Add("CreatedBy");
            dtQueueRatePlan.Columns.Add("CreatedDate", typeof(DateTime));

            foreach (var ratePoolSequence in ratePoolSequences)
            {
                // add queue for rate plan permutation
                var queueId = CreateQueue(context, instanceId, commPlanGroupId, billingPeriod.ServiceProviderId, usesProration);

                // add rate plans to queue
                var dtQueueRatePlanTemp = AddRatePlansToQueue(queueId, ratePoolSequence, commGroupRatePlanTable);
                if (dtQueueRatePlanTemp != null && dtQueueRatePlanTemp.Rows.Count > 0)
                {
                    foreach (DataRow dr in dtQueueRatePlanTemp.Rows)
                    {
                        dtQueueRatePlan.Rows.Add(dr.ItemArray);
                    }
                }
            }

            CreateQueueRatePlans(context, dtQueueRatePlan);
        }

        private void ProcessNoRatePlanDevices(KeySysLambdaContext context, int? serviceProviderId, int? billingPeriodId, int? integrationAuthenticationId, bool usesProration, string revAccountNumber, BillingPeriod billingPeriod, long instanceId, SiteTypes customerType, int? AMOPCustomerId, int tenantId)
        {
            var unusedOptimizationSimCards = GetOptimizationSimCards(context, null, serviceProviderId, revAccountNumber, integrationAuthenticationId, billingPeriod.Id, tenantId, customerType, AMOPCustomerId);
            var noRatePlanCodes = unusedOptimizationSimCards
                .Where(c => string.IsNullOrWhiteSpace(c.CustomerRatePlanCode))
                .ToList();

            if (noRatePlanCodes.Count > 0)
            {
                LogInfo(context, LogTypeConstant.Info, $"Unused SIM Cards with no Customer Rate Plan Codes: {noRatePlanCodes.Count}");
                var unusedCommPlanGroupId = CreateCommPlanGroup(context, instanceId);
                var unusedQueueId = CreateQueue(context, instanceId, unusedCommPlanGroupId, null, usesProration);
                StartQueue(context, unusedQueueId, string.Empty);
                // no rate plan => already set total cost below as 0 => the sims will not participate in the algorithm
                var simsWithNoRatePlanCodes = ProjectDataUsageAndSaveDevices(context, instanceId, noRatePlanCodes, billingPeriod, false);
                OptimizationResultDbWriter.RecordRatePool(context, context.ConnectionString, unusedQueueId, billingPeriodId.Value, simsWithNoRatePlanCodes);
                OptimizationResultDbWriter.RecordTotalCost(context, context.ConnectionString, unusedQueueId, OptimizationConstant.DefaultUnassignedTotalCost);
                StopQueue(context, unusedQueueId);
            }
        }

        private async Task RunCrossProviderCustomerOptimization(KeySysLambdaContext context, int tenantId, int customerId, SiteTypes customerType, string serviceProviderIds, int customerBillingPeriodId, string messageId, long optimizationSessionId, bool isLastInstance, string additionalData)
        {
            LogInfo(context, CommonConstants.SUB, $"({tenantId},{messageId})");

            // get customer
            var customer = crossProviderOptimizationRepository.GetOptimizationCustomer(ParameterizedLog(context), customerId, customerType);


            // start instance
            if (customerBillingPeriodId > 0)
            {
                var billingPeriod = crossProviderOptimizationRepository.GetBillingPeriod(ParameterizedLog(context), customerId, customerBillingPeriodId, context.OptimizationSettings.BillingTimeZone);
                ArgumentNullException.ThrowIfNull(billingPeriod);

                // get customer rate plans
                var ratePlans = customerRatePlanRepository.GetCrossProviderCustomerRatePlans(ParameterizedLog(context), serviceProviderIds, customerType, new List<int> { customerId }, billingPeriod, tenantId);

                var useBillInAdvance = ratePlans.Count(x => x.IsBillInAdvanceEligible) > 0;
                //Disable bill in advance logic until new logic is defined (PORT-166)
                useBillInAdvance = false;

                LogVariableValue(context, nameof(useBillInAdvance), useBillInAdvance);

                BillingPeriod nextBillingPeriod = null;
                if (billingPeriod != null)
                {
                    nextBillingPeriod = crossProviderOptimizationRepository.GetBillingPeriod(ParameterizedLog(context), customerId, billingPeriod.BillingPeriodEnd.AddMonths(CommonConstants.BILL_CYCLE_LENGTH_IN_MONTHS), context.OptimizationSettings.BillingTimeZone);
                }

                var billInAdvanceBillingPeriodId = nextBillingPeriod?.Id;

                LogVariableValue(context, nameof(billInAdvanceBillingPeriodId), billInAdvanceBillingPeriodId);

                if (useBillInAdvance && (billInAdvanceBillingPeriodId == null || billingPeriod == null))
                {
                    LogInfo(context, CommonConstants.ERROR, $"A Billing Period past Customer Billing Period Id of {customerBillingPeriodId} could not be found for this Customer. So, billing in advance is not possible at this time. Optimization not run.");
                    return;
                }

                if (ratePlans.Count > 0)
                {
                    LogVariableValue(context, nameof(serviceProviderIds), serviceProviderIds);
                    LogVariableValue(context, nameof(billingPeriod), billingPeriod.Id);
                    var instanceId = crossProviderOptimizationRepository.StartCrossProviderOptimizationInstance(ParameterizedLog(context), tenantId, messageId,
                        customer, PortalTypes.CrossProvider, optimizationSessionId,
                        useBillInAdvance, billingPeriod, nextBillingPeriod, serviceProviderIds);

                    OptimizationChargeType chargeType = GetChargeType(useBillInAdvance);

                    LogVariableValue(context, nameof(chargeType), chargeType);

                    // check cache and send email if Redis cache is unreachable but the connection string is valid
                    await CheckRedisCache(context, optimizationSessionId, instanceId);

                    var isError = await ProcessCrossProviderDevicesByCustomerRatePlans(context, serviceProviderIds, false, ratePlans, billingPeriod, nextBillingPeriod, instanceId, chargeType, customer, tenantId);

                    if (!isError)
                    {
                        // enqueue cleanup method
                        EnqueueCleanup(context, instanceId, isCustomerOptimization: true, isLastInstance: isLastInstance);
                    }
                    else
                    {
                        var errorMessage = "There is an error in Processing Customer Rate Plans";
                        crossProviderOptimizationRepository.UpdateProcessingCustomerOptimizationInstance(ParameterizedLog(context), optimizationSessionId, instanceId, errorMessage, 0, false, customer.CustomerType, customer.RevAccountNumber, customer.CustomerId);
                        StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                        //triggger AMOP2.0 to send error message
                        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, customerId.ToString(), additionalData);
                    }

                    // Record charges for devices with no rate plans
                    ProcessNoRatePlanCrossProviderDevices(context, serviceProviderIds, false, billingPeriod, instanceId, customer);
                }
                else
                {
                    long instanceId = crossProviderOptimizationRepository.StartCrossProviderOptimizationInstance(ParameterizedLog(context), tenantId, messageId,
                                        customer, PortalTypes.CrossProvider, optimizationSessionId,
                                        useBillInAdvance, billingPeriod, nextBillingPeriod, serviceProviderIds);

                    var errorMessage = "No Comm Groups and/or Rate Plans for this Instance";
                    LogInfo(context, CommonConstants.ERROR, errorMessage);
                    crossProviderOptimizationRepository.UpdateProcessingCustomerOptimizationInstance(ParameterizedLog(context), optimizationSessionId, instanceId, errorMessage, 0, false, customer.CustomerType, customer.RevAccountNumber, customer.CustomerId);
                    StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithErrors);
                    //triggger AMOP2.0 to send error message
                    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, customerId.ToString(), additionalData);
                }
            }
            else
            {
                LogInfo(context, CommonConstants.ERROR, "No Billing Period found for Customer {0} and Bill Cycle End Date {1}");
            }
        }

        private async Task CheckRedisCache(KeySysLambdaContext context, long optimizationSessionId, long instanceId)
        {
            if (context.IsRedisConnectionStringValid && !IsUsingRedisCache)
            {
                await LogAndSendConfigurationIssueEmailAsync(context, ErrorNotificationEmailReceiver, optimizationSessionId, instanceId);
            }
        }

        private async Task<bool> ProcessCrossProviderDevicesByCustomerRatePlans(KeySysLambdaContext context, string serviceProviderIds, bool usesProration, List<RatePlan> ratePlans, BillingPeriod billingPeriod, BillingPeriod nextBillingPeriod, long instanceId, OptimizationChargeType chargeType, OptimizationCustomer customer, int tenantId)
        {
            ArgumentNullException.ThrowIfNull(customer);
            var optimizationSimCards = crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards(ParameterizedLog(context), customer.CustomerType, customer.CustomerId, customer.RevAccountNumber, customer.IntegrationAuthenticationId, billingPeriod, serviceProviderIds);

            optimizationSimCards = optimizationSimCards.Where(s => !string.IsNullOrWhiteSpace(s.CustomerRatePlanCode)).ToList();

            // Process Pooled by Customer Rate Pool
            var ratePlansByCustomerRatePool = ratePlans.Where(ratePlan => !ratePlan.AutoChangeRatePlan).ToList();
            if (ratePlansByCustomerRatePool.Any())
            {
                if (CheckZeroValueRatePlans(context, instanceId, ratePlansByCustomerRatePool, shouldStopInstance: true))
                {
                    return true;
                }
                else
                {
                    // process and return the remaining devices for optimization with algorithm
                    optimizationSimCards = ProcessDevicesWithAutoChangeDisabledRatePlans(context, customer.IntegrationAuthenticationId, usesProration, customer.RevAccountNumber, customer.CustomerId, billingPeriod, nextBillingPeriod, instanceId, optimizationSimCards, ratePlansByCustomerRatePool, tenantId, serviceProviderIds);
                    // checked
                }
            }

            var autoChangeRatePlans = ratePlans.Where(ratePlan => ratePlan.AutoChangeRatePlan);
            if (autoChangeRatePlans.Any() && !string.IsNullOrWhiteSpace(serviceProviderIds))
            {
                var serviceProviderIdList = serviceProviderIds.Split(CommonConstants.STRING_ITEMS_SEPERATOR).ToList();
                autoChangeRatePlans = autoChangeRatePlans.Where(x => x.ServiceProviderIds.Split(CommonConstants.STRING_ITEMS_SEPERATOR).ToList().ContainsAllItems(serviceProviderIdList)).ToList();
                if (!autoChangeRatePlans.Any())
                {
                    LogInfo(context, CommonConstants.ERROR, string.Format(LogCommonStrings.NO_VALID_CROSS_PROVIDER_CUSTOMER_RATE_PLAN_FOUND, serviceProviderIds));
                    return true;
                }
            }

            var simCardsByRatePoolIds = optimizationSimCards.GroupBy(x => x.CustomerRatePoolId).Distinct();

            foreach (var simCardsByRatePoolId in simCardsByRatePoolIds)
            {
                LogInfo(context, CommonConstants.INFO, $"RatePoolId: {simCardsByRatePoolId.Key}");
                // Get all rate plan codes from the devices
                var ratePlanCodes = simCardsByRatePoolId.Select(x => x.CustomerRatePlanCode).Distinct();
                var isError = false;
                if (simCardsByRatePoolId.Key != null)
                {
                    // Get all rate plans with matching rate plan codes
                    var ratePlansForPool = ratePlans.Where(x => ratePlanCodes.Contains(x.PlanName));
                    isError = await ProcessRatePoolGroup(context, customer.IntegrationAuthenticationId, usesProration, customer.RevAccountNumber, customer.CustomerId, billingPeriod, instanceId, chargeType, ratePlansForPool, simCardsByRatePoolId.ToList(), simCardsByRatePoolId?.Key, queuesPerInstance: QueuesPerInstance);
                }
                else
                {
                    // Group rate plans by rate plan code and run auto change optimization logic for this group of devices
                    var ratePlansByCodes = ratePlans.Where(ratePlan => ratePlan.AutoChangeRatePlan && ratePlanCodes.Contains(ratePlan.PlanName)).GroupBy(x => x.PlanName);
                    foreach (var ratePlansByCode in ratePlansByCodes)
                    {
                        isError = await ProcessPlanNameGroup(context, customer.IntegrationAuthenticationId, usesProration, customer.RevAccountNumber, customer.CustomerId, billingPeriod, instanceId, chargeType, ratePlansByCode, optimizationSimCards);
                    }
                }

                // If error, stop optimization midway
                if (isError)
                {
                    return isError;
                }
            }
            return false;
        }

        private void ProcessNoRatePlanCrossProviderDevices(KeySysLambdaContext context, string serviceProviderIds, bool usesProration, BillingPeriod customerBillingPeriod, long instanceId, OptimizationCustomer customer)
        {
            var unusedOptimizationSimCards = crossProviderOptimizationRepository.GetCrossProviderCustomerSimCards(ParameterizedLog(context), customer.CustomerType, customer.CustomerId, customer.RevAccountNumber, customer.IntegrationAuthenticationId, customerBillingPeriod, serviceProviderIds);
            var noRatePlanCodes = unusedOptimizationSimCards
                .Where(c => string.IsNullOrWhiteSpace(c.CustomerRatePlanCode))
                .ToList();

            if (noRatePlanCodes.Count > 0)
            {
                LogInfo(context, LogTypeConstant.Info, $"Unused SIM Cards with no Customer Rate Plan Codes: {noRatePlanCodes.Count}");
                var unusedCommPlanGroupId = CreateCommPlanGroup(context, instanceId);
                var unusedQueueId = CreateQueue(context, instanceId, unusedCommPlanGroupId, null, usesProration);
                StartQueue(context, unusedQueueId, string.Empty);
                // no rate plan => already set total cost below as 0 => the sims will not participate in the algorithm
                var simsWithNoRatePlanCodes = ProjectDataUsageAndSaveDevices(context, instanceId, noRatePlanCodes, customerBillingPeriod, false);
                OptimizationResultDbWriter.RecordCrossProviderRatePool(context, context.ConnectionString, unusedQueueId, simsWithNoRatePlanCodes, customerBillingPeriod.Id);
                OptimizationResultDbWriter.RecordTotalCost(context, context.ConnectionString, unusedQueueId, OptimizationConstant.DefaultUnassignedTotalCost);
                StopQueue(context, unusedQueueId);
            }
        }
    }
}
