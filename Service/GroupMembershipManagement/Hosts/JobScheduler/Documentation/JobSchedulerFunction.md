# Job Scheduler Function App
This guide will explain how to use the JobScheduler function, found in JobScheduler/Function

## Prerequisites
* Visual Studio (tested in VS 2019, but likely works with other versions as well)
* Download the latest version of the public Github repository from: https://github.com/microsoftgraph/group-membership-management
* Ensure that GMM is already set up in your environment, as you will need some of the values from your gmm-data- keyvault for the console app

### Job Scheduler Config
Format:
```
{
    ResetJobs: boolean
    DaysToAddForReset: int
    DistributeJobs: boolean
    IncludeFutureJobs: boolean
    StartTimeDelayMinutes: int
    DelayBetweenSyncsSeconds: int
    DefaultRuntimeSeconds: int
}
```
Note: Only enabled jobs will be included in these operations

### The two main functionalities are:
* ResetJobs: If this is true, then all jobs will have their StartDate reset to the current date minus the number of days specified in "daysToAddForReset" from Settings.json
* DistributeJobs: If this is true, then all jobs will be rescheduled / redistributed based on the AppConfig value for "JobScheduler:JobSchedulerConfig"

<i>The remaining properties are specifications for how to reset / distribute jobs

Note: If ResetJobs and DistributeJobs are both true, then jobs will be reset and then distributed</i>

### When "ResetJobs" is true, the following properties will be used:
* DaysToAddForReset: This represents how many days in the future to reset job StartDates to (this can be a negative number)
* IncludeFutureJobs: If this is true, then jobs that have StartDate in the future will be included in the operation, if not, then they won't

### When "DistributeJobs" is true, the following properties will be used:
* IncludeFutureJobs: If this is true, then jobs that have StartDate in the future will be included in the operation, if not, then they won't
* StartTimeDelayMinutes : The delay in minutes to wait before running the first of the scheduled / distributed jobs
* DelayBetweenSyncsSeconds: The delay in seconds to wait between each scheduled / distributed job
* DefaultRuntimeSeconds: The approximate runtime in seconds of each job (the app will base its scheduling on this runtime so choose carefully)