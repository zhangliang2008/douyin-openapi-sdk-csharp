using System.Collections.Generic;

namespace DouyinOpenApi
{
    /// <summary>
    /// 单次调用的运行时选项，对应 Go SDK 的 <c>RuntimeOptions</c> 与 tea 的 <c>_runtime</c> 字典。
    /// 作用域说明：<see cref="DouyinClient.Options"/> 上的实例是「全局默认」，
    /// 调用接口时传入的实例仅对本次调用生效（未设置的项沿用默认值）。
    /// </summary>
    public sealed class RuntimeOptions
    {
        /// <summary>
        /// 整体读超时（毫秒），默认 5000，与 Go SDK 一致。
        /// <para>
        /// 注意：.NET 的 <c>HttpClient</c> 只能设置一个整体超时，无法分别控制「连接」与「读取」两段，
        /// 因此这里的值作为单次请求的整体超时使用；<see cref="ConnectTimeout"/> 仅用于保持与 Go 端配置项对齐。
        /// </para>
        /// </summary>
        public int ReadTimeout { get; set; } = 5000;

        /// <summary>
        /// 连接超时（毫秒），默认 1000。当前仅作为配置项保留，实际受 <see cref="ReadTimeout"/> 的整体超时约束。
        /// </summary>
        public int ConnectTimeout { get; set; } = 1000;

        /// <summary>
        /// 是否允许自动重试，默认 true。仅对「网络异常 / 429 / 5xx」这类可重试错误生效，
        /// 业务错误（err_no != 0）不会重试，避免重复下单等副作用。
        /// </summary>
        public bool Autoretry { get; set; } = true;

        /// <summary>
        /// 最大尝试次数（含首次请求），默认 3。
        /// </summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>
        /// 是否跳过 HTTPS 证书校验，默认 <c>false</c>。
        /// <para>
        /// 与 Go SDK 的默认值不同：Go SDK 默认 <c>true</c>（等同于不校验证书），
        /// 这里出于安全默认关闭。仅在自建代理抓包调试时才临时打开。
        /// </para>
        /// </summary>
        public bool IgnoreSsl { get; set; }

        /// <summary>
        /// 序列化业务参数时，是否把「未设置的字段」按 Go SDK 的规则补成零值，默认 <c>true</c>。
        /// <para>
        /// Go SDK 生成代码用 <c>tea.StringValue(nil)</c> / <c>tea.Int32Value(nil)</c> 取值，
        /// 未赋值时会变成 <c>""</c> 与 <c>0</c> 并照常发送。开启此项可获得与 Go SDK 完全一致的报文；
        /// 若某些接口对「空字符串」敏感（例如把空串当成显式过滤条件），可置为 <c>false</c> 改为省略空字段。
        /// </para>
        /// <para>此开关只影响 JSON body / 表单，查询参数请看 <see cref="OmitUnsetQueryParameters"/>。</para>
        /// </summary>
        public bool SendNullAsDefaultValue { get; set; } = true;

        /// <summary>
        /// 查询参数里「未赋值」的字段是否省略，默认 <c>true</c>（只发送调用方显式赋过值的参数）。
        /// <para>
        /// 默认值与 body 的处理**不同**，原因是真实踩坑：
        /// 以 <c>GET /goodlife/v1/trade/order/query/</c> 为例，
        /// 只设置 <c>AccountId</c> / <c>PageNum</c> / <c>PageSize</c> 时，
        /// 若照 Go 的零值规则补全，实际会发出
        /// <c>?order_status=0&amp;create_order_start_time=0&amp;ext_order_id=&amp;open_id=&amp;...</c>
        /// 共 12 个参数，抖音返回 <c>10002 参数解析错误</c>；
        /// 而官方文档的请求示例只带 <c>account_id / page_num / page_size</c> 三个。
        /// </para>
        /// <para>
        /// 查询参数在语义上是"过滤条件"，凭空补 <c>0</c>（如时间戳 0 = 1970 年）与空串
        /// 会让服务端当成真实过滤值处理，风险远高于收益。
        /// 若你确实需要与 Go SDK 完全一致的报文（例如做逐字节比对），把它置为 <c>false</c>。
        /// </para>
        /// </summary>
        public bool OmitUnsetQueryParameters { get; set; } = true;

