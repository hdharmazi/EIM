using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace ARM.Integration.Core.Helpers
{
    public sealed class ServiceBusHelper : IAsyncDisposable
    {
        private readonly ServiceBusClient _client;
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);


        public ServiceBusHelper(string configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration))
            {
                throw new ArgumentException("Service Bus configuration is required.", nameof(configuration));
            }

            using var doc = JsonDocument.Parse(configuration);
            var root = doc.RootElement;

            var connectionString = root.TryGetProperty("ConnectionString", out var prop)
                ? prop.GetString() ?? throw new ArgumentException("ConnectionString value is null in configuration JSON.")
                : throw new ArgumentException("ConnectionString not found in configuration JSON.");

            _client = new ServiceBusClient(connectionString);
        }

        public async Task SendAsync(
            string entityPath,
            string messageBody,
            IDictionary<string, object>? applicationProperties = null,
            string? messageId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entityPath))
            {
                throw new ArgumentException("Entity path is required.", nameof(entityPath));
            }

            if (messageBody is null)
            {
                throw new ArgumentNullException(nameof(messageBody));
            }

            var sender = _senders.GetOrAdd(entityPath, _client.CreateSender);
            var message = new ServiceBusMessage(messageBody)
            {
                MessageId = messageId ?? Guid.NewGuid().ToString("N")
            };

            if (applicationProperties is not null)
            {
                foreach (var property in applicationProperties)
                {
                    message.ApplicationProperties[property.Key] = property.Value;
                }
            }

            await sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public Task SendAsync<T>(
            string entityPath,
            T payload,
            IDictionary<string, object>? applicationProperties = null,
            string? messageId = null,
            CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(payload);
            return SendAsync(entityPath, json, applicationProperties, messageId, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var sender in _senders.Values)
            {
                await sender.DisposeAsync().ConfigureAwait(false);
            }

            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
