using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DouyinOpenApi.Internal;
using Newtonsoft.Json.Linq;

namespace DouyinOpenApi
{
    /// <summary>
    /// 默认凭据实现：用 ClientKey / ClientSecret 换取并缓存 client_token / 小程序 access_token。
    /// <para>
    /// 抖音官方明确要求「不要频繁调用」token 接口，因此这里做了四层保护：
    /// </para>
    /// <list type="number">
    ///   <item><description><b>进程级共享缓存</b>：缓存挂在 <c>client_key + 接口地址</c> 上，而不是实例字段。
    ///   同一个应用 new 出多少个 <see cref="DouyinClient"/>，都共用同一份 token，
    ///   只在首次（或过期时）真正请求一次。</description></item>
    ///   <item><description><b>提前刷新</b>：按 <c>expires_in</c> 提前 60 秒判定过期（上限为有效期的一半），
    ///   避免刚好卡在过期边界导致业务调用失败、进而触发第二次换取。</description></item>
    ///   <item><description><b>并发合并</b>：每个 token 槽位一把异步锁 + 双检，N 个线程同时缺 token
    ///   只会发出 1 次 HTTP 请求。</description></item>
    ///   <item><description><b>失败冷却</b>：换取失败后 5 秒内不再重试，直接抛上次的异常。
    ///   否则凭据填错时，每个业务请求都会附带一次失败的 token 调用——恰恰是最容易被风控
    ///   限流的场景。</description></item>
    /// </list>
    /// <para>
    /// 多进程/多机部署时，进程内缓存的命中率有限（每个进程第一次仍会请求一次）。
    /// 想做到整个集群只换一次，可让所有实例共用一份 token：
    /// 从 Redis/数据库取 <c>access_token</c> 后调用 <see cref="SetClientToken"/>，
    /// 或直接实现 <see cref="ICredential"/> 把 token 存到集中式缓存里。
    /// </para>
    /// </summary>
    public sealed class DouyinCredential : ICredential
    {
        /// <summary>
        /// 换取失败后的冷却时长。凭据错误、网络故障时避免每个业务请求都连带打一次 token 接口。
        /// </summary>
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 进程级共享的 token 槽位表，key = <c>类型|client_key|接口地址</c>。
        /// <para>
        /// 用静态字典而不是实例字段，是为了解决「每次请求 new 一个 DouyinClient」这种常见写法：
        /// 若缓存挂在实例上，那种写法等于每个请求都换一次 token，与官方要求直接冲突。
        /// </para>
        /// </summary>
        private static readonly ConcurrentDictionary<string, TokenSlot> Slots =
            new ConcurrentDictionary<string, TokenSlot>(StringComparer.Ordinal);

        private readonly DouyinConfig _config;
        private readonly HttpClient _httpClient;
        private readonly string _clientTokenSlotKey;
        private readonly string _appTokenSlotKey;

        /// <summary>
        /// 用配置构造凭据对象。
        /// </summary>
        /// <param name="config">应用凭据配置。</param>
        /// <param name="httpClient">可选的 HTTP 客户端；不传则使用内部共享实例（推荐）。</param>
        public DouyinCredential(DouyinConfig config, HttpClient httpClient = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();
            _config = config;
            _httpClient = httpClient ?? DouyinClient.SharedHttpClient;

            // 槽位 key 在构造时算好，避免每次调用都拼字符串
            _clientTokenSlotKey = "client|" + config.ClientKey + "|" + config.ClientTokenEndpoint;
            _appTokenSlotKey = "app|" + config.ClientKey + "|" + config.AppTokenEndpoint;
        }

        /// <inheritdoc />
        public string ClientKey => _config.ClientKey;

        /// <inheritdoc />
        public string ClientSecret => _config.ClientSecret;

        /// <inheritdoc />
        public async Task<AccessTokenInfo> GetClientTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var slot = GetSlot(_clientTokenSlotKey);

