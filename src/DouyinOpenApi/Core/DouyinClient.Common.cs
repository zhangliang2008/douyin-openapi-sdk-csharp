using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DouyinOpenApi.Internal;
using Newtonsoft.Json.Linq;

namespace DouyinOpenApi
{
    /// <summary>
    /// 通用调用（不依赖强类型模型）的实现部分。
    /// </summary>
    public sealed partial class DouyinClient
    {
        /// <summary>
        /// 通用接口调用：自行指定域名、路径、方法、请求头与参数，返回解析后的 JSON 对象。
        /// <para>
        /// 与 Go SDK 的 <c>CommonOpenAPI</c> 行为一致：仅当请求头包含
        /// <c>Content-Type: application/json</c> 时才会把 <see cref="CommonRequest.Body"/> 序列化进请求体。
        /// </para>
        /// </summary>
        /// <param name="request">通用请求描述。</param>
        /// <param name="runtimeOptions">本次调用的运行时选项；为空则使用全局 Options。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>响应 JSON 的键值结构；空响应体返回空字典。</returns>
        public async Task<Dictionary<string, object>> CommonOpenApiAsync(
            CommonRequest request,
            RuntimeOptions runtimeOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(request.Path)) throw new ArgumentException("Path 不能为空。", nameof(request));

            var options = runtimeOptions ?? Options ?? new RuntimeOptions();
            var method = string.IsNullOrEmpty(request.Method) ? "POST" : request.Method;
            var host = string.IsNullOrEmpty(request.Host) ? "open.douyin.com" : request.Host;

            // ---------- URL + 查询串 ----------
            var url = BuildUrl(host, request.Path, options);
            if (request.Query != null && request.Query.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var pair in request.Query)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(pair.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
                }
                url = url + "?" + sb.ToString();
            }

            // ---------- access_token：Header 优先，其次客户端级，最后自动获取 ----------
            string token = null;
            if (request.Header != null)
            {
                foreach (var pair in request.Header)
                {
                    if (string.Equals(pair.Key, "access-token", StringComparison.OrdinalIgnoreCase))
                    {
                        token = pair.Value;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(token)) token = AccessToken;
            if (string.IsNullOrEmpty(token) && options.AutoUseClientToken && Credential != null)
            {
                var info = await Credential.GetClientTokenAsync(false, cancellationToken).ConfigureAwait(false);
                if (info != null) token = info.AccessToken;
            }

            // ---------- 请求体：仅 application/json 才写入 ----------
            var isJson = false;
            if (request.Header != null)
            {
                foreach (var pair in request.Header)
                {
                    if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                        && pair.Value != null
                        && pair.Value.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isJson = true;
                        break;
                    }
                }
            }

            var payload = isJson && request.Body != null
                ? Encoding.UTF8.GetBytes(DouyinJson.Serialize(request.Body))
                : null;

            // 通用调用同样带上客户端级默认头
            var mergedHeaders = MergeHeaders(DefaultHeaders, request.Header);

            var maxAttempts = Math.Max(1, options.MaxAttempts);
            var attempt = 0;

            while (true)
            {
                try
                {
                    return await SendCommonOnceAsync(url, method, payload, mergedHeaders, token, options, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DouyinApiException ex)
                {
                    attempt++;
                    if (!ex.IsRetryable || attempt >= maxAttempts) throw;
                    await Task.Delay(ComputeBackoff(attempt, options), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 通用调用的同步版本，便于 WinForms / 控制台等非异步场景直接调用。
        /// </summary>
        /// <param name="request">通用请求描述。</param>
        /// <param name="runtimeOptions">本次调用的运行时选项；为空则使用全局 Options。</param>
        public Dictionary<string, object> CommonOpenApi(CommonRequest request, RuntimeOptions runtimeOptions = null)
        {
            return CommonOpenApiAsync(request, runtimeOptions).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 发送一次通用请求并解析响应。
        /// </summary>
        private static async Task<Dictionary<string, object>> SendCommonOnceAsync(
            string url,
            string method,
            byte[] payload,
            Dictionary<string, string> headers,
            string accessToken,
            RuntimeOptions options,
            CancellationToken cancellationToken)
        {
            var httpClient = GetHttpClient(options.IgnoreSsl);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (options.ReadTimeout > 0) timeoutCts.CancelAfter(options.ReadTimeout);

                using (var httpRequest = new HttpRequestMessage(new HttpMethod(method), url))
                {
                    if (payload != null)
                    {
                        var content = new ByteArrayContent(payload);
                        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                        httpRequest.Content = content;
                    }

                    ApplyUserHeaders(httpRequest, httpRequest.Content, headers);
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
                        throw new DouyinApiException(
                            string.Format(CultureInfo.InvariantCulture, "请求超时（{0} ms）：{1}", options.ReadTimeout, url),
                            new TimeoutException("请求超时"), isRetryable: true);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new DouyinApiException("网络请求失败：" + ex.Message, ex, isRetryable: true);
                    }

                    using (response)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)response.StatusCode;

                        if (status != 200)
                        {
                            throw new DouyinApiException(
                                string.Format(CultureInfo.InvariantCulture, "请求失败，HTTP {0}：{1}", status, Truncate(body, 512)),
                                status.ToString(CultureInfo.InvariantCulture))
                            {
                                HttpStatusCode = status,
                                ResponseBody = body,
                                IsRetryable = status == 429 || status >= 500
                            };
                        }

                        var error = ErrorParser.Parse(body);
                        if (error.Code != 0)
                        {
                            var code = error.Code.ToString(CultureInfo.InvariantCulture);
                            throw new DouyinApiException(
                                string.IsNullOrEmpty(error.Message)
                                    ? string.Format(CultureInfo.InvariantCulture, "接口返回错误码 {0}。", code)
                                    : error.Message,
                                code)
                            {
                                HttpStatusCode = status,
                                LogId = error.LogId,
                                ResponseBody = body,
                                IsTokenExpired = TokenExpiredCodes.Contains(code)
                            };
                        }

                        var result = new Dictionary<string, object>();
                        if (string.IsNullOrEmpty(body)) return result;

                        try
                        {
                            var root = JToken.Parse(body);
                            var dict = FlexibleObjectConverter.ToClrObject(root) as Dictionary<string, object>;
                            if (dict != null) return dict;

                            // 极端情况：响应是数组或标量，包一层便于调用方取值
                            result["data"] = FlexibleObjectConverter.ToClrObject(root);
                            return result;
                        }
                        catch (Exception ex)
                        {
                            throw new DouyinApiException("响应报文解析失败：" + ex.Message, ex, isRetryable: false)
                            {
                                HttpStatusCode = status,
                                ResponseBody = body
                            };
                        }
                    }
                }
            }
        }
    }
}
