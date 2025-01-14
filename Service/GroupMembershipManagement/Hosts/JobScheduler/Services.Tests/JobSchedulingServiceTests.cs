// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Services.Contracts;
using Services.Tests.Mocks;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MockSyncJobRepository = Repositories.SyncJobs.Tests.MockSyncJobRepository;

namespace Services.Tests
{
    [TestClass]
    public class JobSchedulingServiceTests
    {
        public int DEFAULT_RUNTIME_SECONDS = 60;
        public int START_TIME_DELAY_MINUTES = 60;
        public int BUFFER_SECONDS = 10;

        private JobSchedulingService _jobSchedulingService = null;
        private MockSyncJobRepository _mockSyncJobRepository = null;
        private DefaultRuntimeRetrievalService _defaultRuntimeRetrievalService = null;
        private MockLoggingRepository _mockLoggingRepository = null;

        [TestInitialize]
        public void InitializeTest()
        {
            _mockSyncJobRepository = new MockSyncJobRepository();
            _defaultRuntimeRetrievalService = new DefaultRuntimeRetrievalService(DEFAULT_RUNTIME_SECONDS);
            _mockLoggingRepository = new MockLoggingRepository();

            _jobSchedulingService = new JobSchedulingService(
                _mockSyncJobRepository,
                _defaultRuntimeRetrievalService,
                _mockLoggingRepository
            );
        }

        [TestMethod]
        public void ResetAllStartTimes()
        {
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(10, 1);
            DateTime newStartTime = DateTime.UtcNow;

            List<DistributionSyncJob> updatedJobs = _jobSchedulingService.ResetJobStartTimes(jobs, newStartTime, false);

            Assert.AreEqual(jobs.Count, updatedJobs.Count);

            foreach (DistributionSyncJob job in updatedJobs)
            {
                Assert.AreEqual(job.StartDate, newStartTime);
            }
        }

        [TestMethod]
        public void ResetOlderStartTimes()
        {
            DateTime newStartTime = DateTime.UtcNow.Date;
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(10, 1, newStartTime.AddDays(4));

            List<DistributionSyncJob> updatedJobs = _jobSchedulingService.ResetJobStartTimes(jobs, newStartTime, false);

            Assert.AreEqual(jobs.Count, updatedJobs.Count);

            int startTimeUpdatedCount = 0;
            int startTimeNotUpdatedCount = 0;

            foreach (DistributionSyncJob job in updatedJobs)
            {
                if (job.StartDate == newStartTime)
                {
                    startTimeUpdatedCount++;
                }
                else
                {
                    startTimeNotUpdatedCount++;
                }
            }

            Assert.AreEqual(startTimeUpdatedCount, 6);
            Assert.AreEqual(startTimeNotUpdatedCount, 4);
        }

        [TestMethod]
        public async Task ScheduleJobsNone()
        {
            List<DistributionSyncJob> jobs = new List<DistributionSyncJob>();

            List<DistributionSyncJob> updatedJobs = await _jobSchedulingService.DistributeJobStartTimesAsync(jobs, START_TIME_DELAY_MINUTES, BUFFER_SECONDS);

            Assert.AreEqual(updatedJobs.Count, 0);
        }

        [TestMethod]
        public async Task ScheduleJobsOne()
        {
            DateTime dateTimeNow = DateTime.UtcNow;
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(1, 1);
            List<DistributionSyncJob> updatedJobs = await _jobSchedulingService.DistributeJobStartTimesAsync(jobs, START_TIME_DELAY_MINUTES, BUFFER_SECONDS);

            Assert.AreEqual(updatedJobs.Count, 1);
            Assert.IsTrue(updatedJobs[0].StartDate > dateTimeNow);
        }

        [TestMethod]
        public async Task ScheduleJobsMultipleWithPriority()
        {
            DateTime dateTimeNow = DateTime.UtcNow;
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(10, 1, dateTimeNow.Date.AddDays(-20), dateTimeNow.Date);

            List<DistributionSyncJob> updatedJobs = await _jobSchedulingService.DistributeJobStartTimesAsync(jobs, START_TIME_DELAY_MINUTES, BUFFER_SECONDS);

            jobs.Sort();
            updatedJobs.Sort();

            Assert.AreEqual(jobs.Count, updatedJobs.Count);

            for (int i = 0; i < jobs.Count; i++)
            {
                Assert.AreEqual(jobs[i].TargetOfficeGroupId, updatedJobs[i].TargetOfficeGroupId);
                Assert.IsTrue(jobs[i].StartDate < dateTimeNow);
                Assert.IsTrue(updatedJobs[i].StartDate >= dateTimeNow.AddSeconds(60 * START_TIME_DELAY_MINUTES +
                    i * (DEFAULT_RUNTIME_SECONDS + BUFFER_SECONDS)));
            }
        }

