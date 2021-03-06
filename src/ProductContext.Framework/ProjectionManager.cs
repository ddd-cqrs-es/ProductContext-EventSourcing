﻿using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AggregateSource.EventStore;

using EventStore.ClientAPI;

using Newtonsoft.Json;

using ProductContext.Framework.Logging;

using Projac;

namespace ProductContext.Framework
{
    public class ProjectionManager<TConnection>
    {
        private static readonly ILog Log = LogProvider.For<ProjectionManager<TConnection>>();
        private readonly ICheckpointStore _checkpointStore;
        private readonly IEventStoreConnection _connection;
        private readonly Func<TConnection> _getConnection;
        private readonly int _maxLiveQueueSize;
        private readonly ProjectorDefiner[] _projectorDefiners;
        private readonly int _readBatchSize;
        private readonly IEventDeserializer _serializer;
        private readonly ISnapshotter[] _snapshotters;

        internal ProjectionManager(IEventStoreConnection connection,
            IEventDeserializer serializer,
            Func<TConnection> getConnection,
            ICheckpointStore checkpointStore,
            ProjectorDefiner[] projections,
            ISnapshotter[] snapshotters,
            int? maxLiveQueueSize,
            int? readBatchSize)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _getConnection = getConnection;
            _projectorDefiners = projections;
            _snapshotters = snapshotters;
            _checkpointStore = checkpointStore;
            _maxLiveQueueSize = maxLiveQueueSize ?? 10000;
            _readBatchSize = readBatchSize ?? 500;
        }

        public Task Activate() => Task.WhenAll(_projectorDefiners.Select(x => StartProjection(x.GetProjectionName(), x.Build<TConnection>())));

        private async Task StartProjection(string projectionName, Projector<TConnection> projector)
        {
            Position lastCheckpoint = await _checkpointStore.GetLastCheckpoint<Position>(projectionName);

            var settings = new CatchUpSubscriptionSettings(
                _maxLiveQueueSize,
                _readBatchSize,
                Log.IsTraceEnabled(),
                false,
                projectionName
            );

            _connection.SubscribeToAllFrom(
                lastCheckpoint,
                settings,
                EventAppeared(projector, projectionName),
                LiveProcessingStarted(projector, projectionName),
                SubscriptionDropped(projector, projectionName));
        }

        private static object ComposeEnvelope(object @event, long position) => Activator.CreateInstance(typeof(Envelope<>).MakeGenericType(@event.GetType()), @event, position);

        private Action<EventStoreCatchUpSubscription, ResolvedEvent> EventAppeared(
            Projector<TConnection> projection,
            string projectionName
        ) => async (_, e) =>
        {
            // check system event
            if (e.OriginalEvent.EventType.StartsWith("$")) { return; }

            // get the configured clr type name for deserializing the event
            //var eventType = _typeMapper.GetType(e.Event.EventType);

            // try to execute the projection
            await projection.ProjectAsync(_getConnection(), ComposeEnvelope(_serializer.Deserialize(e), e.OriginalPosition.Value.CommitPosition));

            Log.Debug("{projection} projected {eventType}({eventId})", projectionName, e.Event.EventType, e.Event.EventId);

            //**** Storing Checkpoint ****/
            //1.Way
            // await projection.ProjectAsync(_getConnection(), new SetProjectionPosition(e.OriginalPosition));
            //2.Way
            await _checkpointStore.SetLastCheckpoint(projectionName, e.OriginalPosition);

            var metadata = JsonConvert.DeserializeObject<EventMetadata>(Encoding.UTF8.GetString(e.Event.Metadata));
            ISnapshotter snapshotter = _snapshotters.FirstOrDefault(
                x => x.ShouldTakeSnapshot(Type.GetType(metadata.AggregateAssemblyQualifiedName), e) && !metadata.IsSnapshot);

            if (snapshotter != null)
            {
                await snapshotter.Take(e.OriginalStreamId);

                Log.Debug("Snapshot was taken for {aggregate} on event{eventType}{eventId} at number {eventNumber}",
                    metadata.AggregateType, e.Event.EventType, e.Event.EventId, e.Event.EventNumber);
            }
        };

        private Action<EventStoreCatchUpSubscription, SubscriptionDropReason, Exception> SubscriptionDropped(Projector<TConnection> projection, string projectionName)
            => (subscription, reason, ex) =>
            {
                // TODO: Reevaluate stopping subscriptions when issues with reconnect get fixed.
                // https://github.com/EventStore/EventStore/issues/1127
                // https://groups.google.com/d/msg/event-store/AdKzv8TxabM/VR7UDIRxCgAJ

                subscription.Stop();

                switch (reason)
                {
                    case SubscriptionDropReason.UserInitiated:
                        Log.Debug("{projection} projection stopped gracefully.", projection);
                        break;
                    case SubscriptionDropReason.SubscribingError:
                    case SubscriptionDropReason.ServerError:
                    case SubscriptionDropReason.ConnectionClosed:
                    case SubscriptionDropReason.CatchUpError:
                    case SubscriptionDropReason.ProcessingQueueOverflow:
                    case SubscriptionDropReason.EventHandlerException:
                        Log.ErrorException(
                            "{projection} projection stopped because of a transient error ({reason}). " +
                            "Attempting to restart...",
                            ex, projectionName, reason);
                        Task.Run(() => StartProjection(subscription.SubscriptionName, projection));
                        break;
                    default:
                        Log.FatalException(
                            "{projection} projection stopped because of an internal error ({reason}). " +
                            "Please check your logs for details.",
                            ex, projectionName, reason);
                        break;
                }
            };

        private static Action<EventStoreCatchUpSubscription> LiveProcessingStarted(Projector<TConnection> projection, string projectionName)
            => _ => { Log.Debug("{projection} projection has caught up, now processing live!", projectionName); };
    }
}
