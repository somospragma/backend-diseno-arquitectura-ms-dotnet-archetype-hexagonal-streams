using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;
using Pragma.Accounts.Infrastructure.Messaging;

namespace Pragma.Accounts.Tests.Api;

/// <summary>
/// Levanta la aplicacion real con los driven adapters externos reemplazados por dobles en memoria.
/// El hub de eventos (Channels) es el real: los tests de SSE lo ejercitan de punta a punta.
/// </summary>
public sealed class AccountsApiFactory : WebApplicationFactory<Program>
{
    public InMemoryAccountGateway Accounts { get; } = new();

    public List<AccountEvent> PublishedEvents { get; } = [];

    public ICreditScoreGateway CreditScores { get; } = Substitute.For<ICreditScoreGateway>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Accounts", "Host=localhost;Database=accounts_test");
        builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
        builder.UseSetting("Messaging:Sqs:QueueUrl", "https://sqs.us-east-1.amazonaws.com/000000000000/accounts");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountGateway>();
            services.RemoveAll<ICacheGateway>();
            services.RemoveAll<ICreditScoreGateway>();
            services.RemoveAllKeyed<IEventPublisher>(MessagingServiceCollectionExtensions.PublisherKey);
            var dispatcher = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SqsDispatcher));
            services.Remove(dispatcher);
            services.AddSingleton<IAccountGateway>(Accounts);
            services.AddSingleton<ICacheGateway, NoCache>();
            services.AddKeyedSingleton<IEventPublisher>(MessagingServiceCollectionExtensions.PublisherKey, new RecordingPublisher(PublishedEvents));
            services.AddSingleton(CreditScores);
        });
    }

    public sealed class InMemoryAccountGateway : IAccountGateway
    {
        private readonly ConcurrentDictionary<string, Account> _store = new();

        public Task<Account> SaveAsync(Account account, CancellationToken cancellationToken)
        {
            _store[account.Id] = account;
            return Task.FromResult(account);
        }

        public Task<Account?> FindByIdAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_store.TryGetValue(id, out var account) ? account : null);

        public IAsyncEnumerable<Account> StreamAllAsync(int limit, CancellationToken cancellationToken) =>
            _store.Values.OrderBy(a => a.Id, StringComparer.Ordinal).Take(limit).ToAsyncEnumerable();

        public Task<bool> ExistsByIdAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_store.ContainsKey(id));

        public Task DeleteByIdAsync(string id, CancellationToken cancellationToken)
        {
            _store.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<Account> SearchAsync(AccountSearchCriteria criteria, int limit, CancellationToken cancellationToken) =>
            _store.Values.Where(a => criteria.Status is null || a.Status == criteria.Status).Take(limit).ToAsyncEnumerable();
    }

    private sealed class NoCache : ICacheGateway
    {
        public Task<Account?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<Account?>(null);

        public Task SetAsync(Account account, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EvictAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPublisher(List<AccountEvent> sink) : IEventPublisher
    {
        public Task PublishAsync(AccountEvent accountEvent, CancellationToken cancellationToken)
        {
            lock (sink)
            {
                sink.Add(accountEvent);
            }

            return Task.CompletedTask;
        }
    }
}
