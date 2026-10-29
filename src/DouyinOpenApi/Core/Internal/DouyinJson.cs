using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DouyinOpenApi.Internal
{
    /// <summary>
    /// 全局 JSON 序列化配置，请求与响应共用同一套设置，避免两边行为不一致。
    /// </summary>
    internal static class DouyinJson
    {
        /// <summary>
        /// 序列化设置。
        /// </summary>
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // 与 Go SDK 的 encoding/json 行为对齐：是否忽略 null 由调用方在绑定参数阶段决定，
            // 这里保持 Include，保证"该发的字段一定发出去"。
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,

            // 关键：抖音大量字段是形如 "2024-01-01 00:00:00" 的字符串。
            // 若允许自动日期解析，反序列化到 object/interface{} 类型字段时会被悄悄变成 DateTime，
            // 调用方再取出来就不是原始字符串了，因此关闭自动解析。
            DateParseHandling = DateParseHandling.None,

            // 中文原样输出，不转成 \uXXXX，方便抓包肉眼核对
            Formatting = Formatting.None
        };

        /// <summary>反序列化到指定模型。</summary>
        internal static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        /// <summary>序列化为 JSON 文本。</summary>
        internal static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        /// <summary>
        /// 把一段 JSON 文本解析成 CLR 原生结构（对象→Dictionary、数组→List），供通用调用使用。
        /// </summary>
        internal static object ParseFlexible(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return FlexibleObjectConverter.ToClrObject(JToken.Parse(json));
        }

        /// <summary>
        /// 把一个值格式化成"查询串/表单字段"中的文本形式。
        /// <para>
        /// 规则对齐 Go SDK 的 <c>util.StringifyMapValue</c>：字符串原样、数字用不变文化输出、
        /// 数组/对象序列化成 JSON 文本（例如 <c>product_ids=[1,2,3]</c>）。
        /// </para>
        /// </summary>
        internal static string ToParamString(object value, bool sendNullAsDefault)
        {
            if (value == null) return sendNullAsDefault ? string.Empty : null;

            var s = value as string;
            if (s != null) return s;

            // 数值按不变文化输出，避免在 de-DE 之类的区域设置下把小数点写成逗号
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Int16:
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Double:
                case TypeCode.Single:
                case TypeCode.Decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);

                case TypeCode.Boolean:
                    return ((bool)value) ? "true" : "false";
            }

            var jtoken = value as JToken;
            if (jtoken != null) return jtoken.ToString(Formatting.None);

            // 数组、字典、嵌套模型统一走 JSON 文本，与 Go 的 ToJSONString 一致
            return Serialize(value);
        }
    }

    /// <summary>
    /// 把 JSON 里的 <c>object</c> / <c>interface{}</c> 反序列化成 .NET 原生结构。
    /// <para>
    /// Go SDK 里 <c>interface{}</c> 字段映射到 <c>map[string]interface{}</c> / <c>[]interface{}</c>；
    /// 而 Newtonsoft 默认会给出 <see cref="JObject"/>，调用方还得再学一套 JToken API。
    /// 这个转换器把 JObject 拆成 <see cref="Dictionary{TKey,TValue}"/>，与 Go 的取值习惯保持一致。
    /// </para>
    /// </summary>
    public sealed class FlexibleObjectConverter : JsonConverter
    {
        /// <summary>本转换器可处理任意类型的目标（实际由特性挂在 object 类属性上）。</summary>
        public override bool CanConvert(Type objectType) => true;

        /// <summary>本转换器仅用于读取响应，不参与写出。</summary>
        public override bool CanWrite => false;

        /// <summary>把 JSON 令牌转换成 CLR 原生结构。</summary>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return ToClrObject(JToken.Load(reader));
        }

        /// <summary>未使用：<see cref="CanWrite"/> 为 false。</summary>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("FlexibleObjectConverter 仅用于反序列化。");
        }

        /// <summary>
        /// 递归转换：对象→Dictionary&lt;string,object&gt;，数组→List&lt;object&gt;，数字→long/double，其余保持原生类型。
        /// </summary>
        internal static object ToClrObject(JToken token)
        {
            if (token == null) return null;

            switch (token.Type)
            {
                case JTokenType.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in ((JObject)token).Properties())
                        dict[prop.Name] = ToClrObject(prop.Value);
                    return dict;
                }
                case JTokenType.Array:
                {
                    var list = new List<object>();
                    foreach (var item in (JArray)token)
                        list.Add(ToClrObject(item));
                    return list;
                }
                case JTokenType.Integer:
                    // 抖音的 id 类字段可能超出 int 范围，统一用 long 承载
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                default:
                    return token.Value<object>();
            }
        }
    }
}
