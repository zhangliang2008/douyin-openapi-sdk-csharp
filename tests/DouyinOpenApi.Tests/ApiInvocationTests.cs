using DouyinOpenApi.Models;
using Xunit;

namespace DouyinOpenApi.Tests;

/// <summary>
/// 端到端测试：SDK 真实发出 HTTP 请求，由本地 mock 服务端捕获后断言报文形状。
/// <para>
/// 这组测试刻意不使用 mock 掉的 HttpMessageHandler，因为绝大多数线上事故
/// 出在「报文长得不对」（查询串拼错、字段进了 body 却应该是 query、少了个头），
/// 只有真的走一遍 HttpClient 才能覆盖到这些细节。
/// </para>
/// </summary>
public class ApiInvocationTests
{
    /// <summary>构造一个把所有请求指向 mock 服务端的客户端。</summary>
    private static (DouyinClient Client, RuntimeOptions Options) CreateClient(MockHttpServer server)
    {
        var client = new DouyinClient("test-key", "test-secret");
        // 关掉自动换取 token，避免测试期间访问真实网络
        client.Options.AutoUseClientToken = false;
        client.AccessToken = "token-abc";

        var options = new RuntimeOptions
        {
            // 覆盖值自带 http:// 协议，SDK 会直接把它当作基地址
            ReadTimeout = 5000,
        };
        options.HostOverrides["open.douyin.com"] = server.BaseUrl;

        return (client, options);
    }

    [Fact]
    public async Task GET_接口的参数应拼到查询串上()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{\"data\":{\"has_more\":false,\"list\":[]}}}");
        var (client, options) = CreateClient(server);

        var request = new ItemListCommentRequest
        {
            ItemId = "item-1",
            Count = 20,
            // Cursor / OpenId / SortType 故意不赋值：默认不应出现在查询串里
        };

        await client.ItemListCommentAsync(request, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("GET", captured.Method);
        Assert.Equal("/api/apps/v1/item/list_comment/", captured.Path);

        var query = captured.QueryParams;
        Assert.Equal("item-1", query["item_id"]);
        Assert.Equal("20", query["count"]);

        // 默认只发送调用方显式赋过值的参数（对齐官方文档的请求示例）
        Assert.False(query.ContainsKey("cursor"));
        Assert.False(query.ContainsKey("open_id"));
        Assert.False(query.ContainsKey("sort_type"));

        // 默认发「干净的 GET」：没有 body、没有 Content-Length、没有 content-type
        // （抖音网关会把带 body 的 GET 当待解析 JSON，空 body 解析失败即 10002）
        Assert.Equal(string.Empty, captured.Body);
        Assert.False(captured.Headers.ContainsKey("Content-Length"));
        Assert.False(captured.Headers.ContainsKey("Content-Type"));

        // 公共头
        Assert.Equal("openapi-sdk", captured.Headers["x-from"]);
        Assert.Equal("token-abc", captured.Headers["access-token"]);
    }

    [Fact]
    public async Task 关闭省略开关后查询串应回到_Go_的补零值行为()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{\"data\":{\"has_more\":false,\"list\":[]}}}");
        var (client, options) = CreateClient(server);
        options.OmitUnsetQueryParameters = false;   // 需要与 Go SDK 逐字节一致时使用

