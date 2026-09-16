using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace LeiGodAutoPause;

public sealed record ApiCallResult(bool Success, string Message);

public sealed class LeigodApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public LeigodApiClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    public Task<ApiCallResult> PauseAsync(LeigodSession session, CancellationToken cancellationToken = default)
        => SendAsync(session, "/client/pause", "暂停", cancellationToken);

    public Task<ApiCallResult> ResumeAsync(LeigodSession session, CancellationToken cancellationToken = default)
        => SendAsync(session, "/client/recover", "恢复", cancellationToken);

    public Task<ApiCallResult> TestAsync(LeigodSession session, CancellationToken cancellationToken = default)
        => SendAsync(session, "/client/pause/status", "状态检查", cancellationToken);

    private async Task<ApiCallResult> SendAsync(
        LeigodSession session,
        string relativePath,
        string actionName,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        HttpStatusCode? lastStatusCode = null;

        foreach (var uri in session.BuildCandidateUris(relativePath))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(session.RequestBody, Encoding.UTF8, "text/plain")
                };

                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("User-Agent", "axios/1.12.0");

                if (!string.IsNullOrWhiteSpace(session.UserId))
                {
                    request.Headers.TryAddWithoutValidation("X-User-Id", session.UserId);
                }

                request.Headers.TryAddWithoutValidation("X-Device-Id", session.DeviceId);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                lastStatusCode = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return new ApiCallResult(true, $"{actionName}请求已发送成功（HTTP {(int)response.StatusCode}）。");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException("请求超时");
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (lastStatusCode is not null)
        {
            return new ApiCallResult(false, $"{actionName}请求失败，服务器返回 HTTP {(int)lastStatusCode.Value}。");
        }

        return new ApiCallResult(false, $"{actionName}请求失败：{lastException?.Message ?? "无法连接雷神接口"}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
