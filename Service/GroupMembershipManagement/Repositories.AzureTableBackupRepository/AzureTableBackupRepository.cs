// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Entities.AzureMaintenance;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.Cosmos.Table.Queryable;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Repositories.AzureMaintenanceRepository
{
    public class AzureMaintenanceRepository : IAzureMaintenanceRepository
    {
        private const string BACKUP_PREFIX = "zzBackup";
        private const string BACKUP_TABLE_NAME_SUFFIX = "BackupTracker";
        private const string BACKUP_DATE_FORMAT = "yyyyMMddHHmmss";
        private readonly ILoggingRepository _loggingRepository = null;

        public AzureMaintenanceRepository(ILoggingRepository loggingRepository)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
        }

        public async Task<List<BackupEntity>> GetBackupsAsync(IAzureMaintenance backupSettings)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Getting backup tables for table {backupSettings.SourceTableName}" });

            var storageAccount = CloudStorageAccount.Parse(backupSettings.DestinationConnectionString);
            var tableClient = storageAccount.CreateCloudTableClient();

            List<CloudTable> tables;

            if (backupSettings.SourceTableName == "*")
            {
                tables = tableClient.ListTables().ToList();
            }
            else
            {
                tables = tableClient.ListTables(prefix: BACKUP_PREFIX + backupSettings.SourceTableName).ToList();
            }

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Found {tables.Count} backup tables for table {backupSettings.SourceTableName}" });

            return tables.Select(table => new BackupEntity(table.Name, "table")).ToList();
        }

        public async Task<List<DynamicTableEntity>> GetEntitiesAsync(IAzureMaintenance backupSettings)
        {
            var table = await GetCloudTableAsync(backupSettings.SourceConnectionString, backupSettings.SourceTableName);
            var entities = new List<DynamicTableEntity>();
            var query = table.CreateQuery<DynamicTableEntity>().AsTableQuery();

            if (!(await table.ExistsAsync()))
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Source table {backupSettings.SourceTableName} was not found!" });
                return null;
            }

            TableContinuationToken continuationToken = null;
            do
            {
                var segmentResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = segmentResult.ContinuationToken;
                entities.AddRange(segmentResult.Results);

            } while (continuationToken != null);

            return entities;
        }

        public async Task DeleteBackupTrackersAsync(IAzureMaintenance backupSettings, List<(string PartitionKey, string RowKey)> entities)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Deleting old backup trackers from {backupSettings.SourceTableName}" });

            var batchSize = 100;
            var currentSize = 0;
            var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, backupSettings.SourceTableName + BACKUP_TABLE_NAME_SUFFIX);
            var groupedEntities = entities.GroupBy(x => x.PartitionKey);
            var deletedEntitiesCount = 0;

            foreach (var group in groupedEntities)
            {
                var deleteBatchOperation = new TableBatchOperation();

                foreach (var entity in group.AsEnumerable())
                {
                    var entityToDelete = table.Execute(TableOperation.Retrieve<BackupResult>(entity.PartitionKey, entity.RowKey));
                    if (entityToDelete.HttpStatusCode != 404)
                        deleteBatchOperation.Delete(entityToDelete.Result as BackupResult);

                    if (++currentSize == batchSize)
                    {
                        var deleteResponse = await table.ExecuteBatchAsync(deleteBatchOperation);
                        deletedEntitiesCount += deleteResponse.Count(x => IsSuccessStatusCode(x.HttpStatusCode));

                        deleteBatchOperation = new TableBatchOperation();
                        currentSize = 0;
                    }
                }

                if (deleteBatchOperation.Any())
                {
                    var deleteResponse = await table.ExecuteBatchAsync(deleteBatchOperation);
                    deletedEntitiesCount += deleteResponse.Count(x => IsSuccessStatusCode(x.HttpStatusCode));
                }
            }

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Deleted {deletedEntitiesCount} old backup trackers from {backupSettings.SourceTableName}" });
        }

        public async Task<BackupResult> BackupEntitiesAsync(IAzureMaintenance backupSettings, List<DynamicTableEntity> entities)
        {
            var tableName = $"{BACKUP_PREFIX}{backupSettings.SourceTableName}{DateTime.UtcNow.ToString(BACKUP_DATE_FORMAT)}";
            var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, tableName);

            if (!await table.ExistsAsync())
            {
                await table.CreateIfNotExistsAsync();
            }

            await _loggingRepository.LogMessageAsync(
                new LogMessage
                {
                    Message = $"Backing up data to table: {tableName} started",
                    DynamicProperties = { { "status", "Started" } }
                });

            var backupCount = 0;
            var batchSize = 100;
            var currentSize = 0;
            var groups = entities.GroupBy(x => x.PartitionKey).ToList();

            foreach (var group in groups)
            {
                var batchOperation = new TableBatchOperation();

                foreach (var job in group)
                {

                    batchOperation.Insert(job);

                    if (++currentSize == batchSize)
                    {
                        var result = await table.ExecuteBatchAsync(batchOperation);
                        backupCount += result.Count(x => IsSuccessStatusCode(x.HttpStatusCode));
                        batchOperation = new TableBatchOperation();
                        currentSize = 0;
                    }
                }

                if (batchOperation.Any())
                {
                    var result = await table.ExecuteBatchAsync(batchOperation);
                    backupCount += result.Count(x => IsSuccessStatusCode(x.HttpStatusCode));
                }
            }

            await _loggingRepository.LogMessageAsync(
                new LogMessage
                {
                    Message = $"Backing up data to table: {tableName} completed",
                    DynamicProperties = {
                        { "status", "Completed" },
                        { "rowCount", backupCount.ToString() }
                }
                });

            return new BackupResult(tableName, "table", backupCount);
        }

        public async Task<bool> VerifyDeleteBackupAsync(IAzureMaintenance backupSettings, string tableName)
        {
            var cutOffDate = DateTime.UtcNow.AddDays(-backupSettings.DeleteAfterDays);

            if (backupSettings.SourceTableName == "*")
            {
                var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, tableName);

                // Do not delete empty tables
                var takeOneQuery = table.CreateQuery<TableEntity>().AsQueryable().Take(1);
                var takeOneResults = table.ExecuteQuery(takeOneQuery.AsTableQuery()).ToList();
                if(takeOneResults.Count == 0)
                {
                    return false;
                }

                var cutoffQuery = table.CreateQuery<TableEntity>().AsQueryable().Where(e => e.Timestamp >= cutOffDate).Take(1);
                var cutoffResults = table.ExecuteQuery(cutoffQuery.AsTableQuery()).ToList();

                if (cutoffResults.Count == 0)
                {
                    return true;
                }
            }
            else
            {
                var CreatedDate = DateTime.SpecifyKind(
                    DateTime.ParseExact(tableName.Replace(BACKUP_PREFIX + backupSettings.SourceTableName, string.Empty),
                        BACKUP_DATE_FORMAT,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal),
                    DateTimeKind.Utc);
                if (CreatedDate < cutOffDate)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task DeleteBackupAsync(IAzureMaintenance backupSettings, string tableName)
        {
            await _loggingRepository.LogMessageAsync(new Entities.LogMessage { Message = $"Deleting backup table: {tableName}" });

            var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, tableName);

            if (!await table.ExistsAsync())
            {
                await _loggingRepository.LogMessageAsync(new Entities.LogMessage { Message = $"Table not found : {tableName}" });
                return;
            }

            await table.DeleteIfExistsAsync();

            await _loggingRepository.LogMessageAsync(new Entities.LogMessage { Message = $"Deleted backup table: {tableName}" });
        }

        private bool IsSuccessStatusCode(int statusCode) => statusCode >= 200 && statusCode <= 299;

        public async Task AddBackupResultTrackerAsync(IAzureMaintenance backupSettings, BackupResult backupResult)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Creating backup tracker for {backupSettings.SourceTableName}" });

            var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, backupSettings.SourceTableName + BACKUP_TABLE_NAME_SUFFIX);

            if (!await table.ExistsAsync())
            {
                await table.CreateIfNotExistsAsync();
            }

            backupResult.PartitionKey = backupSettings.SourceTableName;
            backupResult.RowKey = backupResult.BackupTableName;

            await table.ExecuteAsync(TableOperation.Insert(backupResult));

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Created backup tracker ({backupResult.RowKey}) for {backupSettings.SourceTableName}" });
        }

        public async Task<BackupResult> GetLastestBackupResultTrackerAsync(IAzureMaintenance backupSettings)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Getting latest backup tracker for {backupSettings.SourceTableName}" });

            var table = await GetCloudTableAsync(backupSettings.DestinationConnectionString, backupSettings.SourceTableName + BACKUP_TABLE_NAME_SUFFIX);

            if (!await table.ExistsAsync())
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"No backup tracker found for {backupSettings.SourceTableName}" });
                return null;
            }

            var results = new List<BackupResult>();
            var query = table.CreateQuery<BackupResult>().Where(x => x.PartitionKey == backupSettings.SourceTableName).AsTableQuery();

            TableContinuationToken continuationToken = null;
            do
            {
                var segmentResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = segmentResult.ContinuationToken;
                results.AddRange(segmentResult.Results);

            } while (continuationToken != null);

            var backupResult = results.OrderByDescending(x => x.Timestamp).FirstOrDefault();

            // if this is null (because the record predates table and blob backup) assume table
            backupResult.BackedUpTo = backupResult.BackedUpTo ?? "table";

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Found latest backup tracker ([{backupResult.Timestamp.UtcDateTime}] - {backupResult.RowKey}) for {backupSettings.SourceTableName}" });

            return backupResult;
        }

        private async Task<CloudTableClient> GetCloudTableClientAsync(string connectionString)
        {
            try
            {
                return CloudStorageAccount.Parse(connectionString).CreateCloudTableClient();
            }
            catch (FormatException)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Azure Table Storage connection string format is not valid" });
                throw;
            }
            catch (Exception ex)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Unable to create cloud table client.\n{ex}" });
                throw;
            }

        }

        private async Task<CloudTable> GetCloudTableAsync(string connectionString, string tableName)
        {
            var cloudTableClient = await GetCloudTableClientAsync(connectionString);
            return cloudTableClient.GetTableReference(tableName);
        }
    }
}
