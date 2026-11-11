using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Infrastructure.EventStream;
using Pragma.Accounts.Infrastructure.Messaging;

namespace Pragma.Accounts.Tests.Infrastructure;

public sealed class ChannelAccountEventHubTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static AccountEvent Event(string id) => new(id, $"evt-{id}", AccountEventTypes.Updated, TestData.Now);

    [Fact]
    public async Task Publish_FansOutToEverySubscriber()
    {
        var hub = new ChannelAccountEventHub(new EventStreamOptions());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        await using var first = hub.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        await using var second = hub.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstNext = first.MoveNextAsync();
        var secondNext = second.MoveNextAsync();
        await WaitForSubscribersAsync(hub, 2);

        await hub.PublishAsync(Event("a"), _ct);

        Assert.True(await firstNext);
        Assert.True(await secondNext);
        Assert.Equal("a", first.Current.AccountId);
        Assert.Equal("a", second.Current.AccountId);
    }

    [Fact]
    public async Task SlowSubscriber_DropsOldestEvents()
    {
        var hub = new ChannelAccountEventHub(new EventStreamOptions { SubscriberBufferSize = 2 });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        await using var subscriber = hub.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = subscriber.MoveNextAsync();
        await WaitForSubscribersAsync(hub, 1);

        // El primero lo toma el lector pendiente; de los 3 siguientes solo caben los 2 mas nuevos.
        await hub.PublishAsync(Event("1"), _ct);
        Assert.True(await pending);
        foreach (var id in new[] { "2", "3", "4" })
        {
            await hub.PublishAsync(Event(id), _ct);
        }

        Assert.True(await subscriber.MoveNextAsync());
        Assert.Equal("3", subscriber.Current.AccountId);
        Assert.True(await subscriber.MoveNextAsync());
        Assert.Equal("4", subscriber.Current.AccountId);
    }

    [Fact]
    public async Task Cancelling_Unsubscribes()
    {
        var hub = new ChannelAccountEventHub(new EventStreamOptions());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in hub.SubscribeAsync(cts.Token))
            {
            }
        }, _ct);
        await WaitForSubscribersAsync(hub, 1);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task Publish_WithoutSubscribers_Completes()
    {
        var hub = new ChannelAccountEventHub(new EventStreamOptions());

        await hub.PublishAsync(Event("a"), _ct);

        Assert.Equal(0, hub.SubscriberCount);
    }

    private static async Task WaitForSubscribersAsync(ChannelAccountEventHub hub, int expected)
    {
        for (var i = 0; i < 200 && hub.SubscriberCount < expected; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, hub.SubscriberCount);
    }
}

public sealed class SqsMessagingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
    private readonly SqsOptions _options = new() { QueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/accounts", BufferCapacity = 100 };

    public SqsMessagingTests() =>
        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse());

    private static AccountEvent Event(int i) => new($"acc-{i}", $"evt-{i}", AccountEventTypes.Created, TestData.Now);

    private SqsDispatcher Dispatcher(SqsOutbox outbox) =>
        new(outbox, _sqs, _options, NullLogger<SqsDispatcher>.Instance);

    [Fact]
    public async Task Publisher_EnqueuesWithoutCallingSqs()
    {
        var outbox = new SqsOutbox(_options);

        await new SqsEventPublisher(outbox).PublishAsync(Event(1), _ct);

        Assert.True(outbox.Channel.Reader.TryRead(out var queued));
        Assert.Equal("acc-1", queued.AccountId);
        await _sqs.DidNotReceive().SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publisher_FullBuffer_AppliesBackpressure()
    {
        var outbox = new SqsOutbox(new SqsOptions { QueueUrl = _options.QueueUrl, BufferCapacity = 1 });
        var publisher = new SqsEventPublisher(outbox);
        await publisher.PublishAsync(Event(1), _ct);

        var blocked = publisher.PublishAsync(Event(2), _ct);
        Assert.False(blocked.IsCompleted);

        outbox.Channel.Reader.TryRead(out _);
        await blocked;
    }

    [Fact]
    public async Task Dispatcher_SendsInBatchesOfAtMostTen_AndDrainsOnStop()
    {
        var outbox = new SqsOutbox(_options);
        for (var i = 0; i < 23; i++)
        {
            await outbox.Channel.Writer.WriteAsync(Event(i), _ct);
        }

        var dispatcher = Dispatcher(outbox);
        await dispatcher.StartAsync(_ct);
        await WaitUntilAsync(() => !outbox.Channel.Reader.TryPeek(out _));
        await dispatcher.StopAsync(_ct);

        var batches = _sqs.ReceivedCalls()
            .Select(c => (SendMessageBatchRequest)c.GetArguments()[0]!)
            .ToList();
        Assert.Equal(23, batches.Sum(b => b.Entries.Count));
        Assert.All(batches, b => Assert.InRange(b.Entries.Count, 1, SqsDispatcher.MaxBatchSize));
        var first = batches[0].Entries[0];
        Assert.Equal(_options.QueueUrl, batches[0].QueueUrl);
        Assert.Contains("\"accountId\":\"acc-0\"", first.MessageBody, StringComparison.Ordinal);
        Assert.Equal(AccountEventTypes.Created, first.MessageAttributes["eventType"].StringValue);
    }

    [Fact]
    public async Task SendBatch_SqsFailure_IsLoggedNotThrown()
    {
        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonSQSException("throttled"));

        await Dispatcher(new SqsOutbox(_options)).SendBatchAsync([Event(1)], _ct);
    }

    [Fact]
    public async Task SendBatch_PartialFailure_IsLoggedNotThrown()
    {
        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse { Failed = [new BatchResultErrorEntry { Id = "0", Code = "InternalError" }] });

        await Dispatcher(new SqsOutbox(_options)).SendBatchAsync([Event(1)], _ct);

        await _sqs.Received(1).SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendBatch_Empty_DoesNothing()
    {
        await Dispatcher(new SqsOutbox(_options)).SendBatchAsync([], _ct);

        await _sqs.DidNotReceive().SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
