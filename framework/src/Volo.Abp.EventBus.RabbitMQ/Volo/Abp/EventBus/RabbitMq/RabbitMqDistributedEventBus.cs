﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.RabbitMQ;
using Volo.Abp.Threading;

namespace Volo.Abp.EventBus.RabbitMq
{
    /* TODO: How to handle unsubscribe to unbind on RabbitMq (may not be possible for)
     * TODO: Implement Retry system
     * TODO: Should be improved
     */
    [Dependency(ReplaceServices = true)]
    [ExposeServices(typeof(IDistributedEventBus), typeof(RabbitMqDistributedEventBus))]
    public class RabbitMqDistributedEventBus : EventBusBase, IDistributedEventBus, ISingletonDependency
    {
        protected RabbitMqEventBusOptions RabbitMqEventBusOptions { get; }
        protected DistributedEventBusOptions DistributedEventBusOptions { get; }
        protected IConnectionPool ConnectionPool { get; }
        protected IRabbitMqSerializer Serializer { get; }
        
        //TODO: Accessing to the List<IEventHandlerFactory> may not be thread-safe!
        protected ConcurrentDictionary<Type, List<IEventHandlerFactory>> HandlerFactories { get; }
        protected ConcurrentDictionary<string, Type> EventTypes { get; }
        protected IRabbitMqMessageConsumerFactory MessageConsumerFactory { get; }
        protected IRabbitMqMessageConsumer Consumer { get; }

        public RabbitMqDistributedEventBus(
            IOptions<RabbitMqEventBusOptions> options,
            IConnectionPool connectionPool,
            IRabbitMqSerializer serializer,
            IHybridServiceScopeFactory serviceScopeFactory, 
            IOptions<DistributedEventBusOptions> distributedEventBusOptions,
            IRabbitMqMessageConsumerFactory messageConsumerFactory)
            : base(serviceScopeFactory)
        {
            ConnectionPool = connectionPool;
            Serializer = serializer;
            MessageConsumerFactory = messageConsumerFactory;
            DistributedEventBusOptions = distributedEventBusOptions.Value;
            RabbitMqEventBusOptions = options.Value;
            
            HandlerFactories = new ConcurrentDictionary<Type, List<IEventHandlerFactory>>();
            EventTypes = new ConcurrentDictionary<string, Type>();

            Consumer = MessageConsumerFactory.Create(
                new ExchangeDeclareConfiguration(
                    RabbitMqEventBusOptions.ExchangeName, 
                    type: "direct",
                    durable: true
                    ),
                new QueueDeclareConfiguration(
                    RabbitMqEventBusOptions.ClientName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false
                    ),
                RabbitMqEventBusOptions.ConnectionName
            );

            Consumer.OnMessageReceived(ProcessEventAsync);

            SubscribeHandlers(DistributedEventBusOptions.Handlers);
        }

        private async Task ProcessEventAsync(IModel channel, BasicDeliverEventArgs ea)
        {
            var eventName = ea.RoutingKey;
            var eventType = EventTypes.GetOrDefault(eventName);
            if (eventType == null)
            {
                return;
            }

            var eventData = Serializer.Deserialize(ea.Body, eventType);

            await TriggerHandlersAsync(eventType, eventData);
        }

        public IDisposable Subscribe<TEvent>(IDistributedEventHandler<TEvent> handler) where TEvent : class
        {
            return Subscribe(typeof(TEvent), handler);
        }

        public override IDisposable Subscribe(Type eventType, IEventHandlerFactory factory)
        {
            var handlerFactories = GetOrCreateHandlerFactories(eventType);
            
            handlerFactories.Add(factory);

            if (handlerFactories.Count == 1) //TODO: Multi-threading!
            {
                Consumer.BindAsync(EventNameAttribute.GetNameOrDefault(eventType));
            }

            return new EventHandlerFactoryUnregistrar(this, eventType, factory);
        }

        /// <inheritdoc/>
        public override void Unsubscribe<TEvent>(Func<TEvent, Task> action)
        {
            Check.NotNull(action, nameof(action));

            GetOrCreateHandlerFactories(typeof(TEvent))
                .Locking(factories =>
                {
                    factories.RemoveAll(
                        factory =>
                        {
                            var singleInstanceFactory = factory as SingleInstanceHandlerFactory;
                            if (singleInstanceFactory == null)
                            {
                                return false;
                            }

                            var actionHandler = singleInstanceFactory.HandlerInstance as ActionEventHandler<TEvent>;
                            if (actionHandler == null)
                            {
                                return false;
                            }

                            return actionHandler.Action == action;
                        });
                });
        }

        /// <inheritdoc/>
        public override void Unsubscribe(Type eventType, IEventHandler handler)
        {
            GetOrCreateHandlerFactories(eventType)
                .Locking(factories =>
                {
                    factories.RemoveAll(
                        factory =>
                            factory is SingleInstanceHandlerFactory &&
                            (factory as SingleInstanceHandlerFactory).HandlerInstance == handler
                    );
                });
        }

        /// <inheritdoc/>
        public override void Unsubscribe(Type eventType, IEventHandlerFactory factory)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Remove(factory));
        }

        /// <inheritdoc/>
        public override void UnsubscribeAll(Type eventType)
        {
            GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Clear());
        }

        public override Task PublishAsync(Type eventType, object eventData)
        {
            var eventName = EventNameAttribute.GetNameOrDefault(eventType);
            var body = Serializer.Serialize(eventData);

            using (var channel = ConnectionPool.Get(RabbitMqEventBusOptions.ConnectionName).CreateModel())
            {
                channel.ExchangeDeclare(
                    RabbitMqEventBusOptions.ExchangeName,
                    "direct",
                    durable: true
                );
                
                var properties = channel.CreateBasicProperties();
                properties.DeliveryMode = RabbitMqConsts.DeliveryModes.Persistent;

                channel.BasicPublish(
                   exchange: RabbitMqEventBusOptions.ExchangeName,
                    routingKey: eventName,
                    mandatory: true,
                    basicProperties: properties,
                    body: body
                );
            }

            return Task.CompletedTask;
        }

        private List<IEventHandlerFactory> GetOrCreateHandlerFactories(Type eventType)
        {
            return HandlerFactories.GetOrAdd(
                eventType,
                type =>
                {
                    var eventName = EventNameAttribute.GetNameOrDefault(type);
                    EventTypes[eventName] = type;
                    return new List<IEventHandlerFactory>();
                }
            );
        }

        protected override IEnumerable<EventTypeWithEventHandlerFactories> GetHandlerFactories(Type eventType)
        {
            var handlerFactoryList = new List<EventTypeWithEventHandlerFactories>();

            foreach (var handlerFactory in HandlerFactories.Where(hf => ShouldTriggerEventForHandler(eventType, hf.Key)))
            {
                handlerFactoryList.Add(new EventTypeWithEventHandlerFactories(handlerFactory.Key, handlerFactory.Value));
            }

            return handlerFactoryList.ToArray();
        }

        private static bool ShouldTriggerEventForHandler(Type targetEventType, Type handlerEventType)
        {
            //Should trigger same type
            if (handlerEventType == targetEventType)
            {
                return true;
            }

            //TODO: Support inheritance? But it does not support on subscription to RabbitMq!
            //Should trigger for inherited types
            if (handlerEventType.IsAssignableFrom(targetEventType))
            {
                return true;
            }

            return false;
        }
    }
}