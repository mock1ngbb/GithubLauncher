using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;

namespace GithubLauncher.Services
{
    /// <summary>
    /// Centralized HTTP client factory providing configured HttpClient instances
    /// with automatic retry, rate-limit tracking, and consistent defaults.
    /// Singleton — all consumers share one handler pool.
    /// </summary>
    public static class HttpClientFactory
    {
        private static readonly HttpClient _defaultClient;
        private static readonly HttpClient _githubClient;

        public static readonly RateLimitTracker GitHubRateLimit = new();

        static HttpClientFactory()
        {
            var defaultHandler = new RetryHandler(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

            _defaultClient = new HttpClient(defaultHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _defaultClient.DefaultRequestHeaders.UserAgent.ParseAdd("GithubLauncher/1.0");

            var githubHandler = new RateLimitTrackingHandler(
                new RetryHandler(new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                }),
                GitHubRateLimit
            );

            _githubClient = new HttpClient(githubHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _githubClient.DefaultRequestHeaders.UserAgent.ParseAdd("GithubLauncher/1.0");
        }

        /// <summary>
        /// Returns a general-purpose HttpClient with 30s timeout, retry, and user-agent.
        /// </summary>
        public static HttpClient GetClient() => _defaultClient;

        /// <summary>
        /// Returns an HttpClient configured for GitHub API requests:
        /// includes Bearer token auth (if set), retry, rate-limit tracking,
        /// and conditional request support.
        /// </summary>
        public static HttpClient GetGitHubClient()
        {
            var token = AppSettings.Load()?.GitHubApiToken ?? string.Empty;
            _githubClient.DefaultRequestHeaders.Authorization = !string.IsNullOrEmpty(token)
                ? new AuthenticationHeaderValue("Bearer", token)
                : null;

            return _githubClient;
        }

        /// <summary>
        /// Returns an HttpClient with a short timeout suited for download progress.
        /// </summary>
        public static HttpClient GetDownloadClient()
        {
            return new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
        }
    }

    /// <summary>
    /// Tracks GitHub API rate-limit state from response headers.
    /// </summary>
    public class RateLimitTracker
    {
        public int Remaining { get; private set; } = 60;
        public int Limit { get; private set; } = 60;
        public DateTime ResetAt { get; private set; } = DateTime.UtcNow;
        public bool IsRateLimited => Remaining <= 0;

        public void UpdateFromHeaders(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem))
            {
                if (int.TryParse(rem.FirstOrDefault(), out var r))
                    Remaining = r;
            }

            if (response.Headers.TryGetValues("X-RateLimit-Limit", out var lim))
            {
                if (int.TryParse(lim.FirstOrDefault(), out var l))
                    Limit = l;
            }

            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset))
            {
                if (long.TryParse(reset.FirstOrDefault(), out var unixTs))
                    ResetAt = DateTimeOffset.FromUnixTimeSeconds(unixTs).UtcDateTime;
            }

            if (Remaining < 10)
                System.Diagnostics.Debug.WriteLine(
                    $"[RateLimit] Low: {Remaining}/{Limit} — resets at {ResetAt:HH:mm:ss}");
        }
    }

    /// <summary>
    /// DelegatingHandler with exponential backoff retry for transient failures.
    /// Retries on: HttpRequestException (network), TaskCanceledException (timeout),
    /// HTTP 429 (rate limit), HTTP 5xx (server errors).
    /// Max 3 retries with 1s/2s/4s backoff plus jitter.
    /// </summary>
    internal class RetryHandler : DelegatingHandler
    {
        private const int MaxRetries = 3;
        private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
        private static readonly Random _jitter = new();

        public RetryHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpResponseMessage? response = null;
                try
                {
                    response = await base.SendAsync(request, ct);
                    if (attempt >= MaxRetries || !ShouldRetry(response.StatusCode))
                        return response;

                    // Drain response so we can retry
                    if (response.Content != null)
                        await response.Content.LoadIntoBufferAsync();
                }
                catch (HttpRequestException) when (attempt < MaxRetries)
                {
                    // Network-level failure — always retry
                }
                catch (TaskCanceledException) when (attempt < MaxRetries)
                {
                    // Timeout — retry
                }

                response?.Dispose();

                var delay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                var jitter = TimeSpan.FromMilliseconds(_jitter.Next(-500, 500));
                System.Diagnostics.Debug.WriteLine($"[RetryHandler] Attempt {attempt + 1} failed, retrying in {delay + jitter:g}");
                await Task.Delay(delay + jitter, ct);
            }
        }

        private static bool ShouldRetry(HttpStatusCode status) => status switch
        {
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.GatewayTimeout => true,
            _ when (int)status >= 500 => true,
            _ => false
        };
    }

    /// <summary>
    /// DelegatingHandler that intercepts GitHub API responses to track rate-limit headers.
    /// </summary>
    internal class RateLimitTrackingHandler : DelegatingHandler
    {
        private readonly RateLimitTracker _tracker;

        public RateLimitTrackingHandler(HttpMessageHandler inner, RateLimitTracker tracker) : base(inner)
        {
            _tracker = tracker;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri?.Host == "api.github.com")
                _tracker.UpdateFromHeaders(response);
            return response;
        }
    }
}
