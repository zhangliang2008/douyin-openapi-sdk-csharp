using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DouyinOpenApi.Internal;

namespace DouyinOpenApi
{
    /// <summary>
    /// 抖音开放平台客户端。全部 640 个接口方法由代码生成器写入 <c>Apis/</c> 下的分部类，
    /// 本文件只负责与业务无关的通用能力：URL 组装、鉴权、参数绑定、重试、错误归一化。
    /// <para>典型用法：</para>
    /// <code>
    /// var client = new DouyinClient("你的ClientKey", "你的ClientSecret");
    /// var resp = await client.ItemListCommentAsync(new ItemListCommentRequest { ItemId = "xxx" });
    /// </code>
    /// </summary>
    public sealed partial class DouyinClient
    {
        /// <summary>
        /// 共享 HttpClient。复用同一个实例可以避免 TIME_WAIT 端口耗尽（每次 new 一个是最常见的坑）。
        /// </summary>
        internal static readonly HttpClient SharedHttpClient = CreateHttpClient(false);

        /// <summary>跳过证书校验的 HttpClient，仅在 <see cref="RuntimeOptions.IgnoreSsl"/> 打开时使用。</summary>
        private static readonly HttpClient InsecureHttpClient = CreateHttpClient(true);

        /// <summary>
        /// 判定为「token 失效」的业务错误码。命中后会刷新 token 并重试一次。
        /// <list type="bullet">
        ///   <item><description>2190008：access_token 过期</description></item>
        ///   <item><description>28001003 / 28001008：token 无效或已失效</description></item>
        /// </list>
        /// </summary>
        private static readonly HashSet<string> TokenExpiredCodes =
            new HashSet<string>(StringComparer.Ordinal) { "2190008", "28001003", "28001008" };

        /// <summary>
        /// 「没带 token」时需要补提示的错误码：令牌无效/未授权类。
        /// </summary>
        private static readonly HashSet<string> TokenMissingHintCodes =
            new HashSet<string>(StringComparer.Ordinal) { "2190002", "2190004", "11001", "11002", "28001003", "28001008" };

        /// <summary>退避抖动用的随机数源（Random 非线程安全，需加锁）。</summary>
        private static readonly Random JitterRandom = new Random();
        private static readonly object JitterLock = new object();

        /// <summary>
        /// 获取 HttpClient 的工厂，入参为「是否跳过证书校验」。
        /// <para>
        /// 默认返回内部共享的两个实例；暴露为可替换主要是两个用途：
        /// ① 单元测试注入会抛异常 / 记录请求的 Handler；
        /// ② 生产环境需要走自建代理、或要挂自定义 <c>DelegatingHandler</c> 做链路追踪。
        /// </para>
        /// </summary>
        internal static Func<bool, HttpClient> HttpClientFactory { get; set; }

        /// <summary>实际取用 HttpClient。</summary>
        private static HttpClient GetHttpClient(bool ignoreSsl)
        {
            var factory = HttpClientFactory;
            return factory != null ? factory(ignoreSsl) : (ignoreSsl ? InsecureHttpClient : SharedHttpClient);
        }

        /// <summary>
        /// 静态构造：.NET Framework 4.7 默认可能不启用 TLS 1.2，而抖音接口强制要求，
        /// 这里主动打开，避免出现 "基础连接已经关闭: 发送时发生错误" 这类隐蔽报错。
        /// </summary>
        static DouyinClient()
        {
#if NETFRAMEWORK
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // 极老的操作系统不支持 TLS 1.2，忽略即可，让后续请求自己报错
            }
#endif
        }

        /// <summary>
        /// 用 ClientKey / ClientSecret 快速构造客户端。
        /// </summary>
        /// <param name="clientKey">应用 ClientKey（AppId）。</param>
        /// <param name="clientSecret">应用 ClientSecret。</param>
        public DouyinClient(string clientKey, string clientSecret)
            : this(new DouyinConfig { ClientKey = clientKey, ClientSecret = clientSecret }, null)
        {
        }