        await client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "item-1", Count = 20 }, options);

        var query = Assert.Single(server.Requests).QueryParams;
        Assert.Equal("0", query["cursor"]);        // Go: Int64Value(nil) -> 0
        Assert.Equal(string.Empty, query["open_id"]); // Go: StringValue(nil) -> ""
    }

    [Fact]
    public async Task 生活服务订单查询只发三个必填参数()
    {
        // 这个用例来自线上真实故障：account_id/page_num/page_size 之外的 9 个可选参数
        // 若被补成 0 或空串发给抖音，会返回 10002 参数解析错误。
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        await client.TradeOrderQueryAsync(new TradeOrderQueryRequest
        {
            AccountId = "test-account-0001",
            PageNum = 1,
            PageSize = 10,
        }, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("/goodlife/v1/trade/order/query/", captured.Path);
        Assert.Equal(3, captured.QueryParams.Count);
        Assert.Equal("test-account-0001", captured.QueryParams["account_id"]);
        Assert.Equal("1", captured.QueryParams["page_num"]);
        Assert.Equal("10", captured.QueryParams["page_size"]);
        // 与抖音文档的 curl 示例一致：GET 不带 body、不带 Content-Length
        Assert.Equal(string.Empty, captured.Body);
        Assert.False(captured.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task 显式开启后_bodyless_请求应带上_content_type()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);
        options.SendJsonContentTypeOnBodylessRequest = true;

        await client.TradeOrderQueryAsync(
            new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("application/json", captured.Headers["Content-Type"]);
    }

    [Fact]
    public async Task 业务错误应带出请求_URL_便于自查参数()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":10002,\"err_msg\":\"参数解析错误\"}");
        var (client, options) = CreateClient(server);

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
            client.TradeOrderQueryAsync(new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options));

        Assert.Equal("10002", ex.Code);
        Assert.Contains("/goodlife/v1/trade/order/query/", ex.RequestUrl);
        Assert.Contains("account_id=a", ex.RequestUrl);
    }

    [Fact]
    public async Task POST_接口的参数应作为_JSON_体发送()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        var request = new GetCouponMetaStatisticsRequest
        {
            AppId = "app-1",
            CouponMetaId = "meta-9",
        };

        await client.GetCouponMetaStatisticsAsync(request, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("/api/promotion/v1/coupon/get_coupon_meta_statistics/", captured.Path);
        Assert.Contains("application/json", captured.Headers["Content-Type"]);

        var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Body);
        Assert.Equal("app-1", (string?)body["app_id"]);
        Assert.Equal("meta-9", (string?)body["coupon_meta_id"]);

        // 未赋值字段补零值：与 Go 的 tea.StringValue(nil) 行为一致
        Assert.Equal(string.Empty, (string?)body["activity_id"]);
        Assert.Equal(string.Empty, (string?)body["talent_account"]);
        Assert.Equal(string.Empty, (string?)body["talent_open_id"]);

        // access_token 只出现在头部，不应混进业务体
        Assert.Null(body["access_token"]);
    }

    [Fact]
    public async Task 关闭补零值后空字段应被省略()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);
        options.SendNullAsDefaultValue = false;

        await client.GetCouponMetaStatisticsAsync(
            new GetCouponMetaStatisticsRequest { AppId = "app-1", CouponMetaId = "meta-9" }, options);

        var body = Newtonsoft.Json.Linq.JObject.Parse(Assert.Single(server.Requests).Body);
        Assert.Equal(2, body.Count);
        Assert.Null(body["activity_id"]);
    }

    [Fact]
    public async Task body_与_query_混用的接口应各归其位()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        // QualSearch 的 account_id 走 query，data 走 JSON 体
        await client.QualSearchAsync(new QualSearchRequest
        {
            AccountId = 12345,
            Data = new QualSearchRequestData
            {
                PoiIds = new List<long?> { 1, 2 },
                QualIds = new List<long?> { 3 },
            }
        }, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("POST", captured.Method);
        Assert.Equal("12345", captured.QueryParams["account_id"]);

        var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Body);
        Assert.Null(body["account_id"]);      // 不能重复进 body
        Assert.NotNull(body["data"]);
        Assert.Equal(2, body["data"]!["poi_ids"]!.Count());
    }

    [Fact]
    public async Task multipart_上传应带文件名与表单字段()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        var request = new V2FileUploadMaterialRequest
        {
            MaterialType = 1,
            MaterialFile = FormFile.FromBytes(new byte[] { 1, 2, 3, 4 }, "cover.jpg"),
        };

        await client.V2FileUploadMaterialAsync(request, options);

        var captured = Assert.Single(server.Requests);
        Assert.Contains("multipart/form-data", captured.Headers["Content-Type"]);
        // .NET 的 ContentDisposition 对简单 token 不加引号，因此这里不断言引号形式
        Assert.Contains("name=material_type", captured.Body);
        Assert.Contains("name=material_file", captured.Body);
        Assert.Contains("filename=cover.jpg", captured.Body);
        Assert.Contains("image/jpeg", captured.Body);
    }

    [Fact]
    public async Task 响应应能反序列化成强类型模型()
    {
        using var server = new MockHttpServer();
        server.Enqueue("""
        {
          "err_no": 0,
          "err_msg": "",
          "log_id": "log-xyz",
          "data": {
            "data": {
              "has_more": true,
              "cursor": 1730000000123,
              "list": [
                { "comment_id": "c-1", "content": "你好", "digg_count": 12 },
                { "comment_id": "c-2", "content": "hi", "digg_count": 7 }
              ]
            },
            "extra": { "now": 1730000000 }
          }
        }
        """);
        var (client, options) = CreateClient(server);

        var resp = await client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "i" }, options);

        Assert.Equal(0, resp.ErrNo);
        Assert.Equal("log-xyz", resp.LogId);
        Assert.True(resp.Data!.Data!.HasMore);
        Assert.Equal(1730000000123L, resp.Data.Data.Cursor);      // 超出 int 范围，验证 long 未被截断
        Assert.Equal(2, resp.Data.Data.List!.Count);
        Assert.Equal("c-2", resp.Data.Data.List![1].CommentId);
        Assert.Equal(7, resp.Data.Data.List[1].DiggCount);
        Assert.Equal(1730000000L, resp.Data.Extra!.Now);
    }

    [Fact]
    public void 同步方法应可直接调用()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{\"data\":{\"has_more\":false,\"list\":[]}}}");
        var (client, options) = CreateClient(server);

        // WinForms / 控制台场景常见的同步调用方式
        var resp = client.ItemListComment(new ItemListCommentRequest { ItemId = "i" }, options);

        Assert.NotNull(resp);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task 自定义请求头应生效且不覆盖关键头()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);

        var request = new GetCouponMetaStatisticsRequest { AppId = "a", CouponMetaId = "m" };
        request.SetHeader("X-Trace-Id", "trace-1");
        // 试图覆盖 SDK 关键头，应不允许
        request.SetHeader("access-token", "hacker");

        await client.GetCouponMetaStatisticsAsync(request, options);

        var captured = Assert.Single(server.Requests);
        Assert.Equal("trace-1", captured.Headers["X-Trace-Id"]);
        // SDK 最后写入 access-token，调用方的同名头会被覆盖
        Assert.Equal("token-abc", captured.Headers["access-token"]);
    }

    [Fact]
    public async Task 客户端默认头应对每个请求生效()
    {
        // 生活服务系列接口需要 Rpc-Transit-Life-Account 指明商户根账户，逐个接口设置太啰嗦
        using var server = new MockHttpServer();
        server.EnqueueTimes(2, "{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);
        client.DefaultHeaders["Rpc-Transit-Life-Account"] = "test-account-0001";

        await client.TradeOrderQueryAsync(
            new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options);
        await client.TradeOrderQueryAsync(
            new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options);

        Assert.Equal(2, server.RequestCount);
        foreach (var captured in server.Requests)
            Assert.Equal("test-account-0001", captured.Headers["Rpc-Transit-Life-Account"]);
    }

    [Fact]
    public async Task 请求级头应覆盖客户端默认头()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"err_no\":0,\"data\":{}}");
        var (client, options) = CreateClient(server);
        client.DefaultHeaders["Rpc-Transit-Life-Account"] = "default-account";

        var request = new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 };
        request.SetHeader("Rpc-Transit-Life-Account", "per-request-account");

        await client.TradeOrderQueryAsync(request, options);

        Assert.Equal("per-request-account",
            Assert.Single(server.Requests).Headers["Rpc-Transit-Life-Account"]);
    }

    [Fact]
    public async Task 异常应带出请求与响应上下文()
    {
        using var server = new MockHttpServer();
        server.Enqueue("{\"extra\":{\"error_code\":10002,\"description\":\"参数错误\"}}");
        var (client, options) = CreateClient(server);

        var ex = await Assert.ThrowsAsync<DouyinApiException>(() =>
            client.TradeOrderQueryAsync(new TradeOrderQueryRequest { AccountId = "a", PageNum = 1, PageSize = 10 }, options));

        var text = ex.ToString();
        Assert.Contains("请求: ", text);
        Assert.Contains("account_id=a", text);
        Assert.Contains("响应: ", text);
        Assert.Contains("10002", text);
    }
}
