// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Repositories.Contracts;
using Services.Contracts;
using System;
using System.Threading.Tasks;

namespace Hosts.JobTrigger
{
    public class GroupNameReaderFunction
    {
        private readonly ILoggingRepository _loggingRepository = null;
        private readonly ISyncJobTopicService _syncJobTopicService = null;
        public GroupNameReaderFunction(ILoggingRepository loggingRepository, ISyncJobTopicService syncJobService)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _syncJobTopicService = syncJobService ?? throw new ArgumentNullException(nameof(syncJobService)); ;
        }

        [FunctionName(nameof(GroupNameReaderFunction))]
        public async Task<SyncJobGroup> GetGroupName([ActivityTrigger] SyncJob syncJob, ILogger log)
        {
            var group = new SyncJobGroup();
            if (syncJob != null)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(GroupNameReaderFunction)} function started", RunId = syncJob.RunId });
                var groupName = await _syncJobTopicService.GetGroupNameAsync(syncJob.TargetOfficeGroupId);
                group = new SyncJobGroup
                {
                    SyncJob = syncJob,
                    Name = groupName
                };
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(GroupNameReaderFunction)} function completed", RunId = syncJob.RunId });
            }
            return group;
        }
    }
}