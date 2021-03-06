﻿using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using VowpalWabbit.Azure.Trainer.Data;
using VowpalWabbit.Azure.Trainer.Operations;
using VW.Serializer;

namespace VowpalWabbit.Azure.Trainer
{
    public sealed class LearnEventProcessorHost : IDisposable
    {
        private readonly TelemetryClient telemetry;

        private readonly object managementLock = new object();
        private TrainEventProcessorFactory trainProcessorFactory;
        private EventProcessorHost eventProcessorHost;
        private Learner trainer;
        private PerformanceCounters perfCounters;
        private SafeTimer perfUpdater;
        private DateTime? eventHubStartDateTimeUtc;

        public LearnEventProcessorHost()
        {
            this.telemetry = new TelemetryClient();
            
            // by default read from the beginning of Event Hubs event stream.
            this.eventHubStartDateTimeUtc = null;
        }

        public PerformanceCounters PerformanceCounters { get { return this.perfCounters; } }

        public DateTime LastStartDateTimeUtc { get; private set; }

        internal object InitialOffsetProvider(string partition)
        {
            string offset;
            if (this.trainer.State.Partitions.TryGetValue(partition, out offset))
                return offset;

            // either DateTime.UtcNow on reset or null if start the first time
            return this.eventHubStartDateTimeUtc;
        }

        public async Task StartAsync(OnlineTrainerSettingsInternal settings)
        {
            await this.SafeExecute(async () => await this.StartInternalAsync(settings));
        }

        public async Task StopAsync()
        {
            await this.SafeExecute(this.StopInternalAsync);
        }

        public async Task Restart(OnlineTrainerSettingsInternal settings)
        {
            await this.SafeExecute(async () => await this.RestartInternalAsync(settings));
        }

        public async Task ResetModelAsync(OnlineTrainerState state = null, byte[] model = null)
        {
            await this.SafeExecute(async () => await this.ResetInternalAsync(state, model));
        }

        public async Task CheckpointAsync()
        {
            await this.SafeExecute(async () => await this.trainProcessorFactory.LearnBlock.SendAsync(new CheckpointTriggerEvent()));
        }

        private Task SafeExecute(Func<Task> action)
        {
            try
            {
                // need to do a lock as child tasks are interleaving
                lock (this.managementLock)
                {
                    action().Wait(TimeSpan.FromMinutes(3));
                }
            }
            catch (Exception ex)
            {
                this.telemetry.TrackException(ex);
                throw ex;
            }

            return Task.FromResult(true);
        }
        
        private async Task ResetInternalAsync(OnlineTrainerState state = null, byte[] model = null)
        {
            if (this.trainer == null)
            {
                this.telemetry.TrackTrace("Online Trainer resetting skipped as trainer hasn't started yet.", SeverityLevel.Information);
                return;
            }

            var msg = "Online Trainer resetting";
            if (state != null)
                msg += "; state supplied";
            if (model != null)
                msg += "; model supplied";

            this.telemetry.TrackTrace(msg, SeverityLevel.Information);

            var settings = this.trainer.Settings;

            await this.StopInternalAsync();

            settings.ForceFreshStart = true;
            settings.CheckpointPolicy.Reset();

            await this.StartInternalAsync(settings, state, model);

            // make sure we store this fresh model, in case we die we don't loose the reset
            await this.trainProcessorFactory.LearnBlock.SendAsync(new CheckpointTriggerEvent());

            // delete the currently deployed model, so the clients don't use the hold one
            var latestModel = await this.trainer.GetLatestModelBlob();
            this.telemetry.TrackTrace($"Resetting client visible model: {latestModel.Uri}", SeverityLevel.Information);
            await latestModel.UploadFromByteArrayAsync(new byte[0], 0, 0);
        }

        private async Task RestartInternalAsync(OnlineTrainerSettingsInternal settings)
        {
            this.telemetry.TrackTrace("Online Trainer restarting", SeverityLevel.Information);

            await this.StopInternalAsync();

            // make sure we ignore previous events
            this.eventHubStartDateTimeUtc = DateTime.UtcNow;

            await this.StartInternalAsync(settings);
        }

