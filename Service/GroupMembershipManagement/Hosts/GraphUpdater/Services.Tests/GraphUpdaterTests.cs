// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Hosts.GraphUpdater;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using Services.Tests.Mocks;
using Repositories.MembershipDifference;
using Entities;
using Repositories.Mocks;
using Repositories.Contracts.InjectConfig;
using System.Threading.Tasks;

namespace Services.Tests
{
    [TestClass]
	public class GraphUpdaterTests
	{
		[TestMethod]
		public async Task AccumulatesMessagesAndUpdatesGraphAsync()
		{
			var mockUpdater = new MockGraphUpdater();
			var mockLogs = new MockLoggingRepository();
			var sessionCollector = new SessionMessageCollector(mockUpdater, mockLogs);

			var mockSession = new MockMessageSession()
			{
				SessionId = "someId"
			};
			var sessionId = "someId";

			var incomingMessages = MakeMembershipMessages();

			foreach (var message in incomingMessages.SkipLast(1))
			{
				var result = await sessionCollector.HandleNewMessageAsync(message, sessionId);

				// sessionCollector doesn't do anything until it gets the last message.
				Assert.AreEqual(0, mockUpdater.Actual.Count);
				Assert.IsFalse(mockSession.Closed);
				Assert.AreEqual(0, mockSession.CompletedLockTokens.Count);
				Assert.AreEqual(false, result.ShouldCompleteMessage);
			}

			var groupMembershipMessageResponse = await sessionCollector.HandleNewMessageAsync(incomingMessages.Last(), sessionId);

			Assert.IsFalse(mockSession.Closed);
			Assert.IsTrue(groupMembershipMessageResponse.ShouldCompleteMessage);
			Assert.AreEqual(1, mockUpdater.Actual.Count);
			var mergedMembership = mockUpdater.Actual.Single();

			for (int i = 0; i < incomingMessages.Length; i++)
			{
				var currentBody = incomingMessages[i].Body;
				Assert.AreEqual(currentBody.SyncJobRowKey, mergedMembership.SyncJobRowKey);
				Assert.AreEqual(currentBody.SyncJobPartitionKey, mergedMembership.SyncJobPartitionKey);
				Assert.AreEqual(currentBody.RunId, mergedMembership.RunId);
				Assert.AreEqual(currentBody.Destination, mergedMembership.Destination);
			}
		}

		[TestMethod]
		public async Task IgnoresMissingDestinationGroupAsync()
		{
			var mockGroups = new MockGraphGroupRepository();
			var mockSyncJobs = new MockSyncJobRepository();
			var mockLogs = new MockLoggingRepository();
			var mockMails = new MockMailRepository();
			var mockEmail = new MockEmail<IEmailSenderRecipient>();
			var mockDryRun = new MockDryRun<IDryRunValue>();
			var updater = new GraphUpdaterApplication(new MembershipDifferenceCalculator<AzureADUser>(), mockSyncJobs, mockLogs, mockMails, mockGroups, mockEmail, mockDryRun);
			var sessionCollector = new SessionMessageCollector(updater, mockLogs);

			var mockSession = new MockMessageSession()
			{
				SessionId = "someId"
			};
			var sessionId = "someId";

			var syncJobKeys = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

			var syncJob = new SyncJob(syncJobKeys.Item1, syncJobKeys.Item2)
			{
				Enabled = true,
				Status = "InProgress",
			};
			mockSyncJobs.ExistingSyncJobs.Add(syncJobKeys, syncJob);

			var incomingMessages = MakeMembershipMessages();

			foreach (var message in incomingMessages)
			{
				message.Body.SyncJobPartitionKey = syncJobKeys.Item1;
				message.Body.SyncJobRowKey = syncJobKeys.Item2;
			}

			var expectedLogs = 0;
			foreach (var message in incomingMessages.SkipLast(1))
			{
				var result = await sessionCollector.HandleNewMessageAsync(message, sessionId);

				// sessionCollector doesn't do anything until it gets the last message.
				expectedLogs += 2;
				Assert.AreEqual(expectedLogs, mockLogs.MessagesLoggedCount);
				Assert.IsFalse(mockSession.Closed);
				Assert.AreEqual(0, mockSession.CompletedLockTokens.Count);
				Assert.IsFalse(result.ShouldCompleteMessage);
			}

			var groupMembershipMessageResponse = await sessionCollector.HandleNewMessageAsync(incomingMessages.Last(), sessionId);

			Assert.IsFalse(mockSession.Closed);
			Assert.AreEqual(expectedLogs + 8, mockLogs.MessagesLoggedCount);
			Assert.AreEqual("Error", syncJob.Status);
			Assert.IsFalse(syncJob.Enabled);
			Assert.AreEqual(0, mockGroups.GroupsToUsers.Count);
			Assert.IsTrue(groupMembershipMessageResponse.ShouldCompleteMessage);
		}