        /// <summary>
        /// 用配置对象构造客户端，使用默认运行时选项。
        /// </summary>
        /// <param name="config">应用凭据配置。</param>
        public DouyinClient(DouyinConfig config)
            : this(config, null)
        {
        }

        /// <summary>
        /// 用配置对象与运行时选项构造客户端。
        /// </summary>
        /// <param name="config">应用凭据配置。</param>
        /// <param name="options">全局默认运行时选项；为空时使用内置默认值。</param>
        public DouyinClient(DouyinConfig config, RuntimeOptions options)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            Config = config;
            Options = options ?? new RuntimeOptions();
            Credential = new DouyinCredential(config);
        }

        /// <summary>应用凭据配置。</summary>
        public DouyinConfig Config { get; }

        /// <summary>
        /// 全局默认运行时选项。可在运行期调整（例如统一放宽超时），不影响已在执行的请求。
        /// </summary>
        public RuntimeOptions Options { get; set; }

        /// <summary>
        /// 凭据提供者。默认实现会自动换取并缓存 client_token；测试时可替换为自定义实现。
        /// </summary>
        public ICredential Credential { get; set; }

        /// <summary>
        /// 客户端级 access_token。设置后，所有未显式指定 token 的请求都会使用它
        /// （优先级高于自动换取的 client_token）。
        /// </summary>
        public string AccessToken { get; set; }

        /// <summary>
        /// 客户端级默认请求头，会对**每个**请求生效（请求对象上的 Header 优先级更高）。
        /// <para>
        /// 典型用途：抖音生活服务（goodlife）系列接口要求带 <c>Rpc-Transit-Life-Account</c>
        /// 头来指明「来客商户根账户ID」，缺它时网关会在路由层直接拒绝，
        /// 返回的正是 <c>10002 参数错误</c> 且 logid 为空（不是业务层报错，所以没有 logid）。
        /// </para>
        /// <code>
        /// client.DefaultHeaders["Rpc-Transit-Life-Account"] = "你的来客商户根账户ID";
        /// </code>
        /// </summary>
        public IDictionary<string, string> DefaultHeaders { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 基于客户端当前的默认配置，派生一个可用于单次调用的 <see cref="RuntimeOptions"/>。
        /// <para>
        /// 为什么需要它：接口上的 <c>runtime</c> 参数是<b>整体替换</b>语义，
        /// 若直接 <c>new RuntimeOptions { ReadTimeout = 8000 }</c>，
        /// 客户端上设置过的 <see cref="RuntimeOptions.AutoUseClientToken"/> 等会全部回到默认值，
        /// 表现为「明明关掉了自动换 token 却还在换」「明明设了 MaxAttempts 却还是 3 次」这类
        /// 难以定位的问题（实测踩过）。正确写法：
        /// </para>
        /// <code>
        /// var opts = client.CreateRuntimeOptions();   // 继承客户端默认值
        /// opts.ReadTimeout = 8000;                    // 只改这次要变的
        /// await client.ItemListCommentAsync(req, opts);
        /// </code>
        /// </summary>
        public RuntimeOptions CreateRuntimeOptions()
        {
            return (Options ?? new RuntimeOptions()).Clone();
        }

        /// <summary>
        /// 所有接口的统一执行入口。
        /// </summary>
        /// <typeparam name="TResponse">响应模型类型。</typeparam>
        /// <param name="request">请求对象（承载业务参数）。</param>
        /// <param name="host">域名，如 <c>open.douyin.com</c>。</param>
        /// <param name="path">接口路径，如 <c>/api/apps/v1/item/list_comment/</c>。</param>
        /// <param name="httpMethod">HTTP 方法。</param>
        /// <param name="location">参数主位置。</param>
        /// <param name="extraQueryFields">额外走 query 的属性名（body/query 混用的接口使用）。</param>
        /// <param name="accessTokenInBody">是否把 access_token 也放进 body（少数接口要求）。</param>
        /// <param name="runtimeOptions">本次调用的运行时选项；为空则使用 <see cref="Options"/>。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        internal async Task<TResponse> ExecuteCoreAsync<TResponse>(
            ApiRequest request,
            string host,
            string path,
            string httpMethod,
            ParamLocation location,
            string[] extraQueryFields,
            bool accessTokenInBody,
            RuntimeOptions runtimeOptions,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var options = runtimeOptions ?? Options ?? new RuntimeOptions();
            var url = BuildUrl(host, path, options);

            // ---------- 1. 解析 access_token ----------
            var token = request.AccessToken;
            if (string.IsNullOrEmpty(token)) token = AccessToken;
            var tokenFromCredential = false;
            if (string.IsNullOrEmpty(token) && options.AutoUseClientToken && Credential != null)
            {
                var info = await Credential.GetClientTokenAsync(false, cancellationToken).ConfigureAwait(false);
                if (info != null) token = info.AccessToken;
                tokenFromCredential = true;
            }

            // ---------- 2. 绑定参数 ----------
            var bound = ParamBinder.Bind(request, location, extraQueryFields, token, accessTokenInBody, options);
            var queryString = ParamBinder.BuildQueryString(bound.Query);
            if (queryString.Length > 0) url = url + "?" + queryString;

            // JSON / 表单体可以预计算成字节数组，重试时直接复用；multipart 每次都要重建（流有状态）
            byte[] payload = null;
            if (location == ParamLocation.JsonBody)
            {
                payload = Encoding.UTF8.GetBytes(DouyinJson.Serialize(bound.Body));
            }
            else if (location == ParamLocation.FormUrlEncoded)
            {
                payload = Encoding.UTF8.GetBytes(EncodeForm(bound, options));
            }

            // 默认头 + 请求头合并（请求级优先），每个请求都带上
            var mergedHeaders = MergeHeaders(DefaultHeaders, request.Header);

            var effectiveToken = token;
            var maxAttempts = Math.Max(1, options.MaxAttempts);
            var attempt = 0;
            var tokenRefreshed = false;

            while (true)
            {
                try
                {
                    return await SendOnceAsync<TResponse>(
                        url, httpMethod, location, bound, payload, mergedHeaders, effectiveToken,
                        options, cancellationToken).ConfigureAwait(false);
                }
                catch (DouyinApiException ex)
                {
                    // ---- 分支 0：请求压根没带 token，却收到「token 无效/未授权」----
                    // 这种情况最容易被误判成业务参数问题（实测踩过），补一句提示。
                    if (string.IsNullOrEmpty(effectiveToken) && TokenMissingHintCodes.Contains(ex.Code))
                    {
                        throw ex.WithHint("（提示：本次请求未携带 access_token —— 请求对象与客户端均未设置，"
                                          + "且 AutoUseClientToken 为 false 或凭据不可用）");
                    }

                    // ---- 分支 A：token 失效且 token 是自动获取的 → 刷新后重试（不占用重试次数）----
                    if (ex.IsTokenExpired && tokenFromCredential && !tokenRefreshed
                        && options.RefreshTokenOnExpiredError && Credential != null)
                    {
                        tokenRefreshed = true;
                        Credential.InvalidateClientToken();
                        var info = await Credential.GetClientTokenAsync(true, cancellationToken).ConfigureAwait(false);
                        effectiveToken = info?.AccessToken;
                        if (accessTokenInBody && !string.IsNullOrEmpty(effectiveToken))
                            bound.Body["access_token"] = effectiveToken;
                        continue;
                    }

                    // ---- 分支 B：可重试错误（网络异常 / 429 / 5xx）----
                    attempt++;
                    if (!ex.IsRetryable || attempt >= maxAttempts) throw;

                    await Task.Delay(ComputeBackoff(attempt, options), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 发起一次 HTTP 请求并把响应归一化成响应模型或异常。
        /// </summary>
        /// <typeparam name="TResponse">响应模型类型。</typeparam>
        /// <param name="url">完整 URL（已含 query）。</param>
        /// <param name="httpMethod">HTTP 方法。</param>
        /// <param name="location">参数位置，决定 content 的形态。</param>
        /// <param name="bound">已绑定的参数。</param>
        /// <param name="payload">预计算的 JSON / 表单字节；multipart 时为 null。</param>
        /// <param name="userHeaders">调用方自定义的请求头。</param>
        /// <param name="accessToken">access_token，空则不写该头。</param>
        /// <param name="options">运行时选项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        private static async Task<TResponse> SendOnceAsync<TResponse>(
            string url,
            string httpMethod,
            ParamLocation location,
            ParamBinder.BoundParams bound,
            byte[] payload,
            Dictionary<string, string> userHeaders,
            string accessToken,
            RuntimeOptions options,
            CancellationToken cancellationToken)
        {
            var bodyless = location == ParamLocation.Query || location == ParamLocation.None;

#if NETFRAMEWORK
            // .NET Framework 的 HttpWebRequest 在 CheckProtocol 里禁止 GET/HEAD 携带任何内容体
            // （哪怕长度是 0），会抛 ProtocolViolationException。当调用方显式要求 bodyless 请求
            // 也带 content-type 时，只能改走 HttpWebRequest：它允许给 GET 设置 ContentType 头，
            // 且不会像 HttpClient 那样附带 Content-Length: 0。
            if (bodyless && options.SendJsonContentTypeOnBodylessRequest)
            {
                return await SendBodylessViaHttpWebRequestAsync<TResponse>(
                    url, httpMethod, userHeaders, accessToken, options, cancellationToken).ConfigureAwait(false);
            }
#endif

            var content = CreateContent(location, bound, payload, options);

            var httpClient = GetHttpClient(options.IgnoreSsl);

            // HttpClient 只有单一 Timeout，这里用 CTS 实现"单次请求整体超时"
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.ReadTimeout > 0) timeoutCts.CancelAfter(options.ReadTimeout);

                using (var httpRequest = new HttpRequestMessage(new HttpMethod(httpMethod), url))
                {
                    httpRequest.Content = content;

                    // 先写入调用方自定义头（与 Go SDK 一致：header 字典作为基础）
                    ApplyUserHeaders(httpRequest, content, userHeaders);

                    // 关掉 100-continue：.NET Framework 默认会先发 Expect 头等服务端确认，
                    // 服务端不响应时每次请求白等 350ms；抖音接口也不需要这个协商。
                    httpRequest.Headers.ExpectContinue = false;

                    // SDK 关键头最后写入，保证不被调用方覆盖（Go 侧是 map 赋值，天然覆盖）
                    SetHeader(httpRequest, "x-from", "openapi-sdk");
                    if (!string.IsNullOrEmpty(accessToken))
                        SetHeader(httpRequest, "access-token", accessToken);

                    HttpResponseMessage response;
                    try
                    {
                        response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 调用方没取消，说明是本地超时
                        throw WithRequestContext(new DouyinApiException(
                            string.Format(CultureInfo.InvariantCulture, "请求超时（{0} ms）。", options.ReadTimeout),
                            new TimeoutException("请求超时"), isRetryable: true), url, payload);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw WithRequestContext(new DouyinApiException("网络请求失败：" + ex.Message, ex, isRetryable: true), url, payload);
                    }

                    using (response)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ProcessResponse<TResponse>(url, payload, (int)response.StatusCode, body);
                    }
                }
            }
        }

        /// <summary>
        /// 把「HTTP 状态码 + 响应原文」归一化成响应模型或 <see cref="DouyinApiException"/>。
        /// <para>
        /// 两条传输路径（HttpClient / HttpWebRequest）共用这一段，避免错误判定逻辑写两遍而漂移。
        /// </para>
        /// </summary>
        /// <param name="url">本次请求 URL，用于异常里回带。</param>
        /// <param name="payload">本次请求体字节（无 body 时为 null）。</param>
        /// <param name="status">HTTP 状态码。</param>
        /// <param name="body">响应报文原文。</param>
        private static TResponse ProcessResponse<TResponse>(string url, byte[] payload, int status, string body)
        {
            if (status != 200)
            {
                // Go SDK 此处直接抛 "Reqeust Failed!" + code=状态码；
                // 这里额外带出平台返回的 err_msg / log_id，定位问题更快
                var httpEx = new DouyinApiException(
                    string.IsNullOrEmpty(body)
                        ? string.Format(CultureInfo.InvariantCulture, "请求失败，HTTP {0}。", status)
                        : string.Format(CultureInfo.InvariantCulture, "请求失败，HTTP {0}：{1}", status, Truncate(body, 512)),
                    status.ToString(CultureInfo.InvariantCulture))
                {
                    HttpStatusCode = status,
                    ResponseBody = body,
                    IsRetryable = status == 429 || status >= 500
                };
                throw WithRequestContext(httpEx, url, payload);
            }

            // 业务错误：HTTP 200 但 err_no != 0
            var error = ErrorParser.Parse(body);
            if (error.Code != 0)
            {
                var code = error.Code.ToString(CultureInfo.InvariantCulture);
                var message = string.IsNullOrEmpty(error.Message)
                    ? string.Format(CultureInfo.InvariantCulture, "接口返回错误码 {0}。", code)
                    : error.Message;
                throw WithRequestContext(new DouyinApiException(message, code)
                {
                    HttpStatusCode = status,
                    LogId = error.LogId,
                    ResponseBody = body,
                    IsRetryable = false,
                    IsTokenExpired = TokenExpiredCodes.Contains(code)
                }, url, payload);
            }

            if (typeof(TResponse) == typeof(string))
                return (TResponse)(object)body;

            if (string.IsNullOrEmpty(body)) return default(TResponse);

            try
            {
                return DouyinJson.Deserialize<TResponse>(body);
            }
            catch (Exception ex)
            {
                throw WithRequestContext(new DouyinApiException(
                    "响应报文解析失败：" + ex.Message, ex, isRetryable: false)
                {
                    HttpStatusCode = status,
                    ResponseBody = body
                }, url, payload);
            }
        }