            return await slot.GetOrFetchAsync(forceRefresh, FailureCooldown, cancellationToken,
                async ct =>
                {
                    var requestBody = new JObject
                    {
                        ["client_key"] = _config.ClientKey,
                        ["client_secret"] = _config.ClientSecret,
                        ["grant_type"] = "client_credential"
                    };

                    var json = await PostJsonAsync(_config.ClientTokenEndpoint, requestBody.ToString(), "获取 client_token", ct)
                        .ConfigureAwait(false);

                    // 响应：{ "data": { "access_token": "clt.xxx", "expires_in": 7200, "error_code": 0 }, "message": "success" }
                    var data = json["data"] as JObject;
                    if (data == null)
                        throw BuildException(json, "获取 client_token 失败：响应中缺少 data 字段。", _config.ClientTokenEndpoint);

                    var errorCode = data.Value<int?>("error_code") ?? 0;
                    if (errorCode != 0)
                    {
                        var desc = data.Value<string>("description");
                        // 必须点明是「换 token」这一步失败：否则调用方只会看到"参数错误"，
                        // 误以为是业务接口的参数问题（真实踩坑：为此排查了两轮）。
                        throw new DouyinApiException(
                            "获取 client_token 失败：" + (string.IsNullOrEmpty(desc) ? "未知原因" : desc)
                            + "（client_key=" + _config.ClientKey + "，接口=" + _config.ClientTokenEndpoint + "）",
                            errorCode.ToString());
                    }

                    var token = data.Value<string>("access_token");
                    if (string.IsNullOrEmpty(token))
                        throw new DouyinApiException("获取 client_token 失败：响应中 access_token 为空。", errorCode.ToString());

                    // expires_in 缺失时按平台默认 7200 秒兜底
                    var expiresIn = data.Value<long?>("expires_in") ?? 7200;
                    return AccessTokenInfo.Create(token, expiresIn);
                }).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AccessTokenInfo> GetAppAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var slot = GetSlot(_appTokenSlotKey);

            return await slot.GetOrFetchAsync(forceRefresh, FailureCooldown, cancellationToken,
                async ct =>
                {
                    var requestBody = new JObject
                    {
                        ["appid"] = _config.ClientKey,
                        ["secret"] = _config.ClientSecret,
                        ["grant_type"] = "client_credential"
                    };

                    var json = await PostJsonAsync(_config.AppTokenEndpoint, requestBody.ToString(), "获取小程序 access_token", ct)
                        .ConfigureAwait(false);

                    // 响应：{ "err_no": 0, "err_tips": "success", "data": { "access_token": "...", "expires_in": 7200 } }
                    var errNo = json.Value<int?>("err_no") ?? 0;
                    if (errNo != 0)
                    {
                        var tips = json.Value<string>("err_tips");
                        throw new DouyinApiException(
                            "获取小程序 access_token 失败：" + (string.IsNullOrEmpty(tips) ? "未知原因" : tips)
                            + "（appid=" + _config.ClientKey + "）",
                            errNo.ToString());
                    }

                    var data = json["data"] as JObject;
                    var token = data?.Value<string>("access_token");
                    if (string.IsNullOrEmpty(token))
                        throw new DouyinApiException("获取小程序 access_token 失败：响应中 access_token 为空。", errNo.ToString());

                    var expiresIn = data.Value<long?>("expires_in") ?? 7200;
                    return AccessTokenInfo.Create(token, expiresIn);
                }).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void SetClientToken(string accessToken, long expiresInSeconds)
        {
            if (string.IsNullOrEmpty(accessToken)) throw new ArgumentNullException(nameof(accessToken));
            // 写入共享槽位：同一进程内所有客户端立即复用，不会再各换一次
            GetSlot(_clientTokenSlotKey).Set(AccessTokenInfo.Create(accessToken, expiresInSeconds));
        }