        /// <summary>
        /// 无 body 的请求（GET 等）是否强行带上 <c>content-type: application/json</c>，默认 <c>false</c>。
        /// <para>
        /// 默认关闭是有原因的：.NET Core 上 Content-Type 属于"内容头"，想让 GET 带上它就必须挂一个
        /// 内容体，于是线上会多出 <c>Content-Length: 0</c>；而抖音生活服务网关把带 body 的 GET
        /// 当成待解析的 JSON，空 body 解析失败即返回 <c>10002 参数解析错误</c>，且<b>没有 logid</b>。
        /// 默认行为改为发一个"干净的 GET"（无 body、无 Content-Length、无 content-type），
        /// 与抖音文档给出的 curl 示例保持一致。
        /// </para>
        /// <para>
        /// 若某个接口确实强制要求该头，把它置为 <c>true</c>：net472 上走 HttpWebRequest
        /// （只有 content-type、没有 Content-Length），其它目标框架会附带 <c>Content-Length: 0</c>。
        /// </para>
        /// </summary>
        public bool SendJsonContentTypeOnBodylessRequest { get; set; }

        /// <summary>
        /// 请求未携带 access_token 时，是否用 ClientKey/ClientSecret 自动换取应用级 client_token 并注入，默认 <c>true</c>。
        /// 换取的 token 由 <see cref="ICredential"/> 缓存，临近过期才重新获取。
        /// </summary>
        public bool AutoUseClientToken { get; set; } = true;

        /// <summary>
        /// 命中「token 失效」类错误码时，是否自动刷新 client_token 并重试一次，默认 <c>true</c>。
        /// 仅在 token 来自自动获取时生效（调用方自己传入的 token 不会被覆盖）。
        /// </summary>
        public bool RefreshTokenOnExpiredError { get; set; } = true;

        /// <summary>
        /// 域名覆盖表，key 为默认域名（如 <c>open.douyin.com</c>），value 为替换后的域名。
        /// 用于灰度/沙箱环境联调，例如把 <c>open.douyin.com</c> 指向自建代理。
        /// </summary>
        public IDictionary<string, string> HostOverrides { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 重试退避基数（毫秒），默认 100。第 n 次重试等待约 <c>base * 2^(n-1)</c> 并叠加抖动。
        /// Go SDK 的 tea 默认策略实际上不等待（退避返回 0），这里改为指数退避，避免瞬时打爆对端。
        /// </summary>
        public int RetryBackoffBaseMilliseconds { get; set; } = 100;

        /// <summary>
        /// 复制一份当前配置，供「全局默认 + 单次覆盖」的场景使用。
        /// </summary>
        internal RuntimeOptions Clone()
        {
            return new RuntimeOptions
            {
                ReadTimeout = ReadTimeout,
                ConnectTimeout = ConnectTimeout,
                Autoretry = Autoretry,
                MaxAttempts = MaxAttempts,
                IgnoreSsl = IgnoreSsl,
                SendNullAsDefaultValue = SendNullAsDefaultValue,
                OmitUnsetQueryParameters = OmitUnsetQueryParameters,
                SendJsonContentTypeOnBodylessRequest = SendJsonContentTypeOnBodylessRequest,
                AutoUseClientToken = AutoUseClientToken,
                RefreshTokenOnExpiredError = RefreshTokenOnExpiredError,
                HostOverrides = HostOverrides == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(HostOverrides),
                RetryBackoffBaseMilliseconds = RetryBackoffBaseMilliseconds
            };
        }
    }
}
