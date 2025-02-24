﻿//-----------------------------------------------------------------------
// <copyright file="ShardedDaemonProcess.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2023 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Linq;
using Akka.Actor;
using Akka.Annotations;
using Akka.Event;
using Akka.Util.Internal;

namespace Akka.Cluster.Sharding
{
    internal class KeepAlivePinger : UntypedActor, IWithTimers
    {
        private sealed class Tick
        {
            public static Tick Instance { get; } = new();
            private Tick() { }
        }

        public string Name { get; }
        public string[] Identities { get; }
        public IActorRef ShardingRef { get; }
        public ShardedDaemonProcessSettings Settings { get; }

        public ITimerScheduler Timers { get; set; }

        public static Props Props(ShardedDaemonProcessSettings settings, string name, string[] identities, IActorRef shardingRef) =>
            Actor.Props.Create(() => new KeepAlivePinger(settings, name, identities, shardingRef));

        public KeepAlivePinger(ShardedDaemonProcessSettings settings, string name, string[] identities, IActorRef shardingRef)
        {
            Settings = settings;
            Name = name;
            Identities = identities;
            ShardingRef = shardingRef;
        }

        protected override void PreStart()
        {
            base.PreStart();

            TriggerStartAll();
            Context.System.Log.Debug("Starting Sharded Daemon Process KeepAlivePinger for [{0}], with ping interval [{1}]", 
                Name, Settings.KeepAliveInterval);
            Timers.StartPeriodicTimer("tick", Tick.Instance, Settings.KeepAliveInterval);
        }

        protected override void OnReceive(object message)
        {
            if (message is Tick _)
            {
                TriggerStartAll();
                Context.System.Log.Debug("Periodic ping sent to [{0}] processes", Identities.Length);
            }
        }

        private void TriggerStartAll() => Identities.ForEach(id => ShardingRef.Tell(new ShardRegion.StartEntity(id)));
    }

    internal sealed class MessageExtractor : HashCodeMessageExtractor
    {
        public MessageExtractor(int maxNumberOfShards)
            : base(maxNumberOfShards)
        { }

        public override string EntityId(object message) => (message as ShardingEnvelope)?.EntityId;
        public override object EntityMessage(object message) => (message as ShardingEnvelope)?.Message;
        public override string ShardId(object message) => message is ShardRegion.StartEntity se ? se.EntityId : EntityId(message);
    }

    /// <summary>
    /// <para>This extension runs a pre set number of actors in a cluster.</para>
    /// <para>
    /// The typical use case is when you have a task that can be divided in a number of workers, each doing a
    /// sharded part of the work, for example consuming the read side events from Akka Persistence through
    /// tagged events where each tag decides which consumer that should consume the event.
    /// </para>
    /// <para>Each named set needs to be started on all the nodes of the cluster on start up.</para>
    /// <para>
    /// The processes are spread out across the cluster, when the cluster topology changes the processes may be stopped
    /// and started anew on a new node to rebalance them.
    /// </para>
    /// <para>Not for user extension.</para>
    /// </summary>
    [DoNotInherit, ApiMayChange]
    public class ShardedDaemonProcess : IExtension
    {
        private readonly ExtendedActorSystem _system;

        public ShardedDaemonProcess(ExtendedActorSystem system) => _system = system;

        public static ShardedDaemonProcess Get(ActorSystem system) =>
            system.WithExtension<ShardedDaemonProcess, ShardedDaemonProcessExtensionProvider>();

        /// <summary>
        /// Start a specific number of actors that is then kept alive in the cluster.
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="numberOfInstances">TBD</param>
        /// <param name="propsFactory">Given a unique id of `0` until `numberOfInstance` create an entity actor.</param>
        public void Init(string name, int numberOfInstances, Func<int, Props> propsFactory) => 
            Init(name, numberOfInstances, propsFactory, ShardedDaemonProcessSettings.Create(_system), null);

        /// <summary>
        /// Start a specific number of actors that is then kept alive in the cluster.
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="numberOfInstances">TBD</param>
        /// <param name="propsFactory">Given a unique id of `0` until `numberOfInstance` create an entity actor.</param>
        /// <param name="stopMessage">Sent to the actors when they need to stop because of a rebalance across the nodes of the cluster or cluster shutdown.</param>
        public void Init(string name, int numberOfInstances, Func<int, Props> propsFactory, object stopMessage) => 
            Init(name, numberOfInstances, propsFactory, ShardedDaemonProcessSettings.Create(_system), stopMessage);

        /// <summary>
        /// Start a specific number of actors, each with a unique numeric id in the set, that is then kept alive in the cluster.
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="numberOfInstances">TBD</param>
        /// <param name="propsFactory">Given a unique id of `0` until `numberOfInstance` create an entity actor.</param>
        /// <param name="settings">TBD</param>
        /// <param name="stopMessage">If defined sent to the actors when they need to stop because of a rebalance across the nodes of the cluster or cluster shutdown.</param>
        public void Init(string name, int numberOfInstances, Func<int, Props> propsFactory, ShardedDaemonProcessSettings settings, object stopMessage)
        {
            // One shard per actor identified by the numeric id encoded in the entity id
            var numberOfShards = numberOfInstances;
            var entityIds = Enumerable.Range(0, numberOfInstances).Select(i => i.ToString()).ToArray();

            // Defaults in `akka.cluster.sharding` but allow overrides specifically for sharded-daemon-process
            var shardingBaseSettings = settings.ShardingSettings;
            if (shardingBaseSettings == null)
            {
                var shardingConfig = _system.Settings.Config.GetConfig("akka.cluster.sharded-daemon-process.sharding");
                var coordinatorSingletonConfig = _system.Settings.Config.GetConfig(shardingConfig.GetString("coordinator-singleton"));
                shardingBaseSettings = ClusterShardingSettings.Create(shardingConfig, coordinatorSingletonConfig);
            }

            var shardingSettings = new ClusterShardingSettings(
                settings.Role ?? shardingBaseSettings.Role,
                false, // remember entities disabled
                "",
                "",
                TimeSpan.Zero, // passivation disabled
                StateStoreMode.DData,
                shardingBaseSettings.TuningParameters,
                shardingBaseSettings.CoordinatorSingletonSettings, 
                shardingBaseSettings.LeaseSettings);

            if (string.IsNullOrEmpty(shardingSettings.Role) || Cluster.Get(_system).SelfRoles.Contains(shardingSettings.Role))
            {
                var sharding = ClusterSharding.Get(_system);
                var shardingRef = sharding.Start(
                    typeName: $"sharded-daemon-process-{name}",
                    entityPropsFactory: entityId => propsFactory(int.Parse(entityId)),
                    settings: shardingSettings,
                    messageExtractor: new MessageExtractor(numberOfShards),
                    allocationStrategy: sharding.DefaultShardAllocationStrategy(shardingSettings), 
                    stopMessage ?? PoisonPill.Instance);

                _system.ActorOf(
                    KeepAlivePinger.Props(settings, name, entityIds, shardingRef),
                    $"ShardedDaemonProcessKeepAlive-{name}");
            }
        }
    }

    public class ShardedDaemonProcessExtensionProvider : ExtensionIdProvider<ShardedDaemonProcess>
    {
        public override ShardedDaemonProcess CreateExtension(ExtendedActorSystem system) => new(system);
    }
}