        /// <inheritdoc />
        public void InvalidateClientToken()
        {
            // 失效必然发生在「服务端判定 token 不可用」之后，因此同时清掉冷却状态，
            // 保证紧接着的强制刷新能真正发出请求
            GetSlot(_clientTokenSlotKey).Invalidate();
        }

        /// <summary>取（或创建）某个槽位。</summary>
        private static TokenSlot GetSlot(string key)
        {
            return Slots.GetOrAdd(key, _ => new TokenSlot());
        }

        /// <summary>
        /// 一个 token 的缓存槽位：负责「缓存命中 → 单飞换取 → 失败冷却」的完整状态机。
        /// <para>
        /// 之所以抽成独立类型：client_token 与小程序 access_token 两条链路复用同一套并发控制，
        /// 避免把双检锁逻辑写两遍而出现行为差异。
        /// </para>
        /// </summary>
        private sealed class TokenSlot
        {
            /// <summary>单飞锁：同一槽位同时只允许一个换取请求。</summary>
            private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

            private AccessTokenInfo _value;

            /// <summary>失败冷却截止时间（UTC Ticks，0 表示不在冷却中）。用 long 以便原子读写。</summary>
            private long _failedUntilTicks;

            /// <summary>上次失败原因，冷却期内直接抛出，保证调用方看到的错误不丢失细节。</summary>
            private Exception _lastError;

            /// <summary>当前缓存的 token（可能为 null）。</summary>
            internal AccessTokenInfo Value => Volatile.Read(ref _value);

