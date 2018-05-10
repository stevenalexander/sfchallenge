﻿using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.ServiceFabric.Data;
using Common;
using Microsoft.ServiceFabric.Data.Collections;
using System.Security;
using MongoDB.Bson;

namespace Logger
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    public sealed class Logger : StatefulService
    {
        public const string QueueName = "toExport";
        private const string databaseName = "exchange";
        private const string collectionName = "trades";
        private ITradeLogger tradeLogger;
        private AutoResetEvent logReceivedEvent = new AutoResetEvent(true);

        public Logger(StatefulServiceContext context)
            : base(context)
        {
            Init();
        }

        public Logger(StatefulServiceContext context, IReliableStateManagerReplica reliableStateManagerReplica)
            : base(context, reliableStateManagerReplica)
        {
            Init();
        }

        private void Init()
        {
            ConfigurationPackage configPackage = this.Context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            String connectionString = configPackage.Settings.Sections["DB"].Parameters["MongoConnectionString"].Value;
            bool.TryParse(configPackage.Settings.Sections["DB"].Parameters["MongoEnableSSL"].Value, out var enableSsl);
            tradeLogger = MongoDBTradeLogger.Create(connectionString, enableSsl, databaseName, collectionName);
        }

        public async Task LogAsync(Trade trade)
        {
            IReliableConcurrentQueue<Trade> exportQueue =
             await this.StateManager.GetOrAddAsync<IReliableConcurrentQueue<Trade>>(QueueName);

            using (var tx = this.StateManager.CreateTransaction())
            {
                // Add trade to log queue
                logReceivedEvent.Set();
                await exportQueue.EnqueueAsync(tx, trade);
                await tx.CommitAsync();
            }
        }

        public async Task ClearAsync()
        {
            IReliableConcurrentQueue<Trade> exportQueue =
             await this.StateManager.GetOrAddAsync<IReliableConcurrentQueue<Trade>>(QueueName);

            using (var tx = this.StateManager.CreateTransaction())
            {
                // Clear the external log trade store
                await tradeLogger.ClearAsync(CancellationToken.None);

                // Clear the queue
                var t = Task.Run(async () =>
                {
                    while (exportQueue.Count > 0)
                    {
                        await exportQueue.TryDequeueAsync(tx);
                    }
                    await tx.CommitAsync();
                    return true;
                });
                var timeout = Task.Delay(TimeSpan.FromSeconds(60));
                await Task.WhenAny(t, timeout);
            }
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            IReliableConcurrentQueue<Trade> exportQueue =
             await this.StateManager.GetOrAddAsync<IReliableConcurrentQueue<Trade>>(QueueName);

            // Take each trade from the queue and insert
            // it into an external trade log store.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Wait to process until logs are received, check anyway if timeout occurs
                    if (exportQueue.Count < 1)
                    {
                        logReceivedEvent.WaitOne(TimeSpan.FromSeconds(5));
                    }
                }
                catch (FabricNotReadableException)
                {
                    // Fabric is not yet readable - this is a transient exception
                    // Backing off temporarily before retrying
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }
               
                using (var tx = this.StateManager.CreateTransaction())
                {
                    try
                    {
                        // This can be batched...
                        var result = await exportQueue.TryDequeueAsync(tx, cancellationToken);
                        if (result.HasValue)
                        {
                            var trade = result.Value;
                            await tradeLogger.InsertAsync(trade, cancellationToken);

                            await tx.CommitAsync();
                        }
                    }
                    catch (FabricNotPrimaryException)
                    {
                        // Attempted to perform write on a non
                        // primary replica.
                        ServiceEventSource.Current.ServiceMessage(this.Context, $"Fabric cannot perform write as it is not the primary replica");
                        return;
                    }
                    catch (FabricNotReadableException)
                    {
                        // Fabric is not yet readable - this is a transient exception
                        // Backing off temporarily before retrying
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new ServiceReplicaListener[]
            {
                new ServiceReplicaListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        return new WebHostBuilder()
                                    .UseKestrel()
                                    .ConfigureServices(
                                        services => services
                                            .AddSingleton<StatefulServiceContext>(serviceContext)
                                            .AddSingleton<IReliableStateManager>(this.StateManager)
                                            .AddSingleton<Logger>(this))
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseStartup<Startup>()
                                    //.UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.UseUniqueServiceUrl)
                                    .UseUrls(url)
                                    .Build();
                    }))
            };
        }
    }
}