        [TestMethod]
        public async Task ScheduleJobsWithConcurrency()
        {
            int defaultTenMinuteRuntime = 600;
            var longerDefaultRuntimeService = new DefaultRuntimeRetrievalService(defaultTenMinuteRuntime);

            JobSchedulerConfig jobSchedulerConfig = new JobSchedulerConfig(true, 0, true, false, START_TIME_DELAY_MINUTES, BUFFER_SECONDS, DEFAULT_RUNTIME_SECONDS); ;
            JobSchedulingService jobSchedulingService = new JobSchedulingService(
                _mockSyncJobRepository,
                longerDefaultRuntimeService,
                _mockLoggingRepository
            );

            DateTime dateTimeNow = DateTime.UtcNow.Date;
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(10, 1, dateTimeNow.Date.AddDays(-20), dateTimeNow.Date);

            List<DistributionSyncJob> updatedJobs = await jobSchedulingService.DistributeJobStartTimesAsync(jobs, START_TIME_DELAY_MINUTES, BUFFER_SECONDS);

            jobs.Sort();
            updatedJobs.Sort();

            Assert.AreEqual(jobs.Count, updatedJobs.Count);
            Assert.AreEqual(jobs.Count, 10);

            // Check that times are sorted like this with concurrency of 2:
            // 0  2  4  6  8  9
            // 1  3  5  7
            for (int i = 0; i < jobs.Count; i++)
            {
                Assert.AreEqual(jobs[i].TargetOfficeGroupId, updatedJobs[i].TargetOfficeGroupId);
                Assert.IsTrue(jobs[i].StartDate < dateTimeNow);
                if (i < 8)
                {
                    Assert.IsTrue(updatedJobs[i].StartDate >= dateTimeNow.AddSeconds(60 * START_TIME_DELAY_MINUTES +
                        i / 2 * (defaultTenMinuteRuntime + BUFFER_SECONDS)));
                }
                else
                {
                    Assert.IsTrue(updatedJobs[i].StartDate >= dateTimeNow.AddSeconds(60 * START_TIME_DELAY_MINUTES +
                        (i - 5) * (defaultTenMinuteRuntime + BUFFER_SECONDS)));
                }
            }
        }

        [TestMethod]
        public async Task ScheduleJobsWithTwoDifferentPeriods()
        {
            DateTime dateTimeNow = DateTime.UtcNow;
            List<DistributionSyncJob> jobs = CreateSampleSyncJobs(3, 1, dateTimeNow.Date.AddDays(-20), dateTimeNow.Date);
            jobs.AddRange(CreateSampleSyncJobs(3, 24, dateTimeNow.Date.AddDays(-20), dateTimeNow.Date));

            List<DistributionSyncJob> updatedJobs = await _jobSchedulingService.DistributeJobStartTimesAsync(jobs, START_TIME_DELAY_MINUTES, BUFFER_SECONDS);

            jobs.Sort(new PeriodComparer());
            updatedJobs.Sort(new PeriodComparer());

            for (int i = 0; i < jobs.Count; i++)
            {
                Assert.AreEqual(jobs[i].TargetOfficeGroupId, updatedJobs[i].TargetOfficeGroupId);
                Assert.IsTrue(jobs[i].StartDate < dateTimeNow);

                if (i < 3)
                {
                    Assert.IsTrue(updatedJobs[i].StartDate >= dateTimeNow.AddSeconds(60 * START_TIME_DELAY_MINUTES +
                        i * (DEFAULT_RUNTIME_SECONDS + BUFFER_SECONDS)));
                }
                else
                {
                    Assert.IsTrue(updatedJobs[i].StartDate >= dateTimeNow.AddSeconds(60 * START_TIME_DELAY_MINUTES +
                        (i - 3) * (DEFAULT_RUNTIME_SECONDS + BUFFER_SECONDS)));
                }
            }
        }

        private List<DistributionSyncJob> CreateSampleSyncJobs(int numberOfJobs, int period, DateTime? startDateBase = null, DateTime? lastRunTimeBase = null)
        {
            var jobs = new List<DistributionSyncJob>();
            DateTime StartDateBase = startDateBase ?? DateTime.UtcNow.AddDays(-1);
            DateTime LastRunTimeBase = lastRunTimeBase ?? DateTime.UtcNow.AddDays(-1);

            for (int i = 0; i < numberOfJobs; i++)
            {
                var job = new DistributionSyncJob
                {
                    PartitionKey = DateTime.UtcNow.ToString("MMddyyyy"),
                    RowKey = Guid.NewGuid().ToString(),
                    Period = period,
                    StartDate = StartDateBase.AddDays(-1 * i),
                    Status = SyncStatus.Idle.ToString(),
                    TargetOfficeGroupId = Guid.NewGuid(),
                    LastRunTime = LastRunTimeBase.AddDays(-1 * i)
                };

                jobs.Add(job);
            }

            return jobs;
        }
    }

    public class PeriodComparer : Comparer<DistributionSyncJob>
    {
        public override int Compare(DistributionSyncJob x, DistributionSyncJob y)
        {
            if (x.Period != y.Period)
            {
                return x.Period.CompareTo(y.Period);
            }

            return x.CompareTo(y);
        }
    }
}
