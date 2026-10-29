using System;

namespace DouyinOpenApi
{
    /// <summary>
    /// access_token 载体。
    /// </summary>
    public sealed class AccessTokenInfo
    {
        /// <summary>token 字符串。</summary>
        public string AccessToken { get; set; }

        /// <summary>有效期（秒），平台返回的值。</summary>
        public long ExpiresIn { get; set; }

        /// <summary>本地计算出的到期时间（已扣除提前刷新余量）。</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>是否已过期或即将过期。</summary>
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        /// <summary>创建一个按「当前时间 + 有效期」计算的 token 信息。</summary>
        /// <param name="accessToken">token 字符串。</param>
        /// <param name="expiresInSeconds">有效期秒数。</param>
        /// <param name="safetyMarginSeconds">提前刷新余量（秒），默认 60，避免临界过期导致的偶发失败。</param>
        public static AccessTokenInfo Create(string accessToken, long expiresInSeconds, int safetyMarginSeconds = 60)
        {
            // 余量不能超过总有效期的一半，否则短效 token（如 300 秒）会永远处于"已过期"状态
            var margin = Math.Min(safetyMarginSeconds, Math.Max(0, expiresInSeconds / 2));
            return new AccessTokenInfo
            {
                AccessToken = accessToken,
                ExpiresIn = expiresInSeconds,
                ExpiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds - margin)
            };
        }
    }

    /// <summary>
    /// 凭据提供者，对应 Go SDK 的 <c>credential.Credential</c> 接口。
    /// <para>
    /// Go SDK 里凭据只做缓存、不参与请求签名（抖音 OpenAPI 使用 <c>access-token</c> 头部鉴权，
    /// 没有阿里云那种 HMAC 签名），因此这里保持同样的职责边界：只负责按需换取并缓存 token。
    /// </para>
    /// </summary>
    public interface ICredential
    {
        /// <summary>应用 Key。</summary>
        string ClientKey { get; }

        /// <summary>应用密钥。</summary>
        string ClientSecret { get; }

        /// <summary>
        /// 获取应用级 client_token（<c>/oauth/client_token/</c>）。带本地缓存，临近过期才真正发起请求。
        /// </summary>
        /// <param name="forceRefresh">为 true 时忽略缓存强制重新获取。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        System.Threading.Tasks.Task<AccessTokenInfo> GetClientTokenAsync(bool forceRefresh = false,
            System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken));

        /// <summary>
        /// 获取小程序/小游戏 access_token（<c>developer.toutiao.com/api/apps/v2/token</c>）。带本地缓存。
        /// </summary>
        /// <param name="forceRefresh">为 true 时忽略缓存强制重新获取。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        System.Threading.Tasks.Task<AccessTokenInfo> GetAppAccessTokenAsync(bool forceRefresh = false,
            System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken));

        /// <summary>
        /// 由调用方注入一个已知的 client_token（例如从统一配置中心读取），避免重复换取。
        /// </summary>
        /// <param name="accessToken">token 字符串。</param>
        /// <param name="expiresInSeconds">剩余有效期秒数。</param>
        void SetClientToken(string accessToken, long expiresInSeconds);

        /// <summary>清空本地缓存，下次获取时重新请求。</summary>
        void InvalidateClientToken();
    }
}
