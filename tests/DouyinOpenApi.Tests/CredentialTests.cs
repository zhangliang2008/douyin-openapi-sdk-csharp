using DouyinOpenApi.Internal;
using Xunit;

namespace DouyinOpenApi.Tests;

/// <summary>
/// 凭据（换取 client_token）相关的报文约束测试。
/// <para>
/// 这里的用例全部来自真实故障：<c>/oauth/client_token/</c> 对 content-type 零容忍，
/// 只要带上 <c>charset=utf-8</c> 就返回 <c>10002 参数错误</c>，
/// 而 token 换不到会让之后**每一个**业务接口都以同样的 10002 失败，且 logid 为空。
/// </para>
/// </summary>
public class CredentialTests
{
    private static DouyinConfig ConfigFor(MockHttpServer server) => new DouyinConfig
    {
        ClientKey = "test-key",
        ClientSecret = "test-secret",
        ClientTokenEndpoint = server.BaseUrl + "/oauth/client_token/",
    };

    [Fact]
    public async Task 换取_token_时_content_type_不得带_charset()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"access_token\":\"clt.abc\",\"expires_in\":7200,\"error_code\":0},\"message\":\"success\"}");

        var credential = new DouyinCredential(ConfigFor(server));
        var token = await credential.GetClientTokenAsync();

        Assert.Equal("clt.abc", token.AccessToken);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/oauth/client_token/", captured.Path);

        // 关键断言：必须严格等于 application/json，多一个参数就会被判 10002
        Assert.Equal("application/json", captured.Headers["Content-Type"]);

        // 请求体字段与 Go SDK 一致
        var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Body);
        Assert.Equal("test-key", (string?)body["client_key"]);
        Assert.Equal("test-secret", (string?)body["client_secret"]);
        Assert.Equal("client_credential", (string?)body["grant_type"]);
    }

    [Fact]
    public async Task 换取_token_失败时应抛出带错误码的异常()
    {
        using var server = new MockHttpServer();
        // 抖音对该接口失败时的真实响应长这样
        server.Enqueue("{\"data\":{\"captcha\":\"\",\"desc_url\":\"\",\"description\":\"client_key 或者client_secret 错误\",\"error_code\":10013},\"message\":\"error\"}");

        var credential = new DouyinCredential(ConfigFor(server));

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() => credential.GetClientTokenAsync());
        Assert.Equal("10013", ex.Code);
        Assert.Contains("client_secret", ex.Message);
    }

    [Fact]
    public async Task 多个客户端实例应共用同一份_token()
    {
        // 这条针对「每次请求 new 一个 DouyinClient」的常见写法：
        // 若缓存挂在实例上，那种写法等于每个请求都换一次 token，与官方「不要频繁调用」冲突。
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"access_token\":\"clt.shared\",\"expires_in\":7200,\"error_code\":0}}");

        var config = ConfigFor(server);
        var a = new DouyinCredential(config);
        var b = new DouyinCredential(ConfigFor(server));

        var ta = await a.GetClientTokenAsync();
        var tb = await b.GetClientTokenAsync();

        Assert.Equal("clt.shared", ta.AccessToken);
        Assert.Equal(ta.AccessToken, tb.AccessToken);
        Assert.Equal(1, server.RequestCount);   // 两个实例总共只请求一次
    }

    [Fact]
    public async Task 并发获取_token_只应发出一次请求()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"access_token\":\"clt.parallel\",\"expires_in\":7200,\"error_code\":0}}");

        var credential = new DouyinCredential(ConfigFor(server));

        // 20 个并发同时要 token，单飞锁应把它们合并成 1 次请求
        var tasks = Enumerable.Range(0, 20).Select(_ => credential.GetClientTokenAsync()).ToArray();
        var tokens = await Task.WhenAll(tasks);

        Assert.All(tokens, t => Assert.Equal("clt.parallel", t.AccessToken));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task 换取失败后冷却期内不应重复请求()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"error_code\":10013,\"description\":\"client_key 或者client_secret 错误\"}}");

        var credential = new DouyinCredential(ConfigFor(server));

        var first = await Assert.ThrowsAsync<DouyinApiException>(() => credential.GetClientTokenAsync());
        Assert.Equal("10013", first.Code);

        // 冷却期内再调用：直接复用上次的失败，不再打接口（避免凭据配错时把 token 接口刷爆）
        var second = await Assert.ThrowsAsync<DouyinApiException>(() => credential.GetClientTokenAsync());

        Assert.Equal("10013", second.Code);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task 注入的_token_应被其它实例复用()
    {
        using var server = new MockHttpServer();

        // 模拟「集群里某个进程换到了 token，写进 Redis，其它进程直接读取注入」
        var producer = new DouyinCredential(ConfigFor(server));
        producer.SetClientToken("clt.from-redis", 7200);

        var consumer = new DouyinCredential(ConfigFor(server));
        var token = await consumer.GetClientTokenAsync();

        Assert.Equal("clt.from-redis", token.AccessToken);
        Assert.Equal(0, server.RequestCount);   // 完全不访问 token 接口
    }

    [Fact]
    public async Task token_应被缓存且过期前不重复请求()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"access_token\":\"clt.abc\",\"expires_in\":7200,\"error_code\":0}}");

        var credential = new DouyinCredential(ConfigFor(server));

        var first = await credential.GetClientTokenAsync();
        var second = await credential.GetClientTokenAsync();

        Assert.Equal(first.AccessToken, second.AccessToken);
        Assert.Equal(1, server.RequestCount);   // 第二次命中缓存，没有新请求
    }

    [Fact]
    public async Task 强制刷新应重新请求()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"data\":{\"access_token\":\"clt.1\",\"expires_in\":7200,\"error_code\":0}}");
        server.Enqueue("{\"data\":{\"access_token\":\"clt.2\",\"expires_in\":7200,\"error_code\":0}}");

        var credential = new DouyinCredential(ConfigFor(server));

        var first = await credential.GetClientTokenAsync();
        var second = await credential.GetClientTokenAsync(forceRefresh: true);

        Assert.Equal("clt.1", first.AccessToken);
        Assert.Equal("clt.2", second.AccessToken);
        Assert.Equal(2, server.RequestCount);
    }
}
