using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;

namespace Pragma.Accounts.Infrastructure.Messaging;

public sealed class SqsOptions
{
    public const string Section = "Messaging:Sqs";

    /// <summary>URL de la cola destino de los eventos de dominio.</summary>
    public string QueueUrl { get; set; } = string.Empty;

    /// <summary>Endpoint alterno (LocalStack). Vacio usa el endpoint regional de AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Capacidad del buffer en memoria. Lleno = el publicador espera (backpressure).</summary>
    public int BufferCapacity { get; set; } = 1_000;
}

/// <summary>Buffer acotado entre los casos de uso y el despachador a SQS.</summary>
internal sealed class SqsOutbox
{
    public SqsOutbox(SqsOptions options) =>
        Channel = System.Threading.Channels.Channel.CreateBounded<AccountEvent>(new BoundedChannelOptions(options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public Channel<AccountEvent> Channel { get; }
}

/// <summary>
/// Publica encolando en el buffer: la llamada HTTP no espera a SQS. Si el buffer esta
/// lleno, WriteAsync espera (backpressure) hasta que haya espacio o se cancele la solicitud.
/// </summary>
internal sealed class SqsEventPublisher(SqsOutbox outbox) : IEventPublisher
{
    public Task PublishAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountEvent);
        return outbox.Channel.Writer.WriteAsync(accountEvent, cancellationToken).AsTask();
    }
}

/// <summary>
/// Drena el buffer en lotes de hasta 10 mensajes (limite de SendMessageBatch).
/// Al detenerse envia lo pendiente. Nota: el buffer es en memoria; si se requiere
/// entrega garantizada ante caidas del proceso, usar el patron outbox transaccional.
/// </summary>
internal sealed partial class SqsDispatcher(
    SqsOutbox outbox,
    IAmazonSQS sqs,
    SqsOptions options,
    ILogger<SqsDispatcher> logger) : BackgroundService
{
    internal const int MaxBatchSize = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = outbox.Channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await SendBatchAsync(Drain(reader), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado ordenado: se envia lo que quedo en el buffer.
            while (reader.TryPeek(out _))
            {
                await SendBatchAsync(Drain(reader), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal async Task SendBatchAsync(IReadOnlyList<AccountEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var request = new SendMessageBatchRequest
        {
            QueueUrl = options.QueueUrl,
            Entries = batch.Select((evt, i) => new SendMessageBatchRequestEntry
            {
                Id = i.ToString(CultureInfo.InvariantCulture),
                MessageBody = JsonSerializer.Serialize(evt, Json),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["eventType"] = new() { DataType = "String", StringValue = evt.Type },
                    ["eventId"] = new() { DataType = "String", StringValue = evt.EventId },
                },
            }).ToList(),
        };

        try
        {
            var response = await sqs.SendMessageBatchAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var failed in response.Failed ?? [])
            {
                LogEntryFailed(logger, failed.Id, failed.Code);
            }
        }
        catch (AmazonSQSException ex)
        {
            LogBatchFailed(logger, batch.Count, ex);
        }
    }

    private static List<AccountEvent> Drain(ChannelReader<AccountEvent> reader)
    {
        var batch = new List<AccountEvent>(MaxBatchSize);
        while (batch.Count < MaxBatchSize && reader.TryRead(out var evt))
        {
            batch.Add(evt);
        }

        return batch;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "SQS rejected batch entry {EntryId} with code {Code}")]
    private static partial void LogEntryFailed(ILogger logger, string entryId, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "SQS batch of {Count} events could not be sent")]
    private static partial void LogBatchFailed(ILogger logger, int count, Exception exception);
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Clave del publicador SQS dentro del fan-out de eventos.</summary>
    public const string PublisherKey = "sqs";

    /// <summary>Registra el publicador SQS (con clave <see cref="PublisherKey"/>) y su despachador en segundo plano.</summary>
    public static IServiceCollection AddSqsEventPublisher(this IServiceCollection services, SqsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.QueueUrl);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BufferCapacity, 1);
        services.AddSingleton(options);
        services.AddSingleton<SqsOutbox>();
        services.AddSingleton<IAmazonSQS>(_ => string.IsNullOrWhiteSpace(options.ServiceUrl)
            ? new AmazonSQSClient()
            : new AmazonSQSClient(new AmazonSQSConfig { ServiceURL = options.ServiceUrl }));
        services.AddKeyedSingleton<IEventPublisher, SqsEventPublisher>(PublisherKey);
        services.AddHostedService<SqsDispatcher>();
        return services;
    }
}