        private async Task StartInternalAsync(OnlineTrainerSettingsInternal settings, OnlineTrainerState state = null, byte[] model = null)
        {
            this.LastStartDateTimeUtc = DateTime.UtcNow;
            this.perfCounters = new PerformanceCounters(settings.Metadata.ApplicationID);

            // setup trainer
            this.trainer = new Learner(settings, this.DelayedExampleCallback, this.perfCounters);

            if (settings.ForceFreshStart || model != null)
                this.trainer.FreshStart(state, model);
            else
                await this.trainer.FindAndResumeFromState();

            // setup factory
            this.trainProcessorFactory = new TrainEventProcessorFactory(settings, this.trainer, this.perfCounters);

            // setup host
            var serviceBusConnectionStringBuilder = new ServiceBusConnectionStringBuilder(settings.JoinedEventHubConnectionString);
            var joinedEventhubName = serviceBusConnectionStringBuilder.EntityPath;
            serviceBusConnectionStringBuilder.EntityPath = string.Empty;

            this.eventProcessorHost = new EventProcessorHost(settings.Metadata.ApplicationID, joinedEventhubName,
                EventHubConsumerGroup.DefaultGroupName, serviceBusConnectionStringBuilder.ToString(), settings.StorageConnectionString);

            await this.eventProcessorHost.RegisterEventProcessorFactoryAsync(
                this.trainProcessorFactory,
                new EventProcessorOptions { InitialOffsetProvider = this.InitialOffsetProvider });

            // don't perform too often
            this.perfUpdater = new SafeTimer(
                TimeSpan.FromMilliseconds(500),
                this.UpdatePerformanceCounters);

            this.telemetry.TrackTrace(
                "OnlineTrainer started",
                SeverityLevel.Information,
                new Dictionary<string, string>
                {
                { "CheckpointPolicy", settings.CheckpointPolicy.ToString() },
                { "VowpalWabbit", settings.Metadata.TrainArguments },
                { "ExampleTracing", settings.EnableExampleTracing.ToString() }
                });
        }

        private void UpdatePerformanceCounters()
        {
            lock (this.managementLock)
            {
                // make sure this is thread safe w.r.t reset/start/stop/...
                try
                {
                    this.trainer.UpdatePerformanceCounters();
                    this.trainProcessorFactory.UpdatePerformanceCounters();
                }
                catch (Exception ex)
                {
                    this.telemetry.TrackException(ex);
                }
            }
        }

        private async Task StopInternalAsync()
        {
            this.telemetry.TrackTrace("OnlineTrainer stopping", SeverityLevel.Verbose);

            if (this.perfUpdater != null)
            {
                this.perfUpdater.Stop(TimeSpan.FromMinutes(1));
                this.perfUpdater = null;
            }

            if (this.eventProcessorHost != null)
            {
                try
                {
                    await this.eventProcessorHost.UnregisterEventProcessorAsync();
                }
                catch (Exception ex)
                {
                    this.telemetry.TrackException(ex);
                }

                this.eventProcessorHost = null;
            }

            if (this.trainProcessorFactory != null)
            {
                // flushes the pipeline
                this.trainProcessorFactory.Dispose();
                this.trainProcessorFactory = null;
            }

            if (this.trainer != null)
            {
                this.trainer.Dispose();
                this.trainer = null;
            }

            if (this.perfCounters != null)
            {
                this.perfCounters.Dispose();
                this.perfCounters = null;
            }

            this.telemetry.TrackTrace("OnlineTrainer stopped", SeverityLevel.Verbose);
        }

        private void DelayedExampleCallback(VowpalWabbitJsonSerializer serializer)
        {
            try
            {
                this.perfCounters.Feature_Requests_Pending.IncrementBy(-1);

                var data = (PipelineData)serializer.UserContext;
                data.Example = serializer.CreateExamples();


                // fire and forget
                // must not block to avoid dead lock
                this.trainProcessorFactory.LearnBlock
                    .SendAsync(data)
                    .ContinueWith(async ret =>
                    {
                        if (!await ret)
                        {
                            this.telemetry.TrackTrace("Unable to enqueue delayed examples", SeverityLevel.Error);

                            // since we couldn't enqueue, need to dispose here
                            data.Example.Dispose();
                        }
                    });
            }
            catch (Exception e)
            {
                this.telemetry.TrackException(e);
            }
            finally
            {
                serializer.Dispose();
            }
        }

        public void Dispose()
        {
            try
            {
                this.StopAsync().Wait(TimeSpan.FromMinutes(1));
            }
            catch (Exception ex)
            {
                this.telemetry.TrackException(ex);
            }
        }
    }
}
