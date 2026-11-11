using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Model.Exceptions;
using Pragma.Accounts.Infrastructure.Api;

namespace Pragma.Accounts.Tests.Api;

public sealed class AccountsApiTests(AccountsApiFactory factory) : IClassFixture<AccountsApiFactory>
{
    private const string Base = AccountEndpoints.BasePath;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_Valid_Returns201WithLocationAndMaskedEmail()
    {
        var response = await _client.PostAsJsonAsync(Base, NewAccount(), _ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountResponse>(_ct);
        Assert.NotNull(body);
        Assert.Equal($"{Base}/{body.Id}", response.Headers.Location!.ToString());
        Assert.Equal("a***@example.com", body.Email);
        Assert.Equal(AccountStatus.Active, body.Status);
        Assert.Contains(factory.PublishedEvents, e => e.AccountId == body.Id && e.Type == AccountEventTypes.Created);
    }

    [Fact]
    public async Task Create_Invalid_Returns400ValidationProblemWithCode()
    {
        var response = await _client.PostAsJsonAsync(Base, new AccountRequest("", "not-an-email", "SAVINGS", -1m), _ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = await ReadJson(response);
        Assert.Equal(ErrorCatalog.ValidationError.Code, problem.RootElement.GetProperty("code").GetString());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("holderName", out _));
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("balance", out _));
    }

    [Fact]
    public async Task Create_InvalidStatus_Returns400()
    {
        var response = await _client.PostAsJsonAsync(Base, NewAccount() with { Status = "CLOSED" }, _ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MalformedJson_Returns400Problem()
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(Base, content, _ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadJson(response);
        Assert.Equal(ErrorCatalog.ValidationError.Code, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_Existing_Returns200()
    {
        var id = await CreateAsync();

        var account = await _client.GetFromJsonAsync<AccountResponse>($"{Base}/{id}", _ct);

        Assert.Equal(id, account?.Id);
    }

    [Fact]
    public async Task Get_Missing_Returns404ProblemWithCodeAndCorrelationId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/does-not-exist");
        request.Headers.Add("X-Correlation-Id", "test-correlation-0001");

        var response = await _client.SendAsync(request, _ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("test-correlation-0001", response.Headers.GetValues("X-Correlation-Id").Single());
        using var problem = await ReadJson(response);
        Assert.Equal(ErrorCatalog.AccountNotFound.Code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-correlation-0001", problem.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task InvalidCorrelationId_IsReplaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}?limit=1");
        request.Headers.Add("X-Correlation-Id", "bad id with spaces");

        var response = await _client.SendAsync(request, _ct);

        var returned = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual("bad id with spaces", returned);
        Assert.True(Guid.TryParse(returned, out _));
    }

    [Fact]
    public async Task Responses_IncludeSecurityHeaders()
    {
        var response = await _client.GetAsync($"{Base}?limit=1", _ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task List_ReturnsAccounts()
    {
        await CreateAsync();

        var accounts = await _client.GetFromJsonAsync<List<AccountResponse>>($"{Base}?limit=100", _ct);

        Assert.NotEmpty(accounts!);
    }

    [Fact]
    public async Task Put_ReplacesAccount()
    {
        var id = await CreateAsync();

        var response = await _client.PutAsJsonAsync($"{Base}/{id}", NewAccount() with { HolderName = "Reemplazo" }, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Reemplazo", (await response.Content.ReadFromJsonAsync<AccountResponse>(_ct))!.HolderName);
    }

    [Fact]
    public async Task Put_Invalid_Returns400() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"{Base}/x", new AccountRequest(null, null, null, null), _ct)).StatusCode);

    [Fact]
    public async Task Patch_UpdatesOnlyGivenFields()
    {
        var id = await CreateAsync();

        var response = await _client.PatchAsJsonAsync($"{Base}/{id}", new AccountPatchRequest(null, null, null, 999m, null), _ct);

        var body = await response.Content.ReadFromJsonAsync<AccountResponse>(_ct);
        Assert.Equal(999m, body!.Balance);
        Assert.Equal("Ana Perez", body.HolderName);
    }

    [Fact]
    public async Task Patch_Invalid_Returns400()
    {
        var response = await _client.PatchAsJsonAsync($"{Base}/x",
            new AccountPatchRequest(new string('x', 200), "bad", null, -5m, "NOPE"), _ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Existing_Returns204ThenGetReturns404()
    {
        var id = await CreateAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{Base}/{id}", _ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/{id}", _ct)).StatusCode);
    }

    [Fact]
    public async Task Query_SearchesWithBody()
    {
        await CreateAsync();
        using var request = new HttpRequestMessage(new HttpMethod("QUERY"), $"{Base}/search?limit=50")
        {
            Content = JsonContent.Create(new AccountSearchRequest(AccountStatus.Active, null, null)),
        };

        var response = await _client.SendAsync(request, _ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(_ct);
        Assert.All(accounts!, a => Assert.Equal(AccountStatus.Active, a.Status));
    }

    [Fact]
    public async Task CreditScore_ReturnsScore()
    {
        var id = await CreateAsync();
        factory.CreditScores.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new CreditScore(id, 680, "B"));

        var score = await _client.GetFromJsonAsync<CreditScoreResponse>($"{Base}/{id}/credit-score", _ct);

        Assert.Equal(680, score!.Score);
    }

    [Fact]
    public async Task CreditScore_ProviderDown_Returns503()
    {
        var id = await CreateAsync();
        factory.CreditScores.GetAsync(id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TechnicalException(ErrorCatalog.CreditScoreUnavailable));

        var response = await _client.GetAsync($"{Base}/{id}/credit-score", _ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = await ReadJson(response);
        Assert.Equal(ErrorCatalog.CreditScoreUnavailable.Code, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnexpectedError_Returns500WithoutInternalDetails()
    {
        var id = await CreateAsync();
        factory.CreditScores.GetAsync(id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("secret internal detail"));

        var response = await _client.GetAsync($"{Base}/{id}/credit-score", _ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(_ct);
        Assert.DoesNotContain("secret internal detail", body, StringComparison.Ordinal);
        Assert.Contains(ErrorCatalog.UnexpectedError.Code, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_StreamsDomainEventsAsServerSentEvents()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var sse = await _client.GetAsync($"{Base}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync(cts.Token));

        // Los headers llegan cuando la suscripcion ya esta registrada: el evento no se puede perder.
        var id = await CreateAsync();
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        while (line is not null && !(line.StartsWith("data:", StringComparison.Ordinal) && line.Contains(id, StringComparison.Ordinal)));

        Assert.NotNull(line);
        using var payload = JsonDocument.Parse(line["data:".Length..]);
        Assert.Equal(id, payload.RootElement.GetProperty("accountId").GetString());
        Assert.Equal(AccountEventTypes.Created, payload.RootElement.GetProperty("type").GetString());
        Assert.Contains(factory.PublishedEvents, e => e.AccountId == id);
    }

    [Fact]
    public async Task HealthLive_Returns200() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", _ct)).StatusCode);

    [Fact]
    public async Task UnknownRoute_Returns404Problem()
    {
        var response = await _client.GetAsync("/nope", _ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static AccountRequest NewAccount() => new("Ana Perez", "ana.perez@example.com", "SAVINGS", 100m);

    private async Task<string> CreateAsync()
    {
        var response = await _client.PostAsJsonAsync(Base, NewAccount(), _ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountResponse>(_ct))!.Id;
    }

    private async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_ct), cancellationToken: _ct);
}
