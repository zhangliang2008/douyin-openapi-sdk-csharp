using System.Collections.Generic;
using Newtonsoft.Json;

namespace DouyinOpenApi
{
    /// <summary>
    /// 所有接口请求模型的基类。
    /// <para>
    /// 对应 Go SDK 中每个 <c>XxxRequest</c> 结构体里固定出现的两个字段
    /// <c>Header</c> 与 <c>AccessToken</c>。把它们收敛到基类，可以避免在
    /// 640 个生成的请求类里重复声明同样的两个属性。
    /// </para>
    /// <para>
    /// 这两个属性都标了 <see cref="JsonIgnoreAttribute"/>：它们描述的是
    /// 「HTTP 报文头部」而不是「业务字段」，所以绝不能参与 body 序列化——
    /// 否则嵌套复用的请求模型会把 header/access_token 一起塞进 JSON。
    /// </para>
    /// </summary>
    public abstract class ApiRequest
    {
        /// <summary>
        /// 附加的 HTTP 请求头，可覆盖 SDK 默认写入的头（如 <c>x-from</c>、<c>content-type</c>）。
        /// 不参与 body 序列化。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> Header { get; set; }

        /// <summary>
        /// 本次调用使用的 access_token，会写入 HTTP 头 <c>access-token</c>。
        /// <para>
        /// 取值优先级：本属性 &gt; <see cref="DouyinClient.AccessToken"/> &gt;
        /// 客户端凭据自动获取的 client_token（需要开启 <see cref="RuntimeOptions.AutoUseClientToken"/>）。
        /// </para>
        /// 不参与 body 序列化（少数接口把 access_token 放到 body 里，由 SDK 内部单独处理）。
        /// </summary>
        [JsonIgnore]
        public string AccessToken { get; set; }

        /// <summary>
        /// 设置 Header 中的一项，便于链式调用。
        /// </summary>
        /// <param name="name">头部名称。</param>
        /// <param name="value">头部值。</param>
        /// <returns>当前请求对象，支持链式调用。</returns>
        public ApiRequest SetHeader(string name, string value)
        {
            if (Header == null) Header = new Dictionary<string, string>();
            Header[name] = value;
            return this;
        }

        /// <summary>
        /// 设置本次调用使用的 access_token，便于链式调用。
        /// </summary>
        /// <param name="accessToken">用户级或应用级 access_token。</param>
        /// <returns>当前请求对象，支持链式调用。</returns>
        public ApiRequest SetAccessToken(string accessToken)
        {
            AccessToken = accessToken;
            return this;
        }
    }
}
