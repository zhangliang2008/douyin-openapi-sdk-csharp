using DouyinOpenApi;
using DouyinOpenApi.Models;
using Newtonsoft.Json;

// ---------------------------------------------------------------------------
// 抖音开放平台 C# SDK 使用示例：查询来客（生活服务）商户订单
//
// 运行前设置三个环境变量：
//     set DOUYIN_CLIENT_KEY=你的ClientKey        （开放平台控制台「应用详情」）
//     set DOUYIN_CLIENT_SECRET=你的ClientSecret
//     set DOUYIN_ACCOUNT_ID=来客商户根账户ID      （来客后台「店铺管理」）
//
// 未设置时只打印用法，不访问网络。
// ---------------------------------------------------------------------------

var clientKey = Env("DOUYIN_CLIENT_KEY");
var clientSecret = Env("DOUYIN_CLIENT_SECRET");
var accountId = Env("DOUYIN_ACCOUNT_ID");

if (clientKey == null || clientSecret == null || accountId == null)
{
    PrintUsage();
    return;
}

// 1) 创建客户端：只需 ClientKey / ClientSecret。
//    token 由 SDK 自动换取并缓存（进程级共享，不会频繁调用 token 接口）。
var client = new DouyinClient(clientKey, clientSecret);

// 2) 生活服务系列接口需要在网关层指明「来客商户根账户」，
//    用 DefaultHeaders 设一次即可对所有请求生效。
client.DefaultHeaders["Rpc-Transit-Life-Account"] = accountId;

try
{
    // 3) 调用接口：参数是强类型对象，字段名与官方文档一一对应。
    //    注意：未赋值的可选参数默认不发送，避免补零值被当成真实过滤条件。
    var resp = client.TradeOrderQuery(new TradeOrderQueryRequest
    {
        AccountId = accountId,
        PageNum = 1,
        PageSize = 10,
    });

    Console.WriteLine($"extra.error_code={resp.Extra?.ErrorCode} logid={resp.Extra?.Logid}");
    Console.WriteLine(JsonConvert.SerializeObject(resp.Data));
}
catch (DouyinApiException ex)
{
    // 4) 所有失败都收敛成 DouyinApiException。
    //    直接打印 ex 会一并输出「实际请求 URL / 请求体 / 原始响应」，
    //    排查 10002 参数错误时靠这几行就能和文档逐参数比对，不必抓包。
    Console.WriteLine("调用失败：");
    Console.WriteLine(ex);
}

/// <summary>读取环境变量，空白视为未设置。</summary>
static string? Env(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>未配置环境变量时打印用法与代码骨架。</summary>
static void PrintUsage()
{
    Console.WriteLine("未检测到必需的环境变量，先执行：");
    Console.WriteLine("    set DOUYIN_CLIENT_KEY=你的ClientKey");
    Console.WriteLine("    set DOUYIN_CLIENT_SECRET=你的ClientSecret");
    Console.WriteLine("    set DOUYIN_ACCOUNT_ID=来客商户根账户ID");
    Console.WriteLine();
    Console.WriteLine("常用调用示例：");
    Console.WriteLine("""
        var client = new DouyinClient(clientKey, clientSecret);

        // 生活服务接口：补一个商户根账户头
        client.DefaultHeaders["Rpc-Transit-Life-Account"] = accountId;

        // 订单查询（同步）
        var resp = client.TradeOrderQuery(new TradeOrderQueryRequest
        {
            AccountId = accountId,
            PageNum   = 1,
            PageSize  = 10,
        });

        // 视频评论列表（异步）
        var comments = await client.ItemListCommentAsync(new ItemListCommentRequest
        {
            ItemId = "视频ID",
            Count  = 20,
        });
        """);

    Console.WriteLine();
    Console.WriteLine("共 639 个接口方法，命名与 Go SDK 保持一致（如 TradeOrderQuery / V2FileUploadMaterial）。");
}
