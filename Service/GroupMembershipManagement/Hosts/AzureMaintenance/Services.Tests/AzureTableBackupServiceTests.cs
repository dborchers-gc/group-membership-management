// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Entities.AzureMaintenance;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using Services.Contracts;
using Services.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Services.Tests
{
    [TestClass]
    public class AzureMaintenanceServiceTests
    {
        [TestMethod]
        public async Task TestMissingAzureMaintenanceSettings()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var backupSettings = new List<AzureMaintenance>();
            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            await azureTableBackupService.RunBackupServiceAsync();

            azureTableBackupRepository.Verify(x => x.GetEntitiesAsync(It.IsAny<IAzureMaintenance>()), Times.Never());
            azureTableBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Never());
            azureBlobBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Never());
            azureBlobBackupRepository.Verify(x => x.VerifyDeleteBackupAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<string>()), Times.Never());
        }

        [TestMethod]
        public async Task TestFirstBackup()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var backupSettings = new List<AzureMaintenance>()
            {
                new AzureMaintenance("tableOne", "sourceConnection", "destinationConnection", "Table", false, 7),
                new AzureMaintenance("tableOne", "sourceConnection", "destinationConnection", "Blob", false, 7)
            };

            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();
            var entities = new List<DynamicTableEntity>();
            for (int i = 0; i < 5; i++)
            {
                entities.Add(new DynamicTableEntity());
            }
            var backups = new List<BackupEntity>() { };

            azureTableBackupRepository.Setup(x => x.GetEntitiesAsync(It.IsAny<IAzureMaintenance>()))
                                        .ReturnsAsync(entities);
            azureTableBackupRepository.Setup(x => x.BackupEntitiesAsync(backupSettings[0], entities))
                                        .ReturnsAsync(new BackupResult { BackupTableName = "backupTableName", BackedUpTo = "table", RowCount = entities.Count });
            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[0]))
                                        .ReturnsAsync(backups);
            azureTableBackupRepository.Setup(x => x.GetLastestBackupResultTrackerAsync(It.IsAny<IAzureMaintenance>()))
                                        .ReturnsAsync((BackupResult)null);

            azureBlobBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[1]))
                                        .ReturnsAsync(backups);
            azureBlobBackupRepository.Setup(x => x.BackupEntitiesAsync(backupSettings[1], entities))
                                        .ReturnsAsync(new BackupResult("backupTableName", "blob", entities.Count));

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            await azureTableBackupService.RunBackupServiceAsync();

            azureTableBackupRepository.Verify(x => x.GetEntitiesAsync(It.IsAny<IAzureMaintenance>()), Times.Exactly(2));
            azureTableBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Once());
            azureBlobBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Once());
        }

        [TestMethod]
        public async Task TestBackupWithExistingBackupTables()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var backupSettings = new List<AzureMaintenance>()
            {
                new AzureMaintenance("tableOne", "sourceConnection", "destinationConnection", "Table", false, 7),
                new AzureMaintenance("tableOne", "sourceConnection", "destinationConnection", "Blob", false, 7)
            };

            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();
            var entities = new List<DynamicTableEntity>();
            for (int i = 0; i < 5; i++)
            {
                entities.Add(new DynamicTableEntity());
            }
            var backups = new List<BackupEntity> { new BackupEntity("backupTableName", "blob") };

            azureTableBackupRepository.Setup(x => x.GetEntitiesAsync(It.IsAny<IAzureMaintenance>()))
                                        .ReturnsAsync(entities);
            azureTableBackupRepository.Setup(x => x.BackupEntitiesAsync(backupSettings[0], entities))
                                        .ReturnsAsync(new BackupResult { BackupTableName = "backupTableName", BackedUpTo = "table", RowCount = entities.Count });
            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[0]))
                                        .ReturnsAsync(backups);
            azureTableBackupRepository.Setup(x => x.GetLastestBackupResultTrackerAsync(It.IsAny<IAzureMaintenance>()))
                                        .ReturnsAsync(new BackupResult { BackupTableName = "backupTableName", BackedUpTo = "table", RowCount = 1 });

            azureBlobBackupRepository.Setup(x => x.BackupEntitiesAsync(backupSettings[1], entities))
                                        .ReturnsAsync(new BackupResult { BackupTableName = "backupTableName", BackedUpTo = "blob", RowCount = entities.Count });
            azureBlobBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[1]))
                                        .ReturnsAsync(backups);

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            await azureTableBackupService.RunBackupServiceAsync();

            azureTableBackupRepository.Verify(x => x.GetEntitiesAsync(It.IsAny<IAzureMaintenance>()), Times.Exactly(2));
            azureTableBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Once());
            azureBlobBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Once());
        }

        [TestMethod]
        public async Task TestCleanupOnlyForTables()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var backupSettings = new List<AzureMaintenance>()
            {
                new AzureMaintenance("tableOne", "sourceConnection", "destinationConnection", "Table", true, 7),
                new AzureMaintenance("*", "otherSourceConnection", "otherDestinationConnection", "Table", true, 30)
            };

            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();
            var backups = new List<BackupEntity> { new BackupEntity("backupTableName", "blob") };

            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[0]))
                                        .ReturnsAsync(backups);
            azureTableBackupRepository.Setup(x => x.GetLastestBackupResultTrackerAsync(It.IsAny<IAzureMaintenance>()))
                                        .ReturnsAsync(new BackupResult { BackupTableName = "backupTableName", BackedUpTo = "table", RowCount = 1 });
            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[1]))
                                        .ReturnsAsync(backups);

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            await azureTableBackupService.RunBackupServiceAsync();

            azureTableBackupRepository.Verify(x => x.BackupEntitiesAsync(It.IsAny<IAzureMaintenance>(), It.IsAny<List<DynamicTableEntity>>()), Times.Exactly(0));
        }

        [TestMethod]
        public async Task TestBackupRetrieval()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var tableSource = "backupTableName";
            var blobSource = "backupBlobName";
            var backupSettings = new List<AzureMaintenance>()
            {
                new AzureMaintenance(tableSource, "sourceConnection", "destinationConnection", "Table", true, 7),
                new AzureMaintenance(blobSource, "otherSourceConnection", "otherDestinationConnection", "Blob", true, 30)
            };

            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();
            var backupsTable = new List<BackupEntity> { new BackupEntity(tableSource, "table") };
            var backupsBlob = new List<BackupEntity> { new BackupEntity(blobSource, "blob") };

            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[0]))
                                        .ReturnsAsync(backupsTable);

            azureBlobBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[1]))
                                        .ReturnsAsync(backupsBlob);

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            var requests = await azureTableBackupService.RetrieveBackupsAsync();

            azureTableBackupRepository.Verify(x => x.GetBackupsAsync(It.IsAny<AzureMaintenance>()), Times.Once());
            azureBlobBackupRepository.Verify(x => x.GetBackupsAsync(It.IsAny<AzureMaintenance>()), Times.Once());

            Assert.AreEqual(requests.Count, 2);
            Assert.AreEqual(requests[0].TableName, tableSource);
            Assert.AreEqual(requests[1].TableName, blobSource);
        }


        [TestMethod]
        public async Task TestReviewAndDelete()
        {
            var loggerMock = new Mock<ILoggingRepository>();
            loggerMock.Setup(x => x.LogMessageAsync(It.IsAny<LogMessage>(), VerbosityLevel.INFO, It.IsAny<string>(), It.IsAny<string>()));

            var tableSource = "backupTableName";
            var blobSource = "backupBlobName";
            var backupSettings = new List<AzureMaintenance>()
            {
                new AzureMaintenance(tableSource, "sourceConnection", "destinationConnection", "Table", false, 7),
                new AzureMaintenance(blobSource, "otherSourceConnection", "otherDestinationConnection", "Blob", true, 30)
            };

            var azureTableBackupRepository = new Mock<IAzureMaintenanceRepository>();
            var azureBlobBackupRepository = new Mock<IAzureStorageBackupRepository>();
            var backupsTable = new List<BackupEntity> { new BackupEntity(tableSource, "table") };
            var backupsBlob = new List<BackupEntity> { new BackupEntity(blobSource, "blob") };

            azureTableBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[0]))
                                        .ReturnsAsync(backupsTable);
            azureTableBackupRepository.Setup(x => x.VerifyDeleteBackupAsync(backupSettings[0], tableSource))
                                        .ReturnsAsync(true);

            azureBlobBackupRepository.Setup(x => x.GetBackupsAsync(backupSettings[1]))
                                        .ReturnsAsync(backupsBlob);
            azureBlobBackupRepository.Setup(x => x.VerifyDeleteBackupAsync(backupSettings[1], blobSource))
                                        .ReturnsAsync(false);

            var azureTableBackupService = new AzureMaintenanceService(backupSettings, loggerMock.Object, azureTableBackupRepository.Object, azureBlobBackupRepository.Object);
            var requests = await azureTableBackupService.RetrieveBackupsAsync();
            var tableRequest = requests[0];
            var blobRequest = requests[1];

            var tableResponse = await azureTableBackupService.ReviewAndDeleteAsync(tableRequest.BackupSetting, tableRequest.TableName);
            var blobResponse = await azureTableBackupService.ReviewAndDeleteAsync(blobRequest.BackupSetting, blobRequest.TableName);

            Assert.IsTrue(tableResponse);
            Assert.IsFalse(blobResponse);

            azureTableBackupRepository.Verify(x => x.DeleteBackupAsync(It.IsAny<AzureMaintenance>(), It.IsAny<string>()), Times.Once());
            azureTableBackupRepository.Verify(x => x.DeleteBackupTrackersAsync(It.IsAny<AzureMaintenance>(), It.IsAny<List<(string, string)>>()), Times.Once());
            azureBlobBackupRepository.Verify(x => x.DeleteBackupAsync(It.IsAny<AzureMaintenance>(), It.IsAny<string>()), Times.Exactly(0));
        }
    }
}
