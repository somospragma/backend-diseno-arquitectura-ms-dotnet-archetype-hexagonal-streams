using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.EventStream;

public sealed class EventStreamOptions
{
    public const string Section = "EventStream";

    /// <summary>
    /// Buffer por suscriptor. Si un consumidor lento lo llena se descartan los eventos
    /// mas antiguos (DropOldest): un cliente lento nunca frena al publicador ni a otros clientes.
    /// </summary>
    public int SubscriberBufferSize { get; set; } = 256;
}

/// <summary>
/// Fan-out en proceso de eventos de dominio: cada suscriptor recibe su propio
/// Channel acotado. Publicar nunca bloquea.
/// </summary>
internal sealed class ChannelAccountEventHub(EventStreamOptions options) : IAccountEventStream, IEventPublisher
{
    private readonly ConcurrentDictionary<Guid, Channel<AccountEvent>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public Task PublishAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountEvent);
        foreach (var subscriber in _subscribers.Values)
        {
            // Con DropOldest el TryWrite siempre acepta; solo falla si el canal ya se cerro.
            subscriber.Writer.TryWrite(accountEvent);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AccountEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<AccountEvent>(new BoundedChannelOptions(options.SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        try
        {
            await foreach (var accountEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return accountEvent;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}

public static class EventStreamServiceCollectionExtensions
{
    /// <summary>Clave del publicador en proceso dentro del fan-out de eventos.</summary>
    public const string PublisherKey = "event-stream";

    /// <summary>Registra el hub como IAccountEventStream y como IEventPublisher con clave <see cref="PublisherKey"/>.</summary>
    public static IServiceCollection AddChannelEventStream(this IServiceCollection services, EventStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SubscriberBufferSize, 1);
        services.AddSingleton(options);
        services.AddSingleton<ChannelAccountEventHub>();
        services.AddSingleton<IAccountEventStream>(sp => sp.GetRequiredService<ChannelAccountEventHub>());
        services.AddKeyedSingleton<IEventPublisher>(PublisherKey, (sp, _) => sp.GetRequiredService<ChannelAccountEventHub>());
        return services;
    }
}