		[TestMethod]
		public async Task SyncsGroupsCorrectlyAsync()
		{
			var mockGroups = new MockGraphGroupRepository();
			var mockSyncJobs = new MockSyncJobRepository();
			var mockLogs = new MockLoggingRepository();
			var mockMails = new MockMailRepository();
			var mockEmail = new MockEmail<IEmailSenderRecipient>();
			var mockDryRun = new MockDryRun<IDryRunValue>();
			var updater = new GraphUpdaterApplication(new MembershipDifferenceCalculator<AzureADUser>(), mockSyncJobs, mockLogs, mockMails, mockGroups, mockEmail, mockDryRun);
			var sessionCollector = new SessionMessageCollector(updater, mockLogs);

			var mockSession = new MockMessageSession()
			{
				SessionId = "someId"
			};
			var sessionId = "someId";

			var syncJobKeys = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

			var syncJob = new SyncJob(syncJobKeys.Item1, syncJobKeys.Item2)
			{
				LastRunTime = DateTime.FromFileTimeUtc(0),
				Enabled = true,
				Status = "InProgress",
			};
			mockSyncJobs.ExistingSyncJobs.Add(syncJobKeys, syncJob);

			var incomingMessages = MakeMembershipMessages();

			mockGroups.GroupsToUsers.Add(incomingMessages.First().Body.Destination.ObjectId, new List<AzureADUser>() { new AzureADUser { ObjectId = Guid.NewGuid() } });

			foreach (var message in incomingMessages)
			{
				message.Body.SyncJobPartitionKey = syncJobKeys.Item1;
				message.Body.SyncJobRowKey = syncJobKeys.Item2;
			}

			var expectedLogs = 0;
			foreach (var message in incomingMessages.SkipLast(1))
			{
				var result = await sessionCollector.HandleNewMessageAsync(message, sessionId);

				// sessionCollector doesn't do anything until it gets the last message.
				expectedLogs += 2;
				Assert.AreEqual(expectedLogs, mockLogs.MessagesLoggedCount);
				Assert.IsFalse(mockSession.Closed);
				Assert.AreEqual(0, mockSession.CompletedLockTokens.Count);
				Assert.IsFalse(result.ShouldCompleteMessage);
			}

			var groupMembershipMessageResponse = await sessionCollector.HandleNewMessageAsync(incomingMessages.Last(), sessionId);

			Assert.IsFalse(mockSession.Closed);
			Assert.AreEqual(expectedLogs + 10, mockLogs.MessagesLoggedCount);
			Assert.IsTrue(groupMembershipMessageResponse.ShouldCompleteMessage);
			Assert.AreEqual("Idle", syncJob.Status);
			Assert.IsTrue(syncJob.Enabled);
			Assert.AreEqual(1, mockGroups.GroupsToUsers.Count);
			Assert.AreEqual(MockGroupMembershipHelper.UserCount, mockGroups.GroupsToUsers.Values.Single().Count);
		}


		private class MockDryRun<T> : IDryRunValue
		{
			public bool DryRunEnabled => false;
		}

		public GroupMembershipMessage[] MakeMembershipMessages()
		{
			int messageNumber = 0;
			return MockGroupMembershipHelper.MockGroupMembership().Split().Select(x => new GroupMembershipMessage
			{
				Body = x,
				LockToken = (messageNumber++).ToString()
			}).ToArray();
		}
	}
}
