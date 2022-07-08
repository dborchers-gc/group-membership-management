// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Repositories.Contracts;
using System;
using System.Threading.Tasks;

namespace Hosts.MembershipAggregator
{
    public class JobStatusUpdaterFunction
    {
        private readonly ILoggingRepository _loggingRepository;
        private readonly ISyncJobRepository _syncJobrespository;

        public JobStatusUpdaterFunction(ILoggingRepository loggingRepository, ISyncJobRepository syncJobRespository)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _syncJobrespository = syncJobRespository ?? throw new ArgumentNullException(nameof(syncJobRespository));
        }

        [FunctionName(nameof(JobStatusUpdaterFunction))]
        public async Task UpdateJobStatusAsync([ActivityTrigger] JobStatusUpdaterRequest request)
        {
            var syncJobProperties = request.SyncJob.ToDictionary();
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(JobStatusUpdaterFunction)} function started", DynamicProperties = syncJobProperties }, VerbosityLevel.DEBUG);

            var syncJob = await _syncJobrespository.GetSyncJobAsync(request.SyncJob.PartitionKey, request.SyncJob.RowKey);
            if (syncJob != null)
            {
                if (request.ThresholdViolations.HasValue)
                    syncJob.ThresholdViolations = request.ThresholdViolations.Value;

                if (request.IsDryRun)
                    syncJob.DryRunTimeStamp = DateTime.UtcNow;
                else
                    syncJob.LastRunTime = DateTime.UtcNow;

                await _syncJobrespository.UpdateSyncJobsAsync(new[] { syncJob }, request.Status);
            }

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(JobStatusUpdaterFunction)} function completed", DynamicProperties = syncJobProperties }, VerbosityLevel.DEBUG);
        }
    }
}