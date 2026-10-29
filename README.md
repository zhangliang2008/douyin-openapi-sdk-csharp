# DouyinOpenApi —— 抖音开放平台 SDK（C# 版）

[![CI](https://github.com/zhangliang2008/douyin-openapi-sdk-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/zhangliang2008/douyin-openapi-sdk-csharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DouyinOpenApi.svg)](https://www.nuget.org/packages/DouyinOpenApi)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

> ## ⚠️ 请先读：项目来源与测试覆盖
>
> **本项目由 AI 生成**（在人工指导下逐轮调试完成）。请按「公共链路可用、具体接口需自测」的预期使用。
>
> | 项 | 实际情况 |
> | --- | --- |
> | 代码来源 | 4127 个模型、639 个接口由 `tools/gen_csharp.py` 从官方 Go SDK 源码**机械翻译**；`Core/` 下的运行时是手写的 |
> | 自动化测试 | 32 项（`net47` + `net8.0` 双跑），但**全部对接本地假服务端**，不发出真实网络请求 |
> | 真实联调过的接口 | 639 个业务接口里**只实测了 1 个**：`GET /goodlife/v1/trade/order/query/`（生活服务订单查询）。另外 `POST /oauth/client_token/`（换取应用级 token）也真实调用过，它属于凭据模块、不在那 639 个之内 |
> | 其余 638 个业务接口 | **从未真实调用验证** |
>
> 换句话说：鉴权、token 换取与缓存、参数绑定、重试、错误归一化这些**公共链路**经过验证；
> 而每个具体接口的**参数位置与字段名**是解析 Go 源码得到的，理论上与官方一致，实际仍以线上行为为准。
>
> 可能遇到的情况：个别接口还要求额外请求头、个别字段线上返回的类型与这里声明的类型不符（反序列化丢字段）、
> 个别接口的参数位置在服务端有过调整。**接入生产前，请先把你实际要用的那几个接口逐一验证一遍。**
>
> 出问题时的自查手段：
> - `catch (DouyinApiException ex) { Console.WriteLine(ex); }` —— 一次打印错误码、`log_id`、实际请求 URL、请求体、原始响应
> - 跑 `samples/DouyinOpenApi.Diagnose`，把「SDK 发出的报文」与「裸 curl 发出的报文」对比
> - 强类型模型对不上时，可用 `CommonOpenApiAsync` 直接发请求并拿原始 JSON（见第 8 节）

对标官方 Go SDK [`github.com/bytedance/douyin-openapi-sdk-go`](https://github.com/bytedance/douyin-openapi-sdk-go)（v1.0.3）的 C# 实现。

| 项目 | 数量 |
| --- | --- |
| 接口方法 | **639 个**（每个都有 `XxxAsync` + 同步 `Xxx` 两个版本） |
| 数据模型 | **4127 个**（请求 / 响应 / 嵌套模型，字段名与官方文档逐字对应） |
| 目标框架 | `net47`（.NET Framework 4.7 及以上原生）、`netstandard2.0`（.NET Core 2.0+ / .NET 5~10） |
| 外部依赖 | 仅 `Newtonsoft.Json` |

> 生成代码约 10 万行，全部由 `tools/gen_csharp.py` 从 Go SDK 源码机械翻译而来；
> 鉴权、重试、序列化、错误归一化等核心逻辑是手写的，见 `src/DouyinOpenApi/Core/`。

---

## 1. 环境要求

- 目标运行时：**.NET Framework 4.7**（含 4.7.1 / 4.7.2 / 4.8）或 **.NET Core 2.0+ / .NET 5+**
- 编译源码需要：.NET SDK 6.0 及以上（已在 SDK 10 上验证）

### 你的项目该拿哪一份 dll

.NET Framework **不向后兼容**（`net471` 的 dll 不能被 4.7.0 项目引用），但**反向兼容**：
`net47` 编出的 dll 可以直接跑在 4.7 / 4.7.1 / 4.7.2 / 4.8 上。
因此只产出一份 `net47`，用一张表覆盖所有情况，NuGet 会自动挑：

| 你的项目 | NuGet 自动选择 | 说明 |
| --- | --- | --- |
| .NET Framework 4.7 ~ 4.8 | `lib/net47` | 原生实现，同一份 dll 通用 |
| .NET Framework 4.6.1 ~ 4.6.2 | `lib/netstandard2.0` | 走 netstandard 兼容层，需注意 binding redirect |
| .NET Core 2.0+ / .NET 5~10 | `lib/netstandard2.0` | 同一份 dll |

已在真实 .NET Framework 运行时上验证：构造客户端、发起 HTTPS 请求、解析平台错误码均正常
（32 项测试在 `net47` 与 `net8.0` 两个目标上全绿）。**注意这是「运行时兼容性」验证，不等于业务接口可用性**——
接口层面的验证范围见文首「项目来源与测试覆盖」。

## 2. 引用方式

**方式一：项目引用**（推荐内部使用）

```xml
<ItemGroup>
  <ProjectReference Include="..\douyin-openapi-sdk-csharp\src\DouyinOpenApi\DouyinOpenApi.csproj" />
</ItemGroup>
```

**方式二：打包后引用**

```bash
dotnet pack src/DouyinOpenApi -c Release
# 得到 bin/Release/DouyinOpenApi.<版本号>.nupkg，放入本地源或私有 NuGet
```

## 3. 快速开始

```csharp
using DouyinOpenApi;
using DouyinOpenApi.Models;

// 只需要 ClientKey / ClientSecret（开放平台控制台「应用详情」里拿）
var client = new DouyinClient("你的ClientKey", "你的ClientSecret");

// 应用级接口：SDK 会自动换取并缓存 client_token，无需手动处理
var resp = await client.ItemListCommentAsync(new ItemListCommentRequest
{
    ItemId = "v0200fg10000abcdefg",
    Count  = 20,
    Cursor = 0,
});

Console.WriteLine($"err_no={resp.ErrNo} log_id={resp.LogId}");
foreach (var item in resp.Data?.Data?.List ?? new List<ItemListCommentResponseDataDataListItem>())
    Console.WriteLine($"{item.NickName}: {item.Content}");
```

同步调用（WinForms / WPF / 老控制台）直接去掉 `Async` 后缀：

```csharp
var resp = client.ItemListComment(new ItemListCommentRequest { ItemId = "视频ID" });
```

方法名与 Go SDK 完全一致（`ItemListComment`、`V2FileUploadMaterial`、`GetCouponMetaStatistics`…），
两边对照文档时不需要做名字映射，IDE 里输入 `client.` 即可看到全部 639 个接口。

## 4. access_token 的三种来源

按优先级依次尝试：

| 优先级 | 来源 | 用法 |
| --- | --- | --- |
| 1 | 请求对象上指定 | `new ItemListCommentRequest { ... }.SetAccessToken("用户 access_token")` |
| 2 | 客户端全局 | `client.AccessToken = "token"` |
| 3 | 自动换取 client_token | 默认开启，用 ClientKey/ClientSecret 调 `/oauth/client_token/` 并缓存 |

用户授权类接口（`/oauth/...`、需要 `open_id` 的接口）必须用第 1 种方式传入**用户** token。
自动换取的 client_token 只代表「应用身份」。

### client_token 的调用频率（官方要求不要频繁调用）

SDK 做了四层保护，正常情况下一个 token 生命周期内**只请求一次**：

| 机制 | 行为 |
| --- | --- |
| 进程级共享缓存 | 缓存键是 `client_key + token 接口地址`，**不挂在实例上**。同一个应用 `new` 出多少个 `DouyinClient` 都共用一份 token |
| 提前刷新 | 按 `expires_in` 提前 60 秒判定"过期"（上限为有效期一半），避免卡在边界上失败后二次换取 |
| 并发合并（单飞） | 每个 token 槽位一把异步锁 + 双检，N 个线程/实例同时缺 token 只会发出 1 次请求 |
| 失败冷却 | 换取失败后 5 秒内不再重试，直接抛上次的异常。凭据填错时不会让每个业务请求都连带打一次 token 接口 |

实测覆盖（`CredentialTests`）：两个客户端实例共用 → 1 次请求；20 个并发 → 1 次请求；
失败后冷却期内不再请求；注入的 token 被其它实例直接复用（0 次请求）。

**多进程 / 多机部署**：进程内缓存只能保证"每个进程一次"，集群规模大时建议集中托管：

```csharp
// 进程启动时从 Redis / 数据库读一份，注入给所有实例（同进程内立即共享，不再请求）
client.Credential.SetClientToken(redisToken, remainingSeconds);

// 或者实现 ICredential，把 token 直接存集中式缓存（约 10 行代码）
client.Credential = new RedisCredential(redis, "douyin:client_token");
```

另外注意：**不要每次请求都 `new DouyinClient`**。虽然共享缓存已经兜住了这种情况，
但复用单例仍能省掉对象构造与 HttpMessageHandler 相关开销。

需要自己托管 token（比如统一配置中心下发）时，可实现 `ICredential` 或直接注入：

```csharp
client.Credential = myCredential;           // 自定义实现
client.Credential.SetClientToken("token", 7200);  // 或塞入已知 token
```

> ⚠️ **换 token 失败会伪装成业务接口报错**
>
> 自动换取 client_token 失败时，SDK 直接抛出该错误，**业务接口根本不会发出**。
> 但历史上出现过 token 接口返回 `10002 参数错误` 的情况，调用方极易误判成"我的参数写错了"。
> 现在这类错误的 Message 会明确写成 `获取 client_token 失败：xxx（client_key=...，接口=...）`，
> 看到这个前缀就去查 ClientKey/ClientSecret 与网络，别再改业务参数。
>
> 另一条已知坑：`/oauth/client_token/` 对 `content-type` **零容忍**——
> `application/json; charset=utf-8` 会被判 `10002`，必须严格是 `application/json`。
> SDK 已按此实现并有回归测试守护（`CredentialTests`）。

## 5. 参数行为（与 Go SDK 对齐，务必读完这一节）

抖音的接口参数位置并不统一：有的在 query，有的在 JSON body，少数在表单/multipart，
还有 **34 个接口同时使用 body 和 query**。这些位置不是靠猜，而是解析 Go 生成代码里
`bodyParam` / `queryParam` 得到的，所以调用方无需关心，传对象即可。

唯一需要留意的是**空值处理**，它按参数位置分成两个开关：

| 位置 | 默认行为 | 开关 |
| --- | --- | --- |
| query（GET 的过滤条件） | **省略**未赋值的字段 | `OmitUnsetQueryParameters`（默认 `true`） |
| body（JSON / 表单） | **补零值**：`string → ""`、数字 → `0`、bool → `false` | `SendNullAsDefaultValue`（默认 `true`） |

为什么两边默认值不同 —— 这是踩了线上故障之后定的：

`GET /goodlife/v1/trade/order/query/` 只需要 `account_id` / `page_num` / `page_size` 三个参数。
若照 Go 的零值规则补全，实际会发出 **12 个参数**：

```
?account_id=...&page_num=1&page_size=10&order_status=0
&create_order_start_time=0&create_order_end_time=0
&update_order_start_time=0&update_order_end_time=0
&ext_order_id=&order_id=&open_id=&get_secret_number=false
```

抖音直接返回 `10002 参数解析错误`；而官方文档的请求示例只带 3 个参数。
查询参数在语义上是"过滤条件"，凭空补 `0`（时间戳 0 = 1970 年）和空串会被服务端当成真实过滤值。

body 侧保持补零值，是因为不少交易类接口对字段完整性有要求，且与官方 Go SDK 逐字节一致更安全。

需要与 Go SDK 完全一致（例如做报文比对）时：

```csharp
client.Options.OmitUnsetQueryParameters = false;   // 查询串回到"补零值"
client.Options.SendNullAsDefaultValue = false;     // body 改为省略空字段
```

## 6. 错误处理

所有失败统一抛 `DouyinApiException`：

| 属性 | 说明 |
| --- | --- |
| `Code` | 业务错误码（如 `"2190008"`）；HTTP 层失败时为状态码字符串 |
| `Message` | 平台返回的 `err_msg` |
| `LogId` | 抖音的 log_id，**提单给官方时必填** |
| `HttpStatusCode` | HTTP 状态码；未拿到响应时为 0 |
| `ResponseBody` | 原始报文，便于事后复盘 |
| `RequestUrl` | **本次请求的完整 URL（含查询串）**，排查 `10002 参数解析错误` 时直接和文档示例对比 |
| `RequestBody` | 本次请求的 body 原文（GET 等无 body 请求为空串） |
| `IsNetworkError` | 是否为 DNS/连接/超时/证书等传输层问题 |
| `IsRetryable` | SDK 是否已对其自动重试 |
| `IsTokenExpired` | 是否为 token 失效类错误（SDK 会自动刷新 token 重试一次） |

```csharp
try
{
    var resp = await client.ItemListCommentAsync(req);
}
catch (DouyinApiException ex)
{
    logger.LogError("调用失败 code={Code} logid={LogId} msg={Msg}", ex.Code, ex.LogId, ex.Message);
    if (ex.IsNetworkError) { /* 可安全重试 */ }
}
```

**单次调用覆盖配置的正确写法**

接口方法都有一个可选的 `runtime` 参数，它的语义是**整体替换**客户端配置。直接
`new RuntimeOptions { ReadTimeout = 8000 }` 会把 `client.Options` 上设过的
`AutoUseClientToken`、`MaxAttempts` 等一并丢回默认值（表现为"明明关掉了自动换 token 却还在换"）。
应当用 `CreateRuntimeOptions()` 继承客户端默认值后再改：

```csharp
var opts = client.CreateRuntimeOptions();   // 继承 client.Options
opts.ReadTimeout = 8000;                    // 只改这次要变的
await client.ItemListCommentAsync(req, opts);
```

**重试策略**：仅对「传输层异常 / 429 / 5xx」重试，默认最多 3 次，指数退避 + 随机抖动。
业务错误（`err_no != 0`）不重试——下单、发券这类接口重放会产生副作用。
token 失效（2190008 / 28001003 / 28001008）会刷新 token 后**额外**重试一次，不占用重试次数。

## 7. 文件上传

5 个 multipart 接口（视频上传、图片上传、素材上传等）用 `FormFile` 传文件：

```csharp
var resp = await client.VideoUploadVideoAsync(new VideoUploadVideoRequest
{
    OpenId = "用户open_id",
    Video  = FormFile.FromFile(@"D:\demo.mp4"),     // 大文件只打开流，不读进内存
});

// 也可从字节数组 / 已有流构造
FormFile.FromBytes(bytes, "cover.jpg");
FormFile.FromStream(stream, "a.mp4", "video/mp4");
```

SDK 不会关闭调用方传入的流，便于失败重试或分片上传复用同一个 `FileStream`。

## 8. 通用调用（新接口未进 SDK 时）

```csharp
var raw = await client.CommonOpenApiAsync(new CommonRequest
{
    Host   = "open.douyin.com",
    Path   = "/api/apps/v1/item/list_comment/",
    Method = "GET",
    Query  = new Dictionary<string, string> { ["item_id"] = "xxx", ["count"] = "10" },
});
// 返回 Dictionary<string, object>，嵌套对象同样是 Dictionary、数组是 List<object>
```

与 Go 的 `CommonOpenAPI` 一致：只有请求头里带 `Content-Type: application/json` 时，
才会把 `Body` 序列化进请求体。

## 9. 目录结构

```
douyin-openapi-sdk-csharp/
├── src/DouyinOpenApi/
│   ├── Core/                      手写核心（约 1.5k 行）
│   │   ├── DouyinClient.cs        执行器：URL 组装、鉴权、重试、错误归一化
│   │   ├── DouyinClient.Common.cs 通用调用入口
│   │   ├── DouyinCredential.cs    token 换取与缓存（并发安全）
│   │   ├── ApiRequest.cs          请求基类（Header / AccessToken）
│   │   ├── RuntimeOptions.cs      超时、重试、空值策略、域名覆盖
│   │   ├── DouyinApiException.cs  统一异常
│   │   ├── FormFile.cs            上传文件封装
│   │   └── Internal/              参数绑定、JSON、错误解析（internal）
│   ├── Models/                    生成：4127 个模型（23 个文件）
│   └── Apis/                      生成：639 个接口（57 个文件，按业务模块分）
├── tests/DouyinOpenApi.Tests/     32 个测试，net47 + net8.0 双跑
├── samples/DouyinOpenApi.Sample/  可运行示例
├── samples/DouyinOpenApi.Diagnose/ 接口报文诊断工具（报错但对不上文档时用）
├── .github/workflows/
│   ├── ci.yml                     构建 + 测试 + 试打包
│   └── release.yml                打 tag 自动发布到 NuGet 并建 Release
├── docs/RELEASING.md              维护者文档：CI、OIDC 发布、排错清单（不随包分发）
├── tools/
│   ├── parse_go.py                解析 Go SDK，产出中间表示
│   └── gen_csharp.py              生成 C# 代码
└── _ref/go-sdk/client/client.go   Go SDK 源码（生成输入，已 gitignore）
```

## 10. 跟随 Go SDK 升级重新生成

```bash
# 1. 拉取最新 Go SDK（放到 _ref/go-sdk）
git clone --depth 1 https://github.com/bytedance/douyin-openapi-sdk-go _ref/go-sdk

# 2. 重新生成（会先清掉旧的生成产物）
python tools/gen_csharp.py

# 3. 编译 + 跑测试
dotnet build && dotnet test
```

生成器会打印解析统计；如果出现「指向未知类型的字段引用」警告，说明 Go 侧新增了 C# 端
没有对应实现的类型，需要去 `Core/` 补一个手写类。

先做解析体检（不改任何文件）也可以单独跑：

```bash
python tools/parse_go.py      # 输出 ir.json + 一组一致性校验结果
```

## 11. 构建与发布（维护者）

CI 状态见页面顶部徽章。构建、测试、打 tag 发布到 nuget.org 的完整流程，以及 OIDC 配置与
踩坑记录，都放在维护者文档里：**[docs/RELEASING.md](docs/RELEASING.md)**。

单独拆出去的原因：本 README 会通过 `<PackageReadmeFile>` 显示在 nuget.org 的包页面上，
发布流程对装包的人是噪音。

想让本仓库发起 PR，先在本机跑：

```bash
dotnet build -c Release
dotnet test  -c Release      # net47 + net8.0 两套运行时
```

## 12. 与 Go SDK 的行为差异（有意为之）

| 项 | Go SDK | 本 SDK | 原因 |
| --- | --- | --- | --- |
| 无 body 请求（GET）的 content-type | 每个请求都写 `application/json` | **默认不写**，发干净的 GET（无 body / 无 Content-Length） | .NET 里给 GET 带 content-type 必然附带 `Content-Length: 0`，抖音网关会把它当成待解析的 JSON 而报 `10002 参数解析错误`。需要时用 `SendJsonContentTypeOnBodylessRequest` 打开 |
| query 空值 | 补零值（未赋值也发 `0` / `""`） | **省略**未赋值字段 | 见第 5 节：补齐后真实接口返回 `10002` |
| `IgnoreSSL` 默认值 | `true`（不校验证书） | `false` | 默认跳过证书校验等于把中间人攻击的口子常开 |
| 重试退避 | tea 默认策略，实际不退避（立即重试） | 指数退避 + 抖动，上限 5s | 立即重试会在对端抖动时形成重试风暴 |
| 重试范围 | 依据 SDKError 的可重试标记 | 仅传输层 / 429 / 5xx | 业务错误重放有副作用（重复下单） |
| 非 200 响应 | 固定文案 `"Reqeust Failed!"` | 带 HTTP 状态码 + 服务端错误体 | 少一次抓包 |
| 错误信息格式 | `"msg=xxx, logid=yyy"` 拼成一个字符串 | `Message` 与 `LogId` 分开，另有 `RequestUrl` | 便于结构化日志与告警 |
| token 过期判断 | `Expire()` 逻辑写反（未过期返回 true） | 按到期时间正确判断 | 修掉官方实现里的 bug |
| 上传流所有权 | 请求结束即关闭 | 不关闭调用方的流 | 支持重试/分片复用 |
| 表单里数组字段 | `fmt.Sprintf("%v")` → `[1 2 3]` | JSON 文本 → `[1,2,3]` | 后者才是合法 JSON，仅影响 5 个表单接口 |
| 100-continue | 不涉及 | 显式关闭 `Expect: 100-continue` | .NET Framework 默认会白等 350ms 才发 body |

字段名、参数位置、body 补零值这三项**未做改动**，与 Go SDK 逐字段一致。

## 13. 故障排查：`10002 参数错误` / `参数解析错误`

这类错误最难查，因为它把「报文格式错」「参数值不对」「网关拒绝路由」都归成同一个码。
按下面的顺序判断，通常三步内能定位：

**第一步：看 `logid` 是否为空** —— 这一步就能把范围砍一半。

| logid | 说明 | 排查方向 |
| --- | --- | --- |
| **有值** | 请求进了业务层，是**参数值**问题 | 对照文档检查必填项、取值范围、时间戳单位（秒非毫秒）、id 是否属于该账户 |
| **为空** | 请求在**网关/路由层**就被挡了，还没进业务逻辑 | 缺必填头、路径或方法不对、应用类型不符 |

**第二步：打印异常即可看到全部上下文**

```csharp
catch (DouyinApiException ex)
{
    Console.WriteLine(ex);   // 自动输出：错误码、请求 URL、请求体、响应原文
}
```

输出形如：

```
DouyinApiException: 参数错误 [err_no=10002] (HttpStatus=200, LogId=-)
  请求: https://open.douyin.com/goodlife/v1/trade/order/query/?page_size=10&page_num=1&account_id=xxx
  响应: {"extra":{"error_code":10002,"description":"参数错误"}}
```

直接拿「请求」那行和文档的请求示例逐参数对比即可。

**第三步：生活服务（goodlife）接口常见原因**

| 现象 | 原因 | 解决 |
| --- | --- | --- |
| 10002 且无 logid | 缺 `Rpc-Transit-Life-Account` 头。生活服务网关靠它定位「来客商户根账户」 | `client.DefaultHeaders["Rpc-Transit-Life-Account"] = "商户根账户ID";` |
| 10002 且无 logid | `account_id` 不是**来客商户根账户ID**（常见于误填抖音号 / 用户 open_id / 小程序 appid） | 到来客后台「店铺管理」复制账户ID |
| 2190004 / 11001 | 不是参数问题：应用未申请该能力 scope，或商家未在来客后台完成第三方应用授权 | 控制台申请 `life.capacity.order.query` 等 scope；让商家授权 |
| 2190002 / 28001008 | access_token 无效/过期 | 用户级接口需传**用户 token**，不能只用自动换取的 client_token |
| 10002 但 Message 以「获取 client_token 失败」开头 | token 都没换到，业务接口未发出 | 查 ClientKey/ClientSecret；若描述是「参数错误」，见第 4 节的 charset 说明（已修） |

`client.DefaultHeaders` 对所有请求生效（请求对象上的 `Header` 优先级更高），
适合服务商场景下每个调用都要带的头，不必逐接口设置。

**第四步：还是查不出来就跑诊断工具**

```bash
set DOUYIN_CLIENT_KEY=你的ClientKey
set DOUYIN_CLIENT_SECRET=你的ClientSecret
set DOUYIN_ACCOUNT_ID=商户根账户ID
set DOUYIN_ACCESS_TOKEN=控制台复制的新鲜token   # 可选但强烈建议
dotnet run --project samples/DouyinOpenApi.Diagnose
```

它会把同一个接口用三种方式各打一遍并对比原始响应：

1. SDK 默认调用
2. 裸 `HttpClient`：只带 `access-token`（等价于纯 curl，无 body / 无 content-type）
3. 裸 `HttpClient`：带 content-type + 空 body（旧版行为，会多出 `Content-Length: 0`）

判读方式：`2` 成功、`3` 失败 → 是 body 的锅；`2`/`3` 都失败 → 是 token 或账号的锅，
工具会直接给出对应错误码的解释。

## 14. 已知限制

- **639 个业务接口里只真实联调过 1 个**（`goodlife/v1/trade/order/query/`），其余仅由自动化测试以本地假服务端
  覆盖「报文形态」，不校验服务端真实行为。范围与自查手段见文首「项目来源与测试覆盖」。
- **生成的字段类型可能与线上返回不完全匹配**：类型由 Go 源码推导（例如 `map[int64][]*int64` →
  `Dictionary<long, List<long>>`），若某个接口线上返回了类型不符的值，该字段会被静默丢弃
  （Newtonsoft.Json 的默认行为），不会抛异常。字段缺失时建议先用 `CommonOpenApiAsync` 取原始 JSON 比对。
- **GET 无法同时拥有 content-type 与「零 body」（.NET 平台限制）**：Content-Type 在 .NET 里属于
  "内容头"，想给 GET 加上就必须挂一个内容体，于是会多出 `Content-Length: 0`（或 chunked）。
  net472 是例外——`HttpWebRequest` 可以只设 `ContentType` 而不带长度，但那要求 GET 不能有内容体
  （`.NET Framework` 的 `CheckProtocol` 会拒绝，抛 `ProtocolViolationException`）。
  因此：默认不发送 content-type；把 `SendJsonContentTypeOnBodylessRequest` 置为 `true` 时，
  net472 走 `HttpWebRequest`（只有 content-type，无 Content-Length，与文档 curl 一致），
  其它框架附带 `Content-Length: 0`。开启该开关后 net472 的这条路径**不经过**
  `DouyinClient.HttpClientFactory` 注入点，代理遵循系统设置。
- **连接超时**：`RuntimeOptions.ConnectTimeout` 仅保留配置项。.NET 的 `HttpClient` 只能设一个
  整体超时，无法单独控制连接阶段；实际由 `ReadTimeout` 约束（net472 上同时写入
  `HttpWebRequest.Timeout` 与 `ReadWriteTimeout`）。
- **`IgnoreSSL` 在 net472 上是全局开关**：走 `ServicePointManager`，会影响进程内其它 HTTP 调用，
  仅建议抓包调试时临时打开。
- **接口返回的 `data.*` 内层错误码不参与判定**：与 Go 的 `GetErrMessage` 保持一致，只解析顶层
  `err_no` / `error_code` / `err.err_code` / `extra.error_code` 四类。
- 生成代码未包含官方文档的中文注释（Go SDK 源码里也没有），字段含义请对照开放平台文档。

## 15. 许可与归属

本项目管理方式与原 Go SDK 一致，遵循 **Apache License 2.0**，全文见 [LICENSE](LICENSE)。

本项目是从 [`bytedance/douyin-openapi-sdk-go`](https://github.com/bytedance/douyin-openapi-sdk-go)
移植而来，`Models/` 与 `Apis/` 下的 4127 个模型、639 个接口签名由 `tools/gen_csharp.py`
从原始 Go 源码机械生成。依 Apache-2.0 第 4(b) 条，改动说明与原始版权声明记录在 [NOTICE](NOTICE) 中。

```
Copyright 2024 ByteDance Ltd. and/or its affiliates.   （原始 Go 实现）
```
