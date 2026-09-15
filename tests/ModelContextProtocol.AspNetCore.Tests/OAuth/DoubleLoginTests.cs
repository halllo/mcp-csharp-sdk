using ModelContextProtocol.Client;
using ModelContextProtocol.Tests.Utils;

namespace ModelContextProtocol.AspNetCore.Tests.OAuth;

/// <summary>
/// One connect must cost the user exactly one login.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Connecting_AsksTheUserToLogInExactlyOnce"/> FAILS today, which is the point. With default
/// options the client prefers the 2026-07-28 revision, so it opens the connection with a
/// <c>server/discover</c> probe bounded by
/// <see cref="McpClientOptions.DiscoverProbeTimeout"/> — 5 seconds by default. An interactive login takes
/// longer than that, so the probe, and with it the login its 401 kicked off, is abandoned. The client then
/// falls back to the <c>initialize</c> handshake, which carries no token because the first login never got
/// far enough to cache one, earns its own 401, and opens a second browser tab.
/// </para>
/// <para>
/// The timings here are scaled down from a human login to keep the test fast: the probe timeout is
/// <see cref="ProbeTimeout"/> instead of 5 seconds, and the simulated login takes <see cref="HumanLogin"/>.
/// The ratio is what matters — any login slower than the probe timeout reproduces this.
/// </para>
/// </remarks>
public class DoubleLoginTests(ITestOutputHelper outputHelper) : OAuthTestBase(outputHelper)
{
    /// <summary>Stands in for the 5 second <see cref="McpClientOptions.DiscoverProbeTimeout"/> default.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Longer than <see cref="ProbeTimeout"/> — as any real interactive login is.</summary>
    private static readonly TimeSpan HumanLogin = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Connecting_AsksTheUserToLogInExactlyOnce()
    {
        var logins = await CountLoginsDuringConnectAsync(options =>
        {
            // Default probe behavior: the probe gives up while the user is still logging in.
            options.DiscoverProbeTimeout = ProbeTimeout;
        });

        Assert.Equal(1, logins);
    }

    /// <summary>
    /// The companion to <see cref="Connecting_AsksTheUserToLogInExactlyOnce"/>, and the reason it is a real
    /// finding rather than a flaky test: the same slow login costs exactly one prompt as soon as the probe
    /// is no longer allowed to abandon it mid-flight. Whatever fixes the test above must keep this one green.
    /// </summary>
    [Fact]
    public async Task Connecting_WithoutASeparateProbeTimeout_AsksTheUserToLogInExactlyOnce()
    {
        var logins = await CountLoginsDuringConnectAsync(options =>
        {
            // Timeout.InfiniteTimeSpan disables the separate probe timeout, so the probe — and the login its
            // 401 started — is bounded only by InitializationTimeout, which outlasts the login.
            options.DiscoverProbeTimeout = Timeout.InfiniteTimeSpan;
        });

        Assert.Equal(1, logins);
    }

    /// <summary>
    /// Connects once against an MCP server that requires OAuth and returns how many times the user was asked
    /// to log in.
    /// </summary>
    private async Task<int> CountLoginsDuringConnectAsync(Action<McpClientOptions> configureOptions)
    {
        await using var app = await StartMcpServerAsync();

        var logins = 0;

        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new(McpServerUrl),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new()
            {
                ClientId = "demo-client",
                ClientSecret = "demo-secret",
                RedirectUri = new Uri("http://localhost:1179/callback"),
                AuthorizationCallbackHandler = async (context, cancellationToken) =>
                {
                    // Each call is a browser tab the user has to deal with. The first one is a human typing
                    // their password; a second tab is instant, because the IdP already has a session by then.
                    // Like a real HttpListener-based handler, the wait ignores the SDK's cancellation token:
                    // a person does not stop typing because a timeout elapsed somewhere inside the client.
                    if (Interlocked.Increment(ref logins) == 1)
                    {
                        await Task.Delay(HumanLogin, CancellationToken.None);
                    }

                    return await HandleAuthorizationUrlAsync(context, cancellationToken);
                },
            },
        }, HttpClient, LoggerFactory);

        var options = new McpClientOptions
        {
            InitializationTimeout = TestConstants.DefaultTimeout,
        };
        configureOptions(options);

        await using var client = await McpClient.CreateAsync(
            transport, options, loggerFactory: LoggerFactory, cancellationToken: TestContext.Current.CancellationToken);

        // The connect itself succeeds either way. The cost to the user is what differs.
        Assert.NotNull(client.ServerInfo);

        return logins;
    }
}
