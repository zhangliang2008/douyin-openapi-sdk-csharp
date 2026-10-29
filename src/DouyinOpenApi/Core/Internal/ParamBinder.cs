using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace DouyinOpenApi.Internal
{
    /// <summary>
    /// 请求参数绑定器：把请求模型的属性按「位置」拆成 query / body 两组。
    /// <para>
    /// 为什么用反射而不是像 Go SDK 那样在方法体里手写 map：Go 的代码是生成出来的，
    /// 642 个接口每个都手写一遍 <c>bodyParam</c> 拼装无可厚非；C# 端若照抄，
    /// 生成出来的代码量会翻几倍且难以阅读。这里改为「属性元数据 + 运行时绑定」，
    /// 每个接口方法体只需一行，参数位置的正确性由生成阶段解析 Go 代码得到的结果保证。
    /// </para>
    /// <para>
    /// 元数据按类型缓存，反射只在首次调用某个请求类型时发生一次。
    /// </para>
    /// </summary>
    internal static class ParamBinder
    {
        /// <summary>单个请求属性的绑定元数据。</summary>
        internal sealed class ParamMeta
        {
            /// <summary>属性反射信息。</summary>
            public PropertyInfo Property;

            /// <summary>报文中的字段名（取自 [JsonProperty] 的 name）。</summary>
            public string JsonName;

            /// <summary>属性类型。</summary>
            public Type Type;

            /// <summary>是否为文件字段（仅有值语义的 multipart 接口会出现）。</summary>
            public bool IsFile;
        }

        /// <summary>绑定结果：body 字段集合 + query 字段序列。</summary>
        internal sealed class BoundParams
        {
            /// <summary>要放进 body 的字段（字段名 → 原始值）。</summary>
            public Dictionary<string, object> Body = new Dictionary<string, object>();

            /// <summary>要放进查询串的字段（字段名 → 已格式化的文本）。</summary>
            public List<KeyValuePair<string, string>> Query = new List<KeyValuePair<string, string>>();

            /// <summary>multipart 接口里的文件字段。</summary>
            public List<KeyValuePair<string, FormFile>> Files = new List<KeyValuePair<string, FormFile>>();
        }

        /// <summary>
        /// 类型 → 参与报文绑定的属性清单。缓存以避免每次请求都做反射。
        /// </summary>
        private static readonly ConcurrentDictionary<Type, ParamMeta[]> MetaCache =
            new ConcurrentDictionary<Type, ParamMeta[]>();

        /// <summary>
        /// 取某个请求类型的可绑定属性（已排除基类 <see cref="ApiRequest"/> 的 Header / AccessToken）。
        /// </summary>
        internal static ParamMeta[] GetMetas(Type requestType)
        {
            return MetaCache.GetOrAdd(requestType, BuildMetas);
        }

        /// <summary>
        /// 反射扫描属性并生成元数据。仅收集带 <see cref="JsonPropertyAttribute"/> 且非继承自基类的属性。
        /// </summary>
        private static ParamMeta[] BuildMetas(Type requestType)
        {
            var list = new List<ParamMeta>();
            foreach (var p in requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // 跳过基类上的 Header / AccessToken：它们描述的是报文头，不是业务字段
                if (p.DeclaringType == typeof(ApiRequest)) continue;
                if (!p.CanRead) continue;

                var attr = p.GetCustomAttribute<JsonPropertyAttribute>(true);
                if (attr == null || string.IsNullOrEmpty(attr.PropertyName)) continue;

                list.Add(new ParamMeta
                {
                    Property = p,
                    JsonName = attr.PropertyName,
                    Type = p.PropertyType,
                    IsFile = typeof(FormFile).IsAssignableFrom(p.PropertyType)
                });
            }

            return list.ToArray();
        }

        /// <summary>
        /// 按参数位置把请求对象的字段分配到 query / body。
        /// </summary>
        /// <param name="request">请求对象。</param>
        /// <param name="location">参数主位置。</param>
        /// <param name="extraQueryFields">额外的 query 字段（C# 属性名）；用于「body + query 混用」的接口。</param>
        /// <param name="accessToken">已解析出的 access_token，仅在 <paramref name="accessTokenInBody"/> 为 true 时写入 body。</param>
        /// <param name="accessTokenInBody">少数接口要求把 access_token 放在 body 里。</param>
        /// <param name="options">运行时选项，影响空值处理。</param>
        internal static BoundParams Bind(
            ApiRequest request,
            ParamLocation location,
            string[] extraQueryFields,
            string accessToken,
            bool accessTokenInBody,
            RuntimeOptions options)
        {
            var bound = new BoundParams();
            var metas = GetMetas(request.GetType());

            // 把「额外的 query 字段名」转成集合，便于 O(1) 过滤
            HashSet<string> queryFieldSet = null;
            if (extraQueryFields != null && extraQueryFields.Length > 0)
                queryFieldSet = new HashSet<string>(extraQueryFields, StringComparer.Ordinal);

            foreach (var meta in metas)
            {
                var value = meta.Property.GetValue(request, null);
                var isExtraQueryField = queryFieldSet != null && queryFieldSet.Contains(meta.Property.Name);

                if (location == ParamLocation.Query || isExtraQueryField)
                {
                    // 未赋值的查询参数默认不发送（见 RuntimeOptions.OmitUnsetQueryParameters 的说明）
                    if (value == null && options.OmitUnsetQueryParameters) continue;

                    string text;
                    if (TryFormatQueryValue(value, meta.Type, options.SendNullAsDefaultValue, out text))
                        bound.Query.Add(new KeyValuePair<string, string>(meta.JsonName, text));
                    continue;
                }

                if (location == ParamLocation.None) continue;

                // ---- body 类位置（JSON / 表单 / multipart）----
                if (meta.IsFile)
                {
                    var file = value as FormFile;
                    if (file != null) bound.Files.Add(new KeyValuePair<string, FormFile>(meta.JsonName, file));
                    continue;
                }

                object normalized;
                if (TryNormalizeBodyValue(value, meta.Type, options.SendNullAsDefaultValue, out normalized))
                    bound.Body[meta.JsonName] = normalized;
            }

            if (accessTokenInBody && !string.IsNullOrEmpty(accessToken))
                bound.Body["access_token"] = accessToken;

            return bound;
        }

        /// <summary>
        /// 把 body 字段值规范化成「与 Go SDK 一致」的形态：
        /// <list type="bullet">
        ///   <item><description>未赋值的 string → <c>""</c>（Go 的 <c>tea.StringValue(nil)</c>）</description></item>
        ///   <item><description>未赋值的数字 → <c>0</c>、bool → <c>false</c></description></item>
        ///   <item><description>未赋值的数组/对象 → <c>null</c>（Go 里 nil 切片序列化就是 null）</description></item>
        /// </list>
        /// </summary>
        /// <returns>返回 false 表示该字段应被省略（仅当 <paramref name="sendNullAsDefault"/> 为 false 时发生）。</returns>
        private static bool TryNormalizeBodyValue(object value, Type type, bool sendNullAsDefault, out object normalized)
        {
            normalized = value;
            if (value != null) return true;

            if (!sendNullAsDefault) return false;   // 调用方要求省略空字段

            if (type == typeof(string)) { normalized = string.Empty; return true; }

            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(long)) { normalized = 0L; return true; }
            if (underlying == typeof(int)) { normalized = 0; return true; }
            if (underlying == typeof(double)) { normalized = 0D; return true; }
            if (underlying == typeof(float)) { normalized = 0F; return true; }
            if (underlying == typeof(decimal)) { normalized = 0M; return true; }
            if (underlying == typeof(bool)) { normalized = false; return true; }

            // 数组 / 字典 / 嵌套模型 / object：保持 null，交由序列化器输出 JSON null
            normalized = null;
            return true;
        }

        /// <summary>
        /// 把查询参数字格式化成文本。
        /// <para>
        /// 规则严格对齐 Go SDK：查询参数在 Go 侧由 <c>tea.XxxValue()</c> 取值后交给
        /// <c>StringifyMapValue</c>，其中
        /// </para>
        /// <list type="bullet">
        ///   <item><description>字符串/数字/bool 的 nil 会先被取成零值（<c>""</c> / <c>0</c> / <c>false</c>）再参与拼接，所以一定会出现；</description></item>
        ///   <item><description>切片与 interface{} 的 nil 仍是 nil，<c>StringifyMapValue</c> 会直接跳过，所以不出现。</description></item>
        /// </list>
        /// 这个差异如果不还原，分页类接口的 <c>page=0</c> 之类参数就会丢，
        /// 与官方 SDK 行为不一致，排查时极易误判。
        /// </summary>
        /// <returns>false 表示该字段应省略。</returns>
        private static bool TryFormatQueryValue(object value, Type type, bool sendNullAsDefault, out string text)
        {
            if (value != null)
            {
                text = DouyinJson.ToParamString(value, sendNullAsDefault);
                return true;
            }

            text = null;
            if (!sendNullAsDefault) return false;

            if (type == typeof(string)) { text = string.Empty; return true; }

            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(long)) { text = "0"; return true; }
            if (underlying == typeof(int)) { text = "0"; return true; }
            if (underlying == typeof(double)) { text = "0"; return true; }
            if (underlying == typeof(float)) { text = "0"; return true; }
            if (underlying == typeof(decimal)) { text = "0"; return true; }
            if (underlying == typeof(bool)) { text = "false"; return true; }

            // 集合 / 字典 / 嵌套模型 / object：Go 侧为 nil，跳过
            return false;
        }

        /// <summary>
        /// 把绑定结果渲染成查询串（不含前导 <c>?</c>）。
        /// </summary>
        /// <param name="query">绑定得到的 query 字段（值已格式化）。</param>
        internal static string BuildQueryString(List<KeyValuePair<string, string>> query)
        {
            if (query == null || query.Count == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var pair in query)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(pair.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把绑定结果渲染成 application/x-www-form-urlencoded 的键值对。
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildFormFields(Dictionary<string, object> body, bool sendNullAsDefault)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var pair in body)
            {
                if (pair.Value is IDictionary || pair.Value is IList)
                {
                    // Go 的 ToFormString 用 fmt.Sprintf("%v") 输出，切片会变成 "[1 2 3]"；
                    // 这里改用 JSON 文本（"[1,2,3]"），对方解析更友好，且仅影响 5 个表单接口。
                    result.Add(new KeyValuePair<string, string>(pair.Key, DouyinJson.Serialize(pair.Value)));
                    continue;
                }

                var text = DouyinJson.ToParamString(pair.Value, sendNullAsDefault);
                if (text == null) continue;
                result.Add(new KeyValuePair<string, string>(pair.Key, text));
            }
            return result;
        }
    }
}