            /// <summary>
            /// 取有效 token；没有则调用 <paramref name="fetch"/> 换取。
            /// </summary>
            /// <param name="forceRefresh">true 时无条件重新换取（token 失效后的恢复路径）。</param>
            /// <param name="cooldown">失败后的冷却时长。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <param name="fetch">真正执行换取并返回新 token 的委托。</param>
            internal async Task<AccessTokenInfo> GetOrFetchAsync(
                bool forceRefresh,
                TimeSpan cooldown,
                CancellationToken cancellationToken,
                Func<CancellationToken, Task<AccessTokenInfo>> fetch)
            {
                // 快路径：命中缓存且未临近过期，不进锁
                var cached = Value;
                if (!forceRefresh && cached != null && !cached.IsExpired)
                    return cached;

                // 失败冷却：刚失败过就不再去打接口（官方要求别频繁调用，这里也包括"失败的调用"）
                if (!forceRefresh && TryGetCooldownError(out var cooldownError))
                    throw cooldownError;

                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // 双检：可能已被其它线程/其它客户端实例刷新
                    cached = Value;
                    if (!forceRefresh && cached != null && !cached.IsExpired)
                        return cached;

                    if (!forceRefresh && TryGetCooldownError(out cooldownError))
                        throw cooldownError;

                    try
                    {
                        var token = await fetch(cancellationToken).ConfigureAwait(false);
                        Volatile.Write(ref _value, token);
                        ClearFailure();
                        return token;
                    }
                    catch (OperationCanceledException)
                    {
                        // 调用方主动取消不算失败，不要进入冷却
                        throw;
                    }
                    catch (Exception ex)
                    {
                        MarkFailure(ex, cooldown);
                        throw;
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            /// <summary>直接写入一个已知 token（例如从集中式缓存读到）。</summary>
            internal void Set(AccessTokenInfo token)
            {
                Volatile.Write(ref _value, token);
                ClearFailure();
            }

            /// <summary>作废当前 token，并清掉冷却状态。</summary>
            internal void Invalidate()
            {
                Volatile.Write(ref _value, null);
                ClearFailure();
            }

            /// <summary>是否处于失败冷却期；是则给出上次的异常。</summary>
            private bool TryGetCooldownError(out Exception error)
            {
                var until = Interlocked.Read(ref _failedUntilTicks);
                if (until != 0 && DateTime.UtcNow.Ticks < until)
                {
                    error = Volatile.Read(ref _lastError);
                    // 极端情况下（刚被 ClearFailure 清掉）退化为通用错误，保证不返回 null
                    if (error == null) error = new DouyinApiException("获取 token 失败（冷却期内不再重试）。", string.Empty);
                    return true;
                }

                error = null;
                return false;
            }

            /// <summary>记录失败并进入冷却期。</summary>
            private void MarkFailure(Exception error, TimeSpan cooldown)
            {
                Volatile.Write(ref _lastError, error);
                Interlocked.Exchange(ref _failedUntilTicks, DateTime.UtcNow.Add(cooldown).Ticks);
            }

            /// <summary>清除失败状态。</summary>
            private void ClearFailure()
            {
                Interlocked.Exchange(ref _failedUntilTicks, 0);
                Volatile.Write(ref _lastError, null);
            }
        }

        /// <summary>
        /// 以 JSON 形式 POST 一个报文并解析 JSON 响应，供两个 token 接口复用。
        /// </summary>
        /// <param name="url">完整地址。</param>
        /// <param name="body">请求体 JSON 文本。</param>
        /// <param name="action">动作描述，用于拼错误信息。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task<JObject> PostJsonAsync(string url, string body, string action, CancellationToken cancellationToken)
        {
            using (var content = CreateJsonContent(body))
            using (var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                JObject json;
                try
                {
                    json = JObject.Parse(text);
                }
                catch (Exception ex)
                {
                    throw new DouyinApiException(
                        string.Format("{0}失败：响应不是合法 JSON（HTTP {1}）：{2}", action, (int)response.StatusCode, Truncate(text, 512)),
                        ex, isRetryable: false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var ex = BuildException(json, string.Format("{0}失败（HTTP {1}）。", action, (int)response.StatusCode), url);
                    ex.HttpStatusCode = (int)response.StatusCode;
                    ex.ResponseBody = text;
                    throw ex;
                }

                return json;
            }
        }

        /// <summary>
        /// 构造 JSON 请求体，**content-type 必须严格是 <c>application/json</c>，不能带 charset**。
        /// <para>
        /// 这是踩过的真实故障：用 <c>new StringContent(body, Encoding.UTF8, "application/json")</c>
        /// 时 .NET 会自动拼成 <c>application/json; charset=utf-8</c>，
        /// 而抖音的 <c>/oauth/client_token/</c> 对这个参数零容忍，直接返回
        /// <c>10002 参数错误</c>；token 换不到，之后每个业务接口都会以同样的 10002 失败，
        /// 且 logid 为空（请求根本没进业务层），极难定位。
        /// 已用 curl 实测：<c>application/json</c> → 正常返回凭据校验结果；
        /// <c>application/json; charset=utf-8</c> → 必定 10002。
        /// </para>
        /// </summary>
        /// <param name="body">请求体 JSON 文本。</param>
        private static HttpContent CreateJsonContent(string body)
        {
            var content = new StringContent(body, Encoding.UTF8);
            // 直接覆盖，避免 StringContent 默认写入 "text/plain; charset=utf-8"
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }

        /// <summary>
        /// 从错误响应里尽量提取 err_msg / err_tips / description，构造带 log_id 的异常。
        /// </summary>
        private static DouyinApiException BuildException(JObject json, string fallbackMessage, string url)
        {
            var parsed = ErrorParser.Parse(json.ToString());
            var message = string.IsNullOrEmpty(parsed.Message) ? fallbackMessage : parsed.Message;
            var ex = new DouyinApiException(
                string.IsNullOrEmpty(parsed.LogId) ? message : message + "（log_id=" + parsed.LogId + "）",
                parsed.Code.ToString());
            ex.LogId = parsed.LogId;
            ex.ResponseBody = json.ToString();
            return ex;
        }

        /// <summary>截断过长文本，避免异常信息刷屏。</summary>
        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
