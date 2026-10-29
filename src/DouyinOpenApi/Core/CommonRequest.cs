using System.Collections.Generic;
using Newtonsoft.Json;

namespace DouyinOpenApi
{
    /// <summary>
    /// 通用调用请求，对应 Go SDK 的 <c>CommonRequest</c>。
    /// <para>
    /// 适用于两种情况：① 平台新上线接口、SDK 还未重新生成时临时调用；
    /// ② 需要完全自控报文（自定义 header 组合）的高级场景。
    /// 有强类型模型可用时，优先用强类型接口，能获得编译期校验。
    /// </para>
    /// </summary>
    public sealed class CommonRequest
    {
        /// <summary>域名，如 <c>open.douyin.com</c>。默认使用该值。</summary>
        public string Host { get; set; } = "open.douyin.com";

        /// <summary>接口路径，必须以 <c>/</c> 开头并以 <c>/</c> 结尾（平台约定）。</summary>
        public string Path { get; set; }

        /// <summary>HTTP 方法，通常为 GET 或 POST。</summary>
        public string Method { get; set; } = "POST";

        /// <summary>
        /// 自定义请求头。若包含 <c>Content-Type: application/json</c>，
        /// <see cref="Body"/> 会被序列化成 JSON 放入请求体。
        /// </summary>
        public Dictionary<string, string> Header { get; set; }

        /// <summary>查询参数，直接拼到 URL 上。</summary>
        public Dictionary<string, string> Query { get; set; }

        /// <summary>请求体对象。仅在 Content-Type 为 application/json 时生效。</summary>
        [JsonIgnore]
        public object Body { get; set; }
    }
}
