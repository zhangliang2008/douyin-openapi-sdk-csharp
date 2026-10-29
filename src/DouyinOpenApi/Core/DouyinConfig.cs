using System;

namespace DouyinOpenApi
{
    /// <summary>
    /// 应用凭据配置，对应 Go SDK 的 <c>credential.Config</c>。
    /// 在抖音开放平台「控制台 - 应用详情」里可以拿到 ClientKey（即 AppId）与 ClientSecret。
    /// </summary>
    public sealed class DouyinConfig
    {
        /// <summary>接口域名，供内部拼接 URL 使用。</summary>
        internal const string DefaultProtocol = "https";

        /// <summary>
        /// 应用 Key（等同于 AppId）。用于换取 client_token。
        /// </summary>
        public string ClientKey { get; set; }

        /// <summary>
        /// 应用密钥。属于敏感信息，请勿硬编码进客户端，也不要写入日志。
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>
        /// 获取应用级 client_token 的地址，默认 <c>https://open.douyin.com/oauth/client_token/</c>。
        /// 联调沙箱环境时可改。
        /// </summary>
        public string ClientTokenEndpoint { get; set; } = "https://open.douyin.com/oauth/client_token/";

        /// <summary>
        /// 获取小程序/小游戏 access_token 的地址，默认 <c>https://developer.toutiao.com/api/apps/v2/token</c>。
        /// </summary>
        public string AppTokenEndpoint { get; set; } = "https://developer.toutiao.com/api/apps/v2/token";

        /// <summary>
        /// 校验配置是否完整，缺字段时抛出明确异常而不是等到发请求才报错。
        /// </summary>
        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(ClientKey))
                throw new ArgumentException("ClientKey 不能为空，请在抖音开放平台控制台获取应用 ClientKey。", nameof(ClientKey));
            if (string.IsNullOrWhiteSpace(ClientSecret))
                throw new ArgumentException("ClientSecret 不能为空。", nameof(ClientSecret));
        }
    }
}