#if NETFRAMEWORK
        /// <summary>
        /// 无 body 请求的 .NET Framework 传输实现。
        /// <para>
        /// 之所以不能让 HttpClient 代劳：HttpWebRequest（.NET Framework 上 HttpClient 的底层实现）
        /// 会在 CheckProtocol 阶段拒绝「GET 带内容体」，连零长度也会抛
        /// <see cref="ProtocolViolationException"/>，因此无法通过 HttpContent 携带 content-type。
        /// 直接用 HttpWebRequest 则可以把 ContentType 当普通头设置，不涉及请求流。
        /// </para>
        /// </summary>
        private static async Task<TResponse> SendBodylessViaHttpWebRequestAsync<TResponse>(
            string url,
            string httpMethod,
            Dictionary<string, string> userHeaders,
            string accessToken,
            RuntimeOptions options,
            CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = httpMethod;

            var timeout = options.ReadTimeout > 0 ? options.ReadTimeout : 100000;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;

            // 与 HttpClient 路径保持一致：不做 100-continue 协商（否则可能白等 350ms）
            try { request.ServicePoint.Expect100Continue = false; } catch (Exception) { /* 极少数环境不可设 */ }

            // 抖音要求 content-type 为固定值 application/json（即使 GET 没有 body）
            request.ContentType = "application/json";

            if (options.IgnoreSsl)
                request.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;

            // 调用方自定义头（受限头跳过，交给运行时）
            if (userHeaders != null)
            {
                foreach (var pair in userHeaders)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pair.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pair.Key, "Expect", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        request.ContentType = pair.Value;
                        continue;
                    }

                    // HttpWebRequest.Headers 对部分受限头会抛异常，这里保守跳过
                    try { request.Headers[pair.Key] = pair.Value; } catch (ArgumentException) { }
                }
            }

            // SDK 关键头最后写入（覆盖语义）
            request.Headers["x-from"] = "openapi-sdk";
            if (!string.IsNullOrEmpty(accessToken))
                request.Headers["access-token"] = accessToken;

            using (cancellationToken.Register(() => { try { request.Abort(); } catch { /* 已结束 */ } }))
            {
                try
                {
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);
                        return ProcessResponse<TResponse>(url, null, (int)response.StatusCode, body);
                    }
                }
                catch (WebException ex)
                {
                    // 4xx/5xx 会走异常分支，但响应体里往往有 err_msg，需要拿出来
                    var errorResponse = ex.Response as HttpWebResponse;
                    if (errorResponse != null)
                    {
                        using (errorResponse)
                        {
                            var body = await ReadResponseBodyAsync(errorResponse).ConfigureAwait(false);
                            return ProcessResponse<TResponse>(url, null, (int)errorResponse.StatusCode, body);
                        }
                    }

                    // 调用方主动取消时，Abort 会以 WebException(RequestCanceled) 冒出来，
                    // 要转换回标准语义，否则调用方 catch OperationCanceledException 会漏掉
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    // 超时/连接失败等可重试；证书、协议错误不可重试
                    var retryable =
                        ex.Status == WebExceptionStatus.Timeout ||
                        ex.Status == WebExceptionStatus.ConnectFailure ||
                        ex.Status == WebExceptionStatus.ConnectionClosed ||
                        ex.Status == WebExceptionStatus.KeepAliveFailure ||
                        ex.Status == WebExceptionStatus.ReceiveFailure ||
                        ex.Status == WebExceptionStatus.SendFailure ||
                        ex.Status == WebExceptionStatus.NameResolutionFailure ||
                        ex.Status == WebExceptionStatus.ProxyNameResolutionFailure;

                    throw WithRequestContext(new DouyinApiException(
                        "网络请求失败：" + ex.Message, ex, isRetryable: retryable), url, null);
                }
            }
        }

        /// <summary>读取 <see cref="HttpWebResponse"/> 的响应体文本。</summary>
        private static async Task<string> ReadResponseBodyAsync(HttpWebResponse response)
        {
            var stream = response.GetResponseStream();
            if (stream == null) return string.Empty;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }
