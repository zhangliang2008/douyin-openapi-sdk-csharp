using System.Net.Http.Headers;
using System.Text;
using DouyinOpenApi;
using DouyinOpenApi.Models;

// =============================================================================
//  抖音接口诊断工具
//
//  用途：当 SDK 调用报错、但控制台/curl 又调得通时，用它把「同一个请求」用三种方式
//        各打一遍，直接定位问题出在 SDK、token 还是账号本身。
//
//  用法（Windows cmd）：
//      set DOUYIN_CLIENT_KEY=你的ClientKey
//      set DOUYIN_CLIENT_SECRET=你的ClientSecret
//      set DOUYIN_ACCOUNT_ID=来客商户根账户ID
//      set DOUYIN_ACCESS_TOKEN=从控制台复制的新鲜 token（可选，强烈建议填）
//      dotnet run --project samples/DouyinOpenApi.Diagnose
//
//  注意：控制台里的 access_token 往往有效期很短且绑定环境，请现取现用。
// =============================================================================

var clientKey = Env("DOUYIN_CLIENT_KEY");
var clientSecret = Env("DOUYIN_CLIENT_SECRET");
var accountId = Env("DOUYIN_ACCOUNT_ID");
var manualToken = Env("DOUYIN_ACCESS_TOKEN");

if (clientKey == null || clientSecret == null || accountId == null)
{
    Console.WriteLine("请先设置 DOUYIN_CLIENT_KEY / DOUYIN_CLIENT_SECRET / DOUYIN_ACCOUNT_ID 环境变量。");
    return;
}

const string Path = "/goodlife/v1/trade/order/query/";
const string Host = "open.douyin.com";
var query = $"account_id={accountId}&page_num=1&page_size=10";
var fullUrl = $"https://{Host}{Path}?{query}";

Console.WriteLine("目标接口: GET " + fullUrl);
Console.WriteLine();

// ---------------------------------------------------------------- 1. 取 token
var client = new DouyinClient(clientKey, clientSecret) { Options = { AutoUseClientToken = false } };
string autoToken = null!;
try
{
    autoToken = (await client.Credential.GetClientTokenAsync()).AccessToken;
    Console.WriteLine($"[1] 自动换取 client_token 成功: {Mask(autoToken)}  长度={autoToken.Length}");
}
catch (Exception ex)
{
    Console.WriteLine("[1] 自动换取 client_token 失败: " + ex.Message);
}

if (manualToken != null)
    Console.WriteLine($"    控制台 token:            {Mask(manualToken)}  长度={manualToken.Length}");

var useToken = manualToken ?? autoToken;
if (useToken == null)
{
    Console.WriteLine("两种 token 都拿不到，无法继续。");
    return;
}

// 关键：把 token 显式交给 client。
// 上面为了单独展示「换取 token」这一步，把 AutoUseClientToken 关掉了；
// 若不在这里补上，[2] 的 SDK 调用会一个 token 都不带，服务端会回 2190002，
// 让人误以为是 SDK 报文有问题（这一步本身踩过坑）。
client.AccessToken = useToken;
Console.WriteLine();

// ---------------------------------------------------------------- 2. SDK 调用
Console.WriteLine("[2] 用 SDK 调用（默认配置）");
try
{
    var resp = await client.TradeOrderQueryAsync(
        new TradeOrderQueryRequest { AccountId = accountId, PageNum = 1, PageSize = 10 });
    Console.WriteLine($"    成功: extra.error_code={resp.Extra?.ErrorCode} logid={resp.Extra?.Logid}");
    Console.WriteLine("    data=" + Newtonsoft.Json.JsonConvert.SerializeObject(resp.Data));
}
catch (DouyinApiException ex)
{
    Console.WriteLine($"    失败: code={ex.Code} msg={ex.Message} logid={ex.LogId} 网络错误={ex.IsNetworkError}");
    Console.WriteLine("    请求: " + ex.RequestUrl);
    Console.WriteLine("    响应: " + ex.ResponseBody);
}
Console.WriteLine();

// ---------------------------------------------------------------- 3. 裸 GET（等价 curl：无 body、无 content-type）
Console.WriteLine("[3] 裸 HttpClient，只带 access-token（等价于纯 curl，无 body / 无 content-type）");
var rawNoCt = await RawGetAsync(fullUrl, useToken, withContentType: false);
Console.WriteLine("    " + Trim(rawNoCt));
Console.WriteLine();

// ---------------------------------------------------------------- 4. 带 content-type 的 GET（旧版 SDK 行为：会多出 Content-Length: 0）
Console.WriteLine("[4] 裸 HttpClient，带 content-type + 空 body（旧版 SDK 行为，多出 Content-Length: 0）");
var rawWithCt = await RawGetAsync(fullUrl, useToken, withContentType: true);
Console.WriteLine("    " + Trim(rawWithCt));
Console.WriteLine();

// ---------------------------------------------------------------- 结论
Console.WriteLine("================= 结论判定 =================");
var ok3 = rawNoCt.Contains("\"error_code\":0") || rawNoCt.Contains("\"err_no\":0");
var ok4 = rawWithCt.Contains("\"error_code\":0") || rawWithCt.Contains("\"err_no\":0");

if (ok3 && ok4)
    Console.WriteLine("→ 裸请求都能成功。若 [2] 仍失败，请把 [2] 的「请求」行发出来，说明是 SDK 附带的头/body 差异。");
else if (ok3 && !ok4)
    Console.WriteLine("→ 确认：带 content-type + 空 body 会被拒（10002）。请把 RuntimeOptions.SendJsonContentTypeOnBodylessRequest 保持默认 false。");
else if (!ok3 && !ok4)
    Console.WriteLine("→ 裸请求也失败：问题不在 HTTP 报文，而在 token 或账号。\n" +
                      "   · 若返回 2190008/2190002 → token 无效/过期（控制台 token 时效很短，请现取）\n" +
                      "   · 若返回 2190004/11001   → 应用未申请 life.capacity.order.query 或商家未授权\n" +
                      "   · 若仍是 10002           → account_id 不是来客商户根账户ID");

// ---------------------------------------------------------------- 本地函数
static string? Env(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

static string Mask(string token)
{
    if (string.IsNullOrEmpty(token)) return "(空)";
    return token.Length <= 16 ? token[..4] + "****" : token[..12] + "..." + token[^8..];
}

static string Trim(string s)
{
    s = s.Replace("\r", " ").Replace("\n", " ");
    return s.Length <= 400 ? s : s[..400] + " ...";
}

static async Task<string> RawGetAsync(string url, string token, bool withContentType)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.TryAddWithoutValidation("access-token", token);

    if (withContentType)
    {
        // 这就是旧版 SDK 的做法：用零长度内容体承载 Content-Type
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    try
    {
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return $"(HTTP {(int)response.StatusCode}) {body}";
    }
    catch (Exception ex)
    {
        return "异常: " + ex.Message;
    }
}
