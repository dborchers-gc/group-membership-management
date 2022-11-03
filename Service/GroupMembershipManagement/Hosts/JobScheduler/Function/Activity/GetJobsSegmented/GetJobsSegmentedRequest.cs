// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Azure;
using Entities;

namespace Hosts.JobScheduler
{
    public class GetJobsSegmentedRequest
    {
        public AsyncPageable<SyncJob> PageableQueryResult;
        public string ContinuationToken;
        public bool IncludeFutureJobs;
    }
}
