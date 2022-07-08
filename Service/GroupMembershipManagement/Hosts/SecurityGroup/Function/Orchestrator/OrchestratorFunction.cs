// Copyright(c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Repositories.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Hosts.SecurityGroup
{
    public class OrchestratorFunction
    {
        private readonly ILoggingRepository _log;
        private readonly IGraphGroupRepository _graphGroup;
        private readonly IConfiguration _configuration;
        private readonly SGMembershipCalculator _calculator;
        private const string SyncDisabledNoValidGroupIds = "SyncDisabledNoValidGroupIds";

        public OrchestratorFunction(
            ILoggingRepository loggingRepository,
            IGraphGroupRepository graphGroupRepository,
            SGMembershipCalculator calculator,
            IConfiguration configuration)
        {
            _log = loggingRepository;
            _graphGroup = graphGroupRepository;
            _calculator = calculator;
            _configuration = configuration;
        }

        [FunctionName(nameof(OrchestratorFunction))]
        public async Task RunOrchestratorAsync([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var mainRequest = context.GetInput<OrchestratorRequest>();
            var syncJob = mainRequest.SyncJob;
            var runId = syncJob.RunId.GetValueOrDefault(context.NewGuid());
            List<AzureADUser> distinctUsers = null;

            _log.SyncJobProperties = syncJob.ToDictionary();
            _graphGroup.RunId = runId;

            try
            {
                if (mainRequest.CurrentPart <= 0 || mainRequest.TotalParts <= 0)
                {
                    if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { RunId = runId, Message = $"Found invalid value for CurrentPart or TotalParts" });
                    await context.CallActivityAsync(nameof(JobStatusUpdaterFunction), new JobStatusUpdaterRequest { SyncJob = syncJob, Status = SyncStatus.Error });
                    return;
                }

                if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { Message = $"{nameof(OrchestratorFunction)} function started", RunId = runId }, VerbosityLevel.DEBUG);
                var sourceGroups = await context.CallActivityAsync<AzureADGroup[]>(nameof(SourceGroupsReaderFunction),
                                                                                    new SourceGroupsReaderRequest
                                                                                    {
                                                                                        SyncJob = syncJob,
                                                                                        CurrentPart = mainRequest.CurrentPart,
                                                                                        IsDestinationPart = mainRequest.IsDestinationPart,
                                                                                        RunId = runId
                                                                                    });

                if (sourceGroups.Length == 0)
                {
                    if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { RunId = runId, Message = $"None of the source groups in Part# {mainRequest.CurrentPart} {syncJob.Query} were valid guids. Marking job as errored." });
                    await context.CallActivityAsync(nameof(EmailSenderFunction), new EmailSenderRequest { SyncJob = syncJob, RunId = runId });
                    await context.CallActivityAsync(nameof(JobStatusUpdaterFunction), new JobStatusUpdaterRequest { SyncJob = syncJob, Status = SyncStatus.Error });
                    return;
                }
                else
                {
                    // Run multiple source group processing flows in parallel
                    var processingTasks = new List<Task<(List<AzureADUser> Users, SyncStatus Status)>>();
                    foreach (var sourceGroup in sourceGroups)
                    {
                        var processTask = context.CallSubOrchestratorAsync<(List<AzureADUser> Users, SyncStatus Status)>(nameof(SubOrchestratorFunction), new SecurityGroupRequest { SyncJob = syncJob, SourceGroup = sourceGroup, RunId = runId });
                        processingTasks.Add(processTask);
                    }
                    var tasks = await Task.WhenAll(processingTasks);
                    if (tasks.Any(x => x.Status == SyncStatus.SecurityGroupNotFound))
                    {
                        await context.CallActivityAsync(nameof(JobStatusUpdaterFunction), new JobStatusUpdaterRequest { SyncJob = syncJob, Status = SyncStatus.SecurityGroupNotFound });
                        return;
                    }

                    var users = new List<AzureADUser>(tasks.SelectMany(x => x.Users));
                    distinctUsers = users.GroupBy(user => user.ObjectId).Select(userGrp => userGrp.First()).ToList();

                    if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage
                    {
                        RunId = runId,
                        Message = $"Found {users.Count - distinctUsers.Count} duplicate user(s). " +
                                    $"Read {distinctUsers.Count} users from source groups {syncJob.Query} to be synced into the destination group {syncJob.TargetOfficeGroupId}."
                    });

                    var filePath = await context.CallActivityAsync<string>(nameof(UsersSenderFunction),
                                                                            new UsersSenderRequest { SyncJob = syncJob, RunId = runId, Users = distinctUsers, CurrentPart = mainRequest.CurrentPart });

                    if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { Message = "Calling MembershipAggregator", RunId = runId });
                    var content = new MembershipAggregatorHttpRequest
                    {
                        FilePath = filePath,
                        PartNumber = mainRequest.CurrentPart,
                        PartsCount = mainRequest.TotalParts,
                        SyncJob = syncJob,
                        IsDestinationPart = mainRequest.IsDestinationPart
                    };

                    var request = new DurableHttpRequest(HttpMethod.Post,
                                                            new Uri(_configuration["membershipAggregatorUrl"]),
                                                            content: JsonConvert.SerializeObject(content),
                                                            headers: new Dictionary<string, StringValues> { { "x-functions-key", _configuration["membershipAggregatorFunctionKey"] } },
                                                            httpRetryOptions: new HttpRetryOptions(TimeSpan.FromSeconds(30), 3));

                    var response = await context.CallHttpAsync(request);
                    if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { Message = $"MembershipAggregator response Code: {response.StatusCode}, Content: {response.Content}", RunId = runId });

                    if (response.StatusCode != HttpStatusCode.NoContent)
                    {
                        await context.CallActivityAsync(nameof(JobStatusUpdaterFunction), new JobStatusUpdaterRequest { SyncJob = syncJob, Status = SyncStatus.Error });
                    }
                }
            }
            catch (Exception ex)
            {
                _ = _log.LogMessageAsync(new LogMessage { Message = $"Caught unexpected exception in Part# {mainRequest.CurrentPart}, marking sync job as errored. Exception:\n{ex}", RunId = runId });

                await context.CallActivityAsync(nameof(JobStatusUpdaterFunction), new JobStatusUpdaterRequest { SyncJob = syncJob, Status = SyncStatus.Error });

                // make sure this gets thrown to where App Insights will handle it
                throw;
            }

            if (!context.IsReplaying) _ = _log.LogMessageAsync(new LogMessage { Message = $"{nameof(OrchestratorFunction)} function completed", RunId = runId }, VerbosityLevel.DEBUG);
        }
    }
}