using System.Net.Http;
using DouyinOpenApi.Internal;
using DouyinOpenApi.Models;
using Xunit;

namespace DouyinOpenApi.Tests;

/// <summary>
/// 错误处理、重试与 token 失效自动恢复的测试。
/// </summary>
public class ErrorHandlingTests
{
    private static (DouyinClient Client, RuntimeOptions Options) CreateClient(MockHttpServer server)
    {
        var client = new DouyinClient("test-key", "test-secret");
        client.Options.AutoUseClientToken = false;
        client.AccessToken = "token-abc";

        var options = new RuntimeOptions { ReadTimeout = 5000 };
        options.HostOverrides["open.douyin.com"] = server.BaseUrl;
        return (client, options);
    }

    [Fact]
    public async Task 业务错误码应抛出异常并带出_log_id()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":28001003,\"err_msg\":\"access_token 无效\",\"log_id\":\"log-777\"}");
        var (client, options) = CreateClient(server);

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
            client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "i" }, options));

        Assert.Equal("28001003", ex.Code);
        Assert.Equal("access_token 无效", ex.Message);
        Assert.Equal("log-777", ex.LogId);
        Assert.Equal(200, ex.HttpStatusCode);
        Assert.False(ex.IsRetryable);            // 业务错误不应重试
        Assert.True(ex.IsTokenExpired);          // 属于 token 失效类错误码
    }

    [Fact]
    public async Task 服务端_5xx_应重试到最大次数()
    {
        using var server = new MockHttpServer();
        server.EnqueueTimes(3, "{\"message\":\"boom\"}", 500);
        var (client, options) = CreateClient(server);
        options.MaxAttempts = 3;
        options.RetryBackoffBaseMilliseconds = 10;   // 缩短测试耗时

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
            client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "i" }, options));

        Assert.Equal(3, server.RequestCount);
        Assert.Equal(500, ex.HttpStatusCode);
        Assert.Contains("boom", ex.Message);          // 把服务端的错误体带进异常，便于排查
    }

    [Fact]
    public async Task 传输层失败应标记为网络错误且可重试()
    {
        var client = new DouyinClient("test-key", "test-secret");
        client.Options.AutoUseClientToken = false;
        client.AccessToken = "t";

        var options = new RuntimeOptions { MaxAttempts = 1, ReadTimeout = 3000 };

        // 注入一个必然抛 HttpRequestException 的 Handler。
        // 不用「连一个关闭的端口」来模拟：CI / 沙箱里往往有 HTTP 代理，
        // 连接被拒会被代理转成 502，测试结果随环境漂移。
        // 注意必须选一个「带 body」的接口：net472 上无 body 的 GET 走 HttpWebRequest 分支，
        // 不经过 HttpClientFactory，注入的 Handler 不会生效。
        DouyinClient.HttpClientFactory = _ => new HttpClient(new ThrowingHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
                client.GetCouponMetaStatisticsAsync(
                    new GetCouponMetaStatisticsRequest { AppId = "a", CouponMetaId = "m" }, options));

            Assert.True(ex.IsNetworkError, ex.ToString());
            Assert.True(ex.IsRetryable, ex.ToString());
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            DouyinClient.HttpClientFactory = null;
        }
    }

    /// <summary>模拟网络中断的 Handler。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("模拟网络中断"));
    }

    [Fact]
    public async Task token_失效时应自动刷新并重试一次()
    {
        using var server = new MockHttpServer();
        // 第一次返回 token 过期，第二次成功
        server.Enqueue("{\"err_no\":2190008,\"err_tips\":\"access_token expired\"}");
        server.Enqueue("{\"err_no\":0,\"data\":{\"data\":{\"has_more\":false,\"list\":[]}}}");

        var credential = new FakeCredential();
        var client = new DouyinClient("test-key", "test-secret") { Credential = credential };

        var options = new RuntimeOptions { MaxAttempts = 3 };
        options.HostOverrides["open.douyin.com"] = server.BaseUrl;

        var resp = await client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "i" }, options);

        Assert.NotNull(resp);
        Assert.Equal(2, server.RequestCount);

        // 第二次请求必须换成刷新后的 token
        Assert.Equal("token-1", server.Requests[0].Headers["access-token"]);
        Assert.Equal("token-2", server.Requests[1].Headers["access-token"]);
        Assert.Equal(2, credential.FetchCount);
    }

    [Fact]
    public async Task 调用方主动取消应抛出取消异常而不是重试()
    {
        using var server = new MockHttpServer();
        server.EnqueueTimes(5, "{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "i" }, options, cts.Token));
    }

    [Fact]
    public async Task 未携带_token_时错误信息应点明这一点()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"error_code\":2190002,\"description\":\"access_token无效\"},"
                       + "\"extra\":{\"error_code\":2190002,\"description\":\"access_token无效\"}}");

        // 既不设请求 token、也不设客户端 token，还关掉自动换取 →
        // SDK 会一个 token 都不带，服务端回 2190002。这类错误最容易被误判成参数问题。
        var client = new DouyinClient("test-key", "test-secret");
        client.Options.AutoUseClientToken = false;

        // 用 CreateRuntimeOptions 继承客户端默认值，否则单次选项会整体替换掉上面的设置
        var options = client.CreateRuntimeOptions();
        options.HostOverrides["open.douyin.com"] = server.BaseUrl;

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
            client.TradeOrderQueryAsync(new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options));

        Assert.Equal("2190002", ex.Code);
        Assert.Contains("未携带 access_token", ex.Message);
    }

    // ---------------------------------------------------------------- 错误解析单元测试

    [Fact]
    public void 错误解析应覆盖多种字段命名()
    {
        // 抖音不同业务线的错误字段不统一，逐个覆盖
        Assert.Equal(2190008, ErrorParser.Parse("{\"err_no\":2190008}").Code);
        Assert.Equal(1, ErrorParser.Parse("{\"error_code\":1}").Code);
        Assert.Equal(2, ErrorParser.Parse("{\"errcode\":2}").Code);
        Assert.Equal(3, ErrorParser.Parse("{\"errno\":3}").Code);
        Assert.Equal(4, ErrorParser.Parse("{\"error\":4}").Code);
        Assert.Equal(5, ErrorParser.Parse("{\"err\":{\"err_code\":5,\"err_msg\":\"bad\"}}").Code);
        Assert.Equal("bad", ErrorParser.Parse("{\"err\":{\"err_code\":5,\"err_msg\":\"bad\"}}").Message);

        // 网关层错误藏在 extra 里，优先级最高
        var gateway = ErrorParser.Parse("{\"extra\":{\"error_code\":40002,\"description\":\"签名错误\",\"logid\":\"lg-1\"}}");
        Assert.Equal(40002, gateway.Code);
        Assert.Equal("签名错误", gateway.Message);
        Assert.Equal("lg-1", gateway.LogId);
    }

    [Fact]
    public void 错误解析对非_JSON_与成功报文应返回零值()
    {
        Assert.Equal(0, ErrorParser.Parse("<html>502 Bad Gateway</html>").Code);
        Assert.Equal(0, ErrorParser.Parse(string.Empty).Code);
        Assert.Equal(0, ErrorParser.Parse("{\"err_no\":0,\"data\":{}}").Code);
    }

    [Fact]
    public void 错误码字符串形式应能兼容()
    {
        // 少数接口把错误码序列化成了字符串
        Assert.Equal(2190008, ErrorParser.Parse("{\"err_no\":\"2190008\"}").Code);
    }

    /// <summary>
    /// 假的凭据实现：每次获取都返回一个新 token，用于验证「刷新后重试」链路。
    /// </summary>
    private sealed class FakeCredential : ICredential
    {
        public int FetchCount;

        public string ClientKey => "test-key";

        public string ClientSecret => "test-secret";

        public Task<AccessTokenInfo> GetClientTokenAsync(bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            FetchCount++;
            return Task.FromResult(AccessTokenInfo.Create("token-" + FetchCount, 7200));
        }

        public Task<AccessTokenInfo> GetAppAccessTokenAsync(bool forceRefresh = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void SetClientToken(string accessToken, long expiresInSeconds) { }

        public void InvalidateClientToken() { }
    }
}
