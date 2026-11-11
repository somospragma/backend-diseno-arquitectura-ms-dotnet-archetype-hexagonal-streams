using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Infrastructure.Cache;
using Pragma.Accounts.Infrastructure.CreditScore;
using Pragma.Accounts.Infrastructure.Helpers;

namespace Pragma.Accounts.Tests.Infrastructure;

public sealed class DistributedAccountCacheTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static DistributedAccountCache Create(IDistributedCache cache) =>
        new(cache, new CacheOptions(), NullLogger<DistributedAccountCache>.Instance);

    [Fact]
    public async Task SetThenGet_RoundTrips_AndEvictRemoves()
    {
        var cache = Create(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        await cache.SetAsync(TestData.Account(), _ct);
        var hit = await cache.GetAsync("acc-1", _ct);
        await cache.EvictAsync("acc-1", _ct);

        Assert.Equal("ana.perez@example.com", hit?.Email);
        Assert.Null(await cache.GetAsync("acc-1", _ct));
    }

    [Fact]
    public async Task Failures_AreSwallowed()
    {
        var broken = Substitute.For<IDistributedCache>();
        broken.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));
        broken.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));
        broken.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));
        var cache = Create(broken);

        Assert.Null(await cache.GetAsync("acc-1", _ct));
        await cache.SetAsync(TestData.Account(), _ct);
        await cache.EvictAsync("acc-1", _ct);
    }

    [Fact]
    public void Key_IsNamespaced() => Assert.Equal("accounts:acc-1", DistributedAccountCache.Key("acc-1"));
}

public sealed class HttpCreditScoreGatewayTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HttpCreditScoreGateway Create(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://provider") });

    [Fact]
    public async Task Get_MapsProviderResponse()
    {
        var gateway = Create(request =>
        {
            Assert.Equal("/api/v1/credit-scores/acc%201", request.RequestUri!.AbsolutePath);
            return Json("""{"accountId":"acc 1","score":710,"rating":"A"}""");
        });

        var score = await gateway.GetAsync("acc 1", _ct);

        Assert.Equal(new CreditScore("acc 1", 710, "A"), score);
    }

    [Fact]
    public async Task Get_MissingRating_DefaultsToUnknown()
    {
        var score = await Create(_ => Json("""{"score":500}""")).GetAsync("acc-1", _ct);

        Assert.Equal("UNKNOWN", score.Rating);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Get_ProviderError_ThrowsTechnical(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<TechnicalException>(() =>
            Create(_ => new HttpResponseMessage(status)).GetAsync("acc-1", _ct));

        Assert.Equal(ErrorCatalog.CreditScoreUnavailable, ex.Error);
    }

    [Fact]
    public async Task Get_NullBody_ThrowsTechnical() =>
        await Assert.ThrowsAsync<TechnicalException>(() => Create(_ => Json("null")).GetAsync("acc-1", _ct));

    [Fact]
    public async Task Get_Timeout_ThrowsTechnical() =>
        await Assert.ThrowsAsync<TechnicalException>(() =>
            Create(_ => throw new TaskCanceledException("timeout")).GetAsync("acc-1", _ct));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}

public sealed class DataMaskerTests
{
    [Theory]
    [InlineData("ana.perez@example.com", "a***@example.com")]
    [InlineData("noatsign", "****")]
    [InlineData("@example.com", "****")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MaskEmail(string? input, string expected) => Assert.Equal(expected, DataMasker.MaskEmail(input));

    [Theory]
    [InlineData("1234567890", "****7890")]
    [InlineData("123", "****")]
    [InlineData("", "")]
    public void MaskLastFour(string input, string expected) => Assert.Equal(expected, DataMasker.MaskLastFour(input));
}
