﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Export;
using Microsoft.Health.Fhir.Core.Features.Operations.Export.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.JobManagement;
using Microsoft.Health.JobManagement.UnitTests;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.Export
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Export)]
    public class ExportProcessingJobTests
    {
        private readonly string _progressToken = "progress token";

        [Fact]
        public async Task GivenAnExportJob_WhenItSucceeds_ThenOutputsAreInTheResult()
        {
            string progressResult = string.Empty;

            Progress<string> progress = new Progress<string>((result) =>
            {
                progressResult = result;
            });

            var expectedResults = GenerateJobRecord(OperationStatus.Completed);

            var processingJob = new ExportProcessingJob(MakeMockJob, new TestQueueClient());
            var taskResult = await processingJob.ExecuteAsync(GenerateJobInfo(expectedResults), progress, CancellationToken.None);
            Assert.Equal(expectedResults, taskResult);

            // For some reason checking the progress result is flaky. Sometimes it passes and sometimes it is blank. There seems to be a timing error, but debugging it causes it not to happen.
            // All the steps are synchronous, so I don't see where the issue is happening.
            // Assert.Equal(expectedResults, progressResult);
        }

        [Fact]
        public async Task GivenAnExportJob_WhenItFails_ThenAnExceptionIsThrown()
        {
            Progress<string> progress = new Progress<string>((result) => { });

            var exceptionMessage = "Test job failed";
            var expectedResults = GenerateJobRecord(OperationStatus.Failed, exceptionMessage);

            var processingJob = new ExportProcessingJob(new Func<IExportJobTask>(MakeMockJob), new TestQueueClient());
            var exception = await Assert.ThrowsAsync<JobExecutionException>(() => processingJob.ExecuteAsync(GenerateJobInfo(expectedResults), progress, CancellationToken.None));
            Assert.Equal(exceptionMessage, exception.Message);
        }

        [Fact]
        public async Task GivenAnExportJob_WhenItIsCancelled_ThenAnExceptionIsThrown()
        {
            Progress<string> progress = new Progress<string>((result) => { });

            var expectedResults = GenerateJobRecord(OperationStatus.Canceled);

            var processingJob = new ExportProcessingJob(new Func<IExportJobTask>(MakeMockJob), new TestQueueClient());
            await Assert.ThrowsAsync<RetriableJobException>(() => processingJob.ExecuteAsync(GenerateJobInfo(expectedResults), progress, CancellationToken.None));
        }

        [Theory]
        [InlineData(OperationStatus.Queued)]
        [InlineData(OperationStatus.Running)]
        public async Task GivenAnExportJob_WhenItFinishesInANonTerminalState_ThenAnExceptionIsThrown(OperationStatus status)
        {
            Progress<string> progress = new Progress<string>((result) => { });

            var expectedResults = GenerateJobRecord(status);

            var processingJob = new ExportProcessingJob(new Func<IExportJobTask>(MakeMockJobThatReturnsImmediately), new TestQueueClient());
            await Assert.ThrowsAsync<RetriableJobException>(() => processingJob.ExecuteAsync(GenerateJobInfo(expectedResults), progress, CancellationToken.None));
        }

        [Fact]
        public async Task GivenAnExportJob_WhenItFinishesAPageOfResults_ThenANewProgressJobIsQueued()
        {
            string progressResult = string.Empty;

            Progress<string> progress = new Progress<string>((result) =>
            {
                progressResult = result;
            });

            var expectedResults = GenerateJobRecord(OperationStatus.Running);

            var queueClient = new TestQueueClient();
            var processingJob = new ExportProcessingJob(MakeMockJobWithProgressUpdate, queueClient);
            var taskResult = await processingJob.ExecuteAsync(GenerateJobInfo(expectedResults), progress, CancellationToken.None);

            Assert.Single(queueClient.JobInfos);
            Assert.Contains(_progressToken, queueClient.JobInfos[0].Definition);
        }

        [Fact]
        public async Task GivenAnExportJob_WhenItFinishesAPageOfResultsAndAFollowupJobExists_ThenANewProgressJobIsNotQueued()
        {
            string progressResult = string.Empty;

            Progress<string> progress = new Progress<string>((result) =>
            {
                progressResult = result;
            });

            var expectedResults = GenerateJobRecord(OperationStatus.Running);

            var queueClient = new TestQueueClient();
            var processingJob = new ExportProcessingJob(MakeMockJobWithProgressUpdate, queueClient);

            var runningJob = GenerateJobInfo(expectedResults);
            var followUpJob = GenerateJobInfo(expectedResults);
            followUpJob.Id = runningJob.Id + 1;
            queueClient.JobInfos.Add(followUpJob);

            var taskResult = await processingJob.ExecuteAsync(runningJob, progress, CancellationToken.None);

            Assert.Single(queueClient.JobInfos);
            Assert.DoesNotContain(_progressToken, queueClient.JobInfos[0].Definition);
            Assert.Equal(followUpJob, queueClient.JobInfos[0]);
        }

        [Theory]
        [InlineData("Patient", "Observation", null, null)]
        [InlineData(null, null, "range1", "range2")]
        public async Task GivenAnExportJob_WhenItFinishesAPageAndNewerParallelJobExists_ThenANewProgressJobIsQueued(string testRunningJobResourceType, string laterParallelJobResourceType, string testRunningJobFeedRange, string laterParallelJobFeedRange)
        {
            string progressResult = string.Empty;

            Progress<string> progress = new Progress<string>((result) =>
            {
                progressResult = result;
            });

            var queueClient = new TestQueueClient();
            var processingJob = new ExportProcessingJob(MakeMockJobWithProgressUpdate, queueClient);

            // Note: Feed ranges are different which means testRunningJob should queue the next job even though.
            var testRunningJob = GenerateJobInfo(GenerateJobRecord(OperationStatus.Running, resourceType: testRunningJobResourceType, feedRange: testRunningJobFeedRange));
            var laterParallelRunningJob = GenerateJobInfo(GenerateJobRecord(OperationStatus.Running, resourceType: laterParallelJobResourceType, feedRange: laterParallelJobFeedRange));

            testRunningJob.Id = 1;
            laterParallelRunningJob.Id = 2;
            queueClient.JobInfos.Add(laterParallelRunningJob);

            await processingJob.ExecuteAsync(testRunningJob, progress, CancellationToken.None);

            Assert.True(queueClient.JobInfos.Count == 2); // laterParallelRunningJob + follow up job for testRunningJob.
            Assert.Equal(laterParallelRunningJob, queueClient.JobInfos[0]);

            var followUpJobDefinition = queueClient.JobInfos[1].DeserializeDefinition<ExportJobRecord>();

            Assert.Equal(testRunningJobResourceType, followUpJobDefinition.ResourceType);
            Assert.Equal(testRunningJobFeedRange, followUpJobDefinition.FeedRange);
            Assert.Contains(_progressToken, queueClient.JobInfos[1].Definition); // This is a follow up job, not our orig testRunningJob.
        }

        [Theory]
        [InlineData(JobStatus.Cancelled, false)]
        [InlineData(JobStatus.Running, true)]
        public async Task GivenAnExportJob_WhenItFinishesAPageOfResultAndCanceledGroupJobInQueue_ThenANewProgressJobIsNotQueued(JobStatus existingJobStatus, bool cancellationRequested)
        {
            string progressResult = string.Empty;

            Progress<string> progress = new Progress<string>((result) =>
            {
                progressResult = result;
            });

            var queueClient = new TestQueueClient();
            var processingJob = new ExportProcessingJob(MakeMockJobWithProgressUpdate, queueClient);

            var runningJob = GenerateJobInfo(GenerateJobRecord(OperationStatus.Running));

            var existingCanceledJob = GenerateJobInfo(GenerateJobRecord(OperationStatus.Running));
            existingCanceledJob.Id = runningJob.Id - 1;
            existingCanceledJob.Status = existingJobStatus;
            existingCanceledJob.CancelRequested = cancellationRequested;
            queueClient.JobInfos.Add(existingCanceledJob);

            var taskResult = await processingJob.ExecuteAsync(runningJob, progress, CancellationToken.None);

            Assert.True(queueClient.JobInfos.Count == 1);
            Assert.DoesNotContain(_progressToken, queueClient.JobInfos[0].Definition);
            Assert.Equal(existingCanceledJob, queueClient.JobInfos[0]);
        }

        private string GenerateJobRecord(OperationStatus status, string failureReason = null, string resourceType = null, string feedRange = null)
        {
            var record = new ExportJobRecord(
                requestUri: new Uri("https://localhost/ExportJob/"),
                exportType: ExportJobType.All,
                exportFormat: ExportFormatTags.ResourceName,
                resourceType: resourceType,
                filters: null,
                hash: "hash",
                rollingFileSizeInMB: 0,
                feedRange: feedRange);
            record.Status = status;
            if (failureReason != null)
            {
                record.FailureDetails = new JobFailureDetails(failureReason, HttpStatusCode.InternalServerError);
            }

            record.Id = string.Empty;
            return JsonConvert.SerializeObject(record);
        }

        private JobInfo GenerateJobInfo(string record)
        {
            var info = new JobInfo();
            info.Id = RandomNumberGenerator.GetInt32(int.MaxValue);
            info.Definition = record;
            return info;
        }

        private IExportJobTask MakeMockJob()
        {
            var mockJob = Substitute.For<IExportJobTask>();
            mockJob.ExecuteAsync(Arg.Any<ExportJobRecord>(), Arg.Any<WeakETag>(), Arg.Any<CancellationToken>()).Returns(x =>
            {
                return mockJob.UpdateExportJob(x.ArgAt<ExportJobRecord>(0), x.ArgAt<WeakETag>(1), x.ArgAt<CancellationToken>(2));
            });

            return mockJob;
        }

        private IExportJobTask MakeMockJobThatReturnsImmediately()
        {
            var mockJob = Substitute.For<IExportJobTask>();
            mockJob.ExecuteAsync(Arg.Any<ExportJobRecord>(), Arg.Any<WeakETag>(), Arg.Any<CancellationToken>()).Returns(x =>
            {
                return Task.FromResult(new ExportJobOutcome(x.ArgAt<ExportJobRecord>(0), x.ArgAt<WeakETag>(1)));
            });

            return mockJob;
        }

        private IExportJobTask MakeMockJobWithProgressUpdate()
        {
            var mockJob = Substitute.For<IExportJobTask>();
            mockJob.ExecuteAsync(Arg.Any<ExportJobRecord>(), Arg.Any<WeakETag>(), Arg.Any<CancellationToken>()).Returns(async x =>
            {
                var record = x.ArgAt<ExportJobRecord>(0);
                record.Progress = new ExportJobProgress(_progressToken, 1);
                try
                {
                    await mockJob.UpdateExportJob(record, x.ArgAt<WeakETag>(1), x.ArgAt<CancellationToken>(2));
                }
                catch (JobSegmentCompletedException)
                {
                    record.Status = OperationStatus.Completed;
                    await mockJob.UpdateExportJob(record, x.ArgAt<WeakETag>(1), x.ArgAt<CancellationToken>(2));
                }
            });

            return mockJob;
        }
    }
}
