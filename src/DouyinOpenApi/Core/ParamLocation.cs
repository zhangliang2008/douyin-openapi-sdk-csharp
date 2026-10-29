namespace DouyinOpenApi
{
    /// <summary>
    /// 描述「请求参数放在报文的哪个位置」。
    /// <para>
    /// Go SDK 的生成代码在方法体里显式拼装 <c>bodyParam</c> / <c>queryParam</c>，
    /// 编译期就决定了每个字段的去向。C# 端改成运行时反射绑定，因此需要把这个
    /// 位置信息作为参数传给执行器，取值来自对 Go 生成代码的解析结果：
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="JsonBody"/>：414 个接口</description></item>
    ///   <item><description><see cref="Query"/>：161 个接口</description></item>
    ///   <item><description><see cref="None"/>：54 个接口（只需 access_token）</description></item>
    ///   <item><description><see cref="FormUrlEncoded"/>：5 个接口；<see cref="Multipart"/>：5 个接口</description></item>
    ///   <item><description>其中 34 个接口同时使用 body 与 query，这类接口在调用时会额外传「query 字段名清单」</description></item>
    /// </list>
    /// </summary>
    public enum ParamLocation
    {
        /// <summary>该接口不携带任何业务参数（仅需 access_token）。</summary>
        None = 0,

        /// <summary>参数以 JSON 形式放在请求 body 中（Content-Type: application/json）。</summary>
        JsonBody = 1,

        /// <summary>参数以查询串形式拼在 URL 上。</summary>
        Query = 2,

        /// <summary>参数以表单形式放在 body 中（application/x-www-form-urlencoded）。</summary>
        FormUrlEncoded = 3,

        /// <summary>参数以 multipart/form-data 形式提交，通常包含文件流。</summary>
        Multipart = 4
    }
}
