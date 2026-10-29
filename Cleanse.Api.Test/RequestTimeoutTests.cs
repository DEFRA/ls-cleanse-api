using System.Net;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cleanse.Api.Test;

/// <summary>
/// The request timeout policy is what stops a dependency that has stopped answering from holding
/// every request open until the caller gives up. These tests prove it is wired to the configured
/// value and that it fires: the store substitute waits on the cancellation token the middleware
/// trips, exactly as Npgsql does when a query is cancelled.
/// </summary>
public class RequestTimeoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void The_policy_is_the_configured_timeout_and_answers_504()
    {
        var api = factory.Services.GetRequiredService<ApiOptions>();
        var policy = factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy;

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromSeconds(api.RequestTimeoutSeconds), policy.Timeout);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, policy.TimeoutStatusCode);
    }

    [Fact]
    public async Task A_dependency_that_never_answers_is_cut_off_with_a_504()
    {
        var store = Substitute.For<IIssueStore>();
        store.GetAsync(Arg.Any<IssueId>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return (Issue?)null;
            });

        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(store);

            // The policy under test, shortened so the test does not wait the production 25 seconds.
            services.PostConfigure<RequestTimeoutOptions>(options =>
                options.DefaultPolicy = new RequestTimeoutPolicy
                {
                    Timeout = TimeSpan.FromMilliseconds(250),
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
                });
        }));

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, ApiFactory.ApiKey);

        // A ceiling on the test itself: if the policy did not fire this fails rather than hangs.
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync("/v1/issues/never-answers", giveUp.Token);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await store.Received(1).GetAsync(Arg.Any<IssueId>(), Arg.Any<CancellationToken>());
    }
}