#endif

        /// <summary>
        /// 按参数位置创建请求体。
        /// <para>
        /// 注意 <see cref="ParamLocation.Query"/> / <see cref="ParamLocation.None"/> 也返回一个
        /// <b>零长度</b>的 JSON 内容体：抖音生活服务等接口把 <c>content-type: application/json</c>
        /// 列为**必填请求头**（即使 GET 没有任何 body）。.NET 里 Content-Type 属于"内容头"，
        /// 没有 Content 就挂不上去，所以这里用空内容承载这个头——官方 Go SDK 同样是每个请求都写
        /// <c>content-type: application/json</c>，缺了它网关会直接返回 10002「参数解析错误」。
        /// </para>
        /// </summary>
        private static HttpContent CreateContent(
            ParamLocation location,
            ParamBinder.BoundParams bound,
            byte[] payload,
            RuntimeOptions options)
        {
            switch (location)
            {
                case ParamLocation.JsonBody:
                    return new ByteArrayContent(payload ?? EmptyPayload)
                    {
                        Headers = { ContentType = NewJsonMediaType() }
                    };

                case ParamLocation.FormUrlEncoded:
                    return new ByteArrayContent(payload ?? EmptyPayload)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded") }
                    };

                case ParamLocation.Multipart:
                {
                    var multipart = new MultipartFormDataContent();
                    foreach (var field in bound.Body)
                    {
                        var text = DouyinJson.ToParamString(field.Value, options.SendNullAsDefaultValue);
                        if (string.IsNullOrEmpty(text)) continue;   // 与 Go 的 ToFileForm 一致：空值不写分段
                        // 分段也用不带 charset 的 text/plain，避免过于严格的网关判非法
                        var part = new StringContent(text, Encoding.UTF8);
                        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                        multipart.Add(part, field.Key);
                    }

                    foreach (var file in bound.Files)
                    {
                        // 重试时需要从头再读，否则第二次上传内容为空
                        if (file.Value.Content.CanSeek) file.Value.Content.Seek(0, System.IO.SeekOrigin.Begin);
                        multipart.Add(
                            new NonClosingStreamContent(file.Value.Content, file.Value.ContentType),
                            file.Key,
                            file.Value.FileName ?? "file");
                    }
                    return multipart;
                }

                default:
                    // Query / None：默认发一个「干净的 GET」——没有 body、没有 Content-Length。
                    // 只有显式打开开关时才挂一个空内容体去承载 content-type
                    // （代价是多出 Content-Length: 0，见 RuntimeOptions 里的说明）。
                    if (!options.SendJsonContentTypeOnBodylessRequest)
                        return null;

                    return new ByteArrayContent(EmptyPayload)
                    {
                        Headers = { ContentType = NewJsonMediaType() }
                    };
            }
        }

        /// <summary>共享的空内容字节，避免每次请求都分配新数组。</summary>
        private static readonly byte[] EmptyPayload = new byte[0];

        /// <summary>
        /// 构造 JSON 内容类型。
        /// <para>
        /// 抖音文档要求 content-type 为<b>固定值 <c>application/json</c></b>，
        /// 因此不追加 <c>charset=utf-8</c>：JSON 默认即 UTF-8，
        /// 多带一个参数可能让做严格比对的网关判为非法值。
        /// </para>
        /// </summary>
        private static System.Net.Http.Headers.MediaTypeHeaderValue NewJsonMediaType()
        {
            return new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        /// <summary>
        /// 把 application/x-www-form-urlencoded 的键值对编码成 <c>k=v&amp;k2=v2</c>。
        /// </summary>
        private static string EncodeForm(ParamBinder.BoundParams bound, RuntimeOptions options)
        {
            var fields = ParamBinder.BuildFormFields(bound.Body, options.SendNullAsDefaultValue);
            var sb = new StringBuilder();
            foreach (var pair in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(pair.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 设置请求头，语义是「覆盖」而不是「追加」。
        /// <para>
        /// 必须显式 Remove：<c>HttpHeaders.TryAddWithoutValidation</c> 对同名头是追加，
        /// 最终会发出 <c>access-token: 旧值, 新值</c> 这种合并值，服务端会直接判非法。
        /// Go SDK 使用 map 赋值天然是覆盖语义，这里要还原一致。
        /// </para>
        /// </summary>
        private static void SetHeader(HttpRequestMessage httpRequest, string name, string value)
        {
            httpRequest.Headers.Remove(name);
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        /// <summary>
        /// 写入调用方自定义请求头。受限头（Host / Content-Length 等）由运行时管理，跳过。
        /// content-type 属于 content 头，需要写到 content 上才生效。
        /// </summary>
        private static void ApplyUserHeaders(HttpRequestMessage httpRequest, HttpContent content, Dictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0) return;

            foreach (var pair in headers)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;

                if (string.Equals(pair.Key, "Host", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    // 由 HttpClient 依据 URL / 内容自动生成，手动设置会抛 InvalidOperationException
                    continue;
                }

                if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (content != null) content.Headers.Remove("Content-Type");
                    if (content != null) content.Headers.TryAddWithoutValidation("Content-Type", pair.Value);
                    continue;
                }

                httpRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// 拼接 URL，并按 <see cref="RuntimeOptions.HostOverrides"/> 做域名替换。
        /// </summary>
        private string BuildUrl(string host, string path, RuntimeOptions options)
        {
            if (string.IsNullOrEmpty(path)) path = "/";
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

            string mapped = null;
            if (options.HostOverrides != null && options.HostOverrides.Count > 0)
                options.HostOverrides.TryGetValue(host, out mapped);

            if (!string.IsNullOrEmpty(mapped))
            {
                // 覆盖值自带协议时（如 http://127.0.0.1:8080）直接用作基地址：
                // 便于指向自建代理或本地 mock 服务做联调/自动化测试
                if (mapped.IndexOf("://", StringComparison.Ordinal) >= 0)
                    return mapped.TrimEnd('/') + path;

                return string.Format(CultureInfo.InvariantCulture, "{0}://{1}{2}",
                    DouyinConfig.DefaultProtocol, mapped, path);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}://{1}{2}",
                DouyinConfig.DefaultProtocol, host, path);
        }

        /// <summary>
        /// 计算第 <paramref name="attempt"/> 次重试的等待时间：指数退避 + 抖动。
        /// <para>
        /// 抖动很关键：大量客户端同时失败时，若退避时间完全一致会形成"同步重试风暴"，
        /// 反而把刚恢复的服务再打垮。
        /// </para>
        /// </summary>
        private static TimeSpan ComputeBackoff(int attempt, RuntimeOptions options)
        {
            var baseMs = Math.Max(1, options.RetryBackoffBaseMilliseconds);
            var exponential = baseMs * Math.Pow(2, Math.Max(0, attempt - 1));

            int jitter;
            lock (JitterLock)
            {
                jitter = JitterRandom.Next(0, baseMs);
            }

            return TimeSpan.FromMilliseconds(Math.Min(exponential + jitter, 5000));
        }

        /// <summary>
        /// 合并「客户端默认头」与「请求级头」，同名时请求级优先。
        /// </summary>
        /// <param name="defaults">客户端级默认头，可为 null。</param>
        /// <param name="requestHeaders">请求对象上的头，可为 null。</param>
        private static Dictionary<string, string> MergeHeaders(
            IDictionary<string, string> defaults,
            Dictionary<string, string> requestHeaders)
        {
            if ((defaults == null || defaults.Count == 0)
                && (requestHeaders == null || requestHeaders.Count == 0))
                return null;

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (defaults != null)
                foreach (var pair in defaults) merged[pair.Key] = pair.Value;
            if (requestHeaders != null)
                foreach (var pair in requestHeaders) merged[pair.Key] = pair.Value;
            return merged;
        }

        /// <summary>
        /// 给异常补上「本次请求的 URL 与 body」。
        /// <para>
        /// 抖音的「10002 参数解析错误」只看错误码是查不出原因的，必须把实际发出的报文
        /// 和文档的请求示例逐参数比对。把这个信息挂到异常上，出问题时打一行日志就够定位，
        /// 不必额外装抓包工具。
        /// </para>
        /// </summary>
        private static DouyinApiException WithRequestContext(DouyinApiException ex, string url, byte[] payload)
        {
            ex.RequestUrl = url;
            ex.RequestBody = payload == null ? string.Empty : Encoding.UTF8.GetString(payload);
            return ex;
        }

        /// <summary>截断过长文本，避免异常信息刷屏。</summary>
        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        /// <summary>
        /// 创建 HttpClient。超时统一交给每次请求的 CTS 控制，因此这里设为无限。
        /// </summary>
        private static HttpClient CreateHttpClient(bool ignoreSsl)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            if (ignoreSsl)
            {
#if NETFRAMEWORK
                // .NET Framework 走全局回调；调用方若同时使用其它 HTTP 客户端，需自行评估影响
                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    (sender, certificate, chain, errors) => true;
#else
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
#endif
            }

            return new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }
}
