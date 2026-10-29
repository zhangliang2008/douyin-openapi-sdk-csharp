using System;

namespace DouyinOpenApi
{
    /// <summary>
    /// SDK 统一异常。抖音开放平台所有接口失败都会抛出本类型，调用方只需捕获一次。
    /// <para>失败分为三类，可通过属性区分：</para>
    /// <list type="bullet">
    ///   <item><description><see cref="IsNetworkError"/> = true：DNS/连接/超时/证书等传输层问题，可安全重试。</description></item>
    ///   <item><description><see cref="Code"/> 为 HTTP 状态码：服务端返回了非 200（对齐 Go SDK 的处理方式）。</description></item>
    ///   <item><description><see cref="Code"/> 为业务错误码：HTTP 200 但 <c>err_no != 0</c>。</description></item>
    /// </list>
    /// </summary>
    public class DouyinApiException : Exception
    {
        /// <summary>业务错误码字符串，如 <c>"2190008"</c>；HTTP 层失败时为 HTTP 状态码字符串。</summary>
        public string Code { get; internal set; }

        /// <summary>抖音返回的 log_id，向官方提单时必填，排查线上问题非常关键。</summary>
        public string LogId { get; internal set; }

        /// <summary>HTTP 状态码；传输层失败（未拿到响应）时为 0。</summary>
        public int HttpStatusCode { get; internal set; }

        /// <summary>原始响应报文，便于事后复盘。可能是空串。</summary>
        public string ResponseBody { get; internal set; }

        /// <summary>
        /// 本次请求的完整 URL（含查询串）。
        /// <para>
        /// 排查「10002 参数解析错误」这类问题时这是最关键的信息：直接对比文档的
        /// 请求示例就能看出多传/少传了哪个参数。SDK 不记录 access_token。
        /// </para>
        /// </summary>
        public string RequestUrl { get; internal set; }

        /// <summary>本次请求的 body 原文（GET 等无 body 的请求为空串）。</summary>
        public string RequestBody { get; internal set; }

        /// <summary>是否为传输层异常（网络不可达、超时、证书错误等）。</summary>
        public bool IsNetworkError { get; internal set; }

        /// <summary>SDK 是否会对此错误自动重试。业务错误恒为 false。</summary>
        public bool IsRetryable { get; internal set; }

        /// <summary>是否为「token 失效/过期」类错误，SDK 会在内部自动刷新 token 后重试一次。</summary>
        public bool IsTokenExpired { get; internal set; }

        /// <summary>
        /// 构造一个业务/HTTP 层失败异常。
        /// </summary>
        /// <param name="message">错误描述（平台返回的 err_msg）。</param>
        /// <param name="code">错误码字符串。</param>
        internal DouyinApiException(string message, string code)
            : base(message)
        {
            Code = code;
        }

        /// <summary>
        /// 构造一个传输层异常，包装底层 <see cref="System.Net.Http.HttpRequestException"/> / <see cref="OperationCanceledException"/>。
        /// </summary>
        /// <param name="message">错误描述。</param>
        /// <param name="inner">底层异常。</param>
        /// <param name="isRetryable">是否可重试；调用方主动取消时传 false。</param>
        internal DouyinApiException(string message, Exception inner, bool isRetryable)
            : base(message, inner)
        {
            IsNetworkError = true;
            IsRetryable = isRetryable;
        }

        /// <summary>
        /// 复制一份异常并在描述末尾追加诊断提示。
        /// <para>
        /// Exception.Message 是只读的，只能在抛出前生成新实例。用于补充「本次请求根本没带
        /// access_token」这类上下文——否则调用方看到 <c>2190002 access_token无效</c>
        /// 会去怀疑 token 本身，而实际是压根没发送。
        /// </para>
        /// </summary>
        internal DouyinApiException WithHint(string hint)
        {
            return new DouyinApiException(Message + hint, Code)
            {
                HttpStatusCode = HttpStatusCode,
                LogId = LogId,
                ResponseBody = ResponseBody,
                RequestUrl = RequestUrl,
                RequestBody = RequestBody,
                IsNetworkError = IsNetworkError,
                IsRetryable = IsRetryable,
                IsTokenExpired = IsTokenExpired
            };
        }

        /// <summary>
        /// 输出包含错误码、log_id、实际请求与原始响应的完整信息。
        /// <para>
        /// 排查「10002 参数错误」这类模糊错误时，直接 <c>Console.WriteLine(ex)</c> 就能拿到
        /// 完整上下文：请求 URL、请求体、响应原文，无需再单独取值或抓包。
        /// </para>
        /// </summary>
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(
                "{0}: {1}{2} (HttpStatus={3}, LogId={4})",
                GetType().Name,
                Message,
                string.IsNullOrEmpty(Code) ? string.Empty : " [err_no=" + Code + "]",
                HttpStatusCode,
                string.IsNullOrEmpty(LogId) ? "-" : LogId);

            if (!string.IsNullOrEmpty(RequestUrl))
                sb.AppendLine().Append("  请求: ").Append(RequestUrl);
            if (!string.IsNullOrEmpty(RequestBody))
                sb.AppendLine().Append("  请求体: ").Append(RequestBody);
            if (!string.IsNullOrEmpty(ResponseBody))
                sb.AppendLine().Append("  响应: ").Append(ResponseBody);

            if (InnerException != null)
                sb.AppendLine().Append("  内层异常: ").Append(InnerException.Message);

            return sb.ToString();
        }
    }
}
