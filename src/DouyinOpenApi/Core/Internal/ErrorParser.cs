using System;
using Newtonsoft.Json.Linq;

namespace DouyinOpenApi.Internal
{
    /// <summary>
    /// 解析抖音响应报文里的错误信息。
    /// <para>
    /// 抖音开放平台并非所有接口都用同一个错误字段：有的用 <c>err_no</c>，有的用 <c>error_code</c>，
    /// 网关层错误则藏在 <c>extra</c> 里。这里完整对齐 Go SDK <c>util.GetErrMessage</c> 的取值顺序，
    /// 保证同一个报文在两种语言下得到相同的判定结果。
    /// </para>
    /// <para>取值优先级（后者覆盖前者，与 Go 实现一致）：</para>
    /// <list type="bullet">
    ///   <item><description>错误码：<c>errno</c> → <c>err_no</c> → <c>errcode</c> → <c>error</c> → <c>error_code</c> → <c>err.err_code</c> → <c>extra.error_code</c></description></item>
    ///   <item><description>错误描述：<c>errmsg</c> → <c>err_tips</c> → <c>err_msg</c> → <c>err.err_msg</c> → <c>extra.description</c></description></item>
    ///   <item><description>log_id：<c>log_id</c> → <c>extra.logid</c></description></item>
    /// </list>
    /// </summary>
    internal static class ErrorParser
    {
        /// <summary>业务错误信息载体。</summary>
        internal sealed class ServiceError
        {
            /// <summary>业务错误码，0 表示成功。</summary>
            public int Code { get; set; }

            /// <summary>错误描述。报文里一个都没有时为空串。</summary>
            public string Message { get; set; } = string.Empty;

            /// <summary>log_id。报文里没有时为空串。</summary>
            public string LogId { get; set; } = string.Empty;
        }

        /// <summary>
        /// 从响应报文中提取错误信息。报文不是合法 JSON 时返回 code=0 的空结果，
        /// 由调用方继续按「非 200 或解析失败」处理（与 Go 行为一致）。
        /// </summary>
        /// <param name="body">HTTP 响应体原文。</param>
        public static ServiceError Parse(string body)
        {
            var result = new ServiceError();
            if (string.IsNullOrEmpty(body)) return result;

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (Exception)
            {
                // 非 JSON（例如网关返回 HTML 错误页）：交给上层按 HTTP 状态码报错
                return result;
            }

            // ---- 错误码：按 Go 的覆盖顺序依次取值 ----
            var code = 0;
            intv(root, "errno", ref code);
            intv(root, "err_no", ref code);
            intv(root, "errcode", ref code);
            intv(root, "error", ref code);
            intv(root, "error_code", ref code);

            var err = root["err"] as JObject;
            if (err != null) intv(err, "err_code", ref code);

            var extra = root["extra"] as JObject;
            if (extra != null) intv(extra, "error_code", ref code);
            result.Code = code;

            // ---- 错误描述 ----
            var msg = string.Empty;
            strv(root, "errmsg", ref msg);
            strv(root, "err_tips", ref msg);
            strv(root, "err_msg", ref msg);
            if (err != null) strv(err, "err_msg", ref msg);
            if (extra != null) strv(extra, "description", ref msg);
            result.Message = msg;

            // ---- log_id ----
            var logId = string.Empty;
            strv(root, "log_id", ref logId);
            if (extra != null) strv(extra, "logid", ref logId);
            result.LogId = logId;

            return result;
        }

        /// <summary>
        /// 若 <paramref name="token"/> 下存在 name 且为非 0 整数，则覆盖 target。
        /// 「非 0 才覆盖」是 Go 实现的语义：缺省 0 不会把已经取到的错误码冲掉。
        /// </summary>
        private static void intv(JObject token, string name, ref int target)
        {
            var v = token[name];
            if (v == null || v.Type == JTokenType.Null) return;

            int parsed;
            if (v.Type == JTokenType.Integer)
            {
                parsed = v.Value<int>();
            }
            else if (v.Type == JTokenType.String)
            {
                // 部分接口把错误码序列化成字符串，这里做兼容
                if (!int.TryParse((string)v, out parsed)) return;
            }
            else
            {
                return;
            }

            if (parsed != 0) target = parsed;
        }

        /// <summary>
        /// 若 <paramref name="token"/> 下存在 name 且为非空字符串，则覆盖 target。
        /// </summary>
        private static void strv(JObject token, string name, ref string target)
        {
            var v = token[name];
            if (v == null || v.Type != JTokenType.String) return;

            var s = (string)v;
            if (!string.IsNullOrEmpty(s)) target = s;
        }
    }
}
