#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Go SDK -> C# SDK 代码生成器。

用法:
    python tools/gen_csharp.py                 # 读取 _ref/go-sdk/client/client.go 并生成
    python tools/gen_csharp.py <go文件> <输出根目录>

产出:
    src/DouyinOpenApi/Models/GenModels_*.cs       请求/响应/嵌套模型
    src/DouyinOpenApi/Apis/DouyinClient.*.cs      639 个接口方法（async + sync 双份）

设计要点:
  1. 只生成「样板代码」，鉴权/重试/序列化/错误归一化等核心逻辑全部手写在 Core/ 下，
     生成器改动面越小，升级 Go SDK 时越安全。
  2. 参数位置（query / body / 表单 / multipart）不由 C# 侧猜，而是解析 Go 生成代码里
     显式写出的 bodyParam / queryParam 得到，保证与官方实现逐字段对齐。
  3. 类名、属性名、JSON 字段名原样沿用 Go 侧命名，方便两边对照查文档。
"""

import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from parse_go import (  # noqa: E402  复用解析器，避免两套解析逻辑漂移
    analyze_method, go_type_to_csharp, parse_methods, parse_structs,
)

ROOT = Path(__file__).resolve().parent.parent

# 手写实现，不参与生成
SKIP_STRUCTS = {"CommonRequest", "RuntimeOptions"}
# 手写实现，不参与生成
SKIP_METHODS = {"CommonOpenAPI"}

CS_VALUE_TYPES = {"long?", "int?", "double?", "float?", "bool?"}

# 已由 .NET 或本 SDK 手写提供的类型名，用于校验生成结果里没有悬空引用
BUILTIN_TYPE_NAMES = {
    "string", "object", "byte", "long", "int", "double", "float", "bool",
    "List", "Dictionary", "FormFile",
}

# 生成过程中发现的、指向不存在模型的类型名（生成结束后统一报告）
UNKNOWN_TYPE_REFS = set()

MODELS_PER_FILE = 180     # 每个模型文件包含的类型数量
METHODS_PER_FILE = 80     # 每个接口文件包含的方法数量（每接口 2 个方法）


# ---------------------------------------------------------------- 类型映射

def cs_type(go_type, structs):
    """
    把 Go 字段类型递归映射为 C# 类型字符串。

    需要覆盖的形态（按 Go SDK 实际出现的频次）:
        *string / *int64 / *int32 / *int / *bool / *float64
        []*string / []*int64 / []*int / []*int32 / []byte
        map[string]*string / map[int64][]*int64 / map[int64]*bool / map[string]*int32 ...
        *XxxModel / []*XxxModel           引用其它生成模型
        interface{} / []interface{}       任意 JSON
        *util.FileField                   上传文件
    """
    go_type = go_type.strip()

    if go_type == "*util.FileField":
        return "FormFile"
    if go_type == "interface{}":
        return "object"

    # ---- 切片 ----
    if go_type.startswith("[]"):
        inner = go_type[2:]
        # Go 的 []byte 在 JSON 里是 base64 字符串，映射成 byte[] 才能被 Newtonsoft 正确还原；
        # 若映射成 List<byte> 会变成数字数组，语义完全变了。
        if inner == "byte":
            return "byte[]"
        return "List<%s>" % cs_type(inner, structs)

    # ---- 映射 ----
    if go_type.startswith("map["):
        # 解析 map[K]V 的 K 与 V，K 一定是可比较的基本类型或 string
        depth = 0
        for i, ch in enumerate(go_type):
            if ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    key_go, value_go = go_type[4:i], go_type[i + 1:]
                    return "Dictionary<%s, %s>" % (cs_map_key(key_go), cs_type(value_go, structs))
        return "Dictionary<string, object>"

    base = go_type.lstrip("*")
    simple = {
        "string": "string",
        "int64": "long?",
        "int32": "int?",
        "int": "int?",
        "float64": "double?",
        "float32": "float?",
        "bool": "bool?",
        "byte": "byte",
    }
    if base in simple:
        return simple[base]
    return base   # 引用其它生成模型


def cs_map_key(go_type):
    """字典键类型：不允许可空值类型，因此单独映射（long? 不能作为 Dictionary 键的常见用法）。"""
    return {
        "string": "string",
        "int64": "long",
        "int32": "int",
        "int": "int",
        "bool": "bool",
    }.get(go_type.strip().lstrip("*"), "string")


def is_flexible(cs):
    """是否需要挂 JSON 柔性转换器（把 JSON 拆成 Dictionary/List，而不是 JObject）。"""
    return cs in ("object", "List<object>", "Dictionary<string, object>")


# ---------------------------------------------------------------- 模型生成

def gen_models(ir):
    """生成模型类源码，返回 [(文件名, 内容), ...]。"""
    structs = ir["structs"]

    # 找出被接口方法用作「请求类型」的结构体，这些类需要继承 ApiRequest
    request_types = {m["request"] for m in ir["methods"] if m["name"] not in SKIP_METHODS}
    # 请求类型 -> 接口元信息，用于类注释
    method_by_request = {}
    for m in ir["methods"]:
        if m["name"] in SKIP_METHODS:
            continue
        method_by_request[m["request"]] = m

    # 响应类型同样是 m["name"] + "Response"，用于类注释
    response_method = {}
    for m in ir["methods"]:
        if m["name"] in SKIP_METHODS:
            continue
        resp = m["request"][: -len("Request")] + "Response" if m["request"].endswith("Request") else None
        if resp:
            response_method[resp] = m

    files = []
    buffer = []
    file_index = 1
    emitted_types = 0

    def flush():
        nonlocal buffer, file_index, emitted_types
        if not buffer:
            return
        name = "GenModels_%02d.cs" % file_index
        files.append((name, "".join(buffer)))
        buffer = []
        file_index += 1
        emitted_types = 0

    for struct_name, info in structs.items():
        if struct_name in SKIP_STRUCTS:
            continue

        fields = info["fields"]
        is_request = struct_name in request_types

        # ---- 类注释 ----
        if is_request and struct_name in method_by_request:
            m = method_by_request[struct_name]
            doc = "请求参数：%s %s%s" % (m["method"] or "?", m["host"] or "?", m["path"] or "?")
        elif struct_name in response_method:
            m = response_method[struct_name]
            doc = "响应数据：%s %s%s" % (m["method"] or "?", m["host"] or "?", m["path"] or "?")
        else:
            doc = "嵌套数据模型：%s" % struct_name

        buffer.append("    /// <summary>%s</summary>\n" % escape_xml(doc))
        buffer.append("    public class %s%s\n    {\n" % (
            struct_name, " : ApiRequest" if is_request else ""))

        emitted = 0
        for fname, ftype, json_name in fields:
            # 请求类继承 ApiRequest 后，基类已提供 Header / AccessToken，不重复声明
            if is_request and fname in ("Header", "AccessToken"):
                continue

            cs = cs_type(ftype, structs)
            # 悬空引用校验：C# 编译期会报错，但提前在生成阶段报出来更容易定位是解析漏了类型
            for ident in re.findall(r"[A-Za-z_][A-Za-z0-9_]*", cs):
                if ident in BUILTIN_TYPE_NAMES or ident in structs:
                    continue
                UNKNOWN_TYPE_REFS.add("%s.%s: %s" % (struct_name, fname, cs))
            buffer.append('        /// <summary>字段 <c>%s</c></summary>\n' % escape_xml(json_name))
            if is_flexible(cs):
                buffer.append("        [JsonConverter(typeof(FlexibleObjectConverter))]\n")
            buffer.append('        [JsonProperty("%s")]\n' % json_name)
            buffer.append("        public %s %s { get; set; }\n\n" % (cs, fname))
            emitted += 1

        if emitted == 0:
            # 参数为空的请求类：去掉多余的换行，保持文件整洁
            buffer[-1] = buffer[-1].rstrip("\n") + "\n"
        else:
            buffer[-1] = buffer[-1].rstrip("\n") + "\n"

        buffer.append("    }\n\n")
        emitted_types += 1

        if emitted_types >= MODELS_PER_FILE:
            flush()

    flush()

    header = (
        "// <auto-generated>\n"
        "//   本文件由 tools/gen_csharp.py 依据 Go SDK（github.com/bytedance/douyin-openapi-sdk-go v1.0.3）自动生成。\n"
        "//   请勿手工修改：重新生成请执行  python tools/gen_csharp.py\n"
        "// </auto-generated>\n"
        "#pragma warning disable CS1591\n"
        "\n"
        "using System.Collections.Generic;\n"
        "using Newtonsoft.Json;\n"
        "\n"
        "namespace DouyinOpenApi.Models\n"
        "{\n"
    )
    footer = "}\n"

    return [(name, header + content + footer) for name, content in files]


# ---------------------------------------------------------------- 接口生成

def module_of(host, path):
    """按域名/路径推断模块名，用于把接口拆到不同的 partial 文件里。"""
    host_map = {
        "minigame.zijieapi.com": "Minigame",
        "developer.toutiao.com": "Developer",
        "webcast.bytedance.com": "Webcast",
        "open-platform-qa.byted.org": "Qa",
    }
    if host in host_map:
        return host_map[host]

    segs = [s for s in path.split("/") if s]
    drop = {"api", "mgplatform", "goodlife", "data", "extern", "open", "tool"}
    candidates = [s for s in segs if s not in drop and not re.match(r"^v\d+$", s)]
    if not candidates:
        return "Default"

    first = pascal(candidates[0])
    # /api/apps/trade、/api/apps/ecpay 这类单独成模块，避免全塞进 Apps
    if candidates[0] == "apps" and len(candidates) > 1 and candidates[1] in ("trade", "ecpay"):
        return first + pascal(candidates[1])
    return first


def pascal(text):
    """把 snake_case / kebab-case 转成 PascalCase。"""
    parts = re.split(r"[_\-\s]+", text)
    return "".join(p[:1].upper() + p[1:] for p in parts if p)


def gen_apis(ir):
    """生成 639 个接口方法，返回 [(文件名, 内容), ...]。"""
    structs = ir["structs"]
    methods = [m for m in ir["methods"] if m["name"] not in SKIP_METHODS]

    # 按模块分组；同名方法忽略大小写去重，避免生成 CS0111（重复定义）
    groups = {}
    seen_methods = set()
    for m in methods:
        key = m["name"].lower()
        if key in seen_methods:
            print("跳过重复方法（大小写不敏感重名）: %s" % m["name"])
            continue
        seen_methods.add(key)
        module = module_of(m["host"], m["path"])
        groups.setdefault(module, []).append(m)

    files = []
    for module in sorted(groups):
        items = groups[module]
        # 模块过大时切分，单文件控制在 METHODS_PER_FILE 个接口以内
        for idx in range(0, len(items), METHODS_PER_FILE):
            chunk = items[idx: idx + METHODS_PER_FILE]
            suffix = "" if idx == 0 else ".Part%d" % (idx // METHODS_PER_FILE + 1)
            name = "DouyinClient.%s%s.cs" % (module, suffix)
            files.append((name, render_api_file(module, chunk, structs)))

    return files


def render_api_file(module, methods, structs):
    """渲染单个接口文件。"""
    body = []
    body.append("    public sealed partial class DouyinClient\n    {\n")

    for m in methods:
        req_type = m["request"]
        method_name = m["name"]
        resp_type = req_type[: -len("Request")] + "Response" if req_type.endswith("Request") else None
        if resp_type is None:
            print("跳过无法推断响应类型的方法: %s" % method_name)
            continue

        # ---- 参数位置 ----
        body_fields = [p for p in m["params"].values() if p["in"] == "body" and p["field"]]
        query_fields = [p for p in m["params"].values() if p["in"] == "query" and p["field"]]

        if m["multipart"]:
            location = "Multipart"
        elif m["content_type"] == "application/x-www-form-urlencoded":
            location = "FormUrlEncoded"
        elif body_fields and query_fields:
            location = "JsonBody"
        elif body_fields:
            location = "JsonBody"
        elif query_fields:
            location = "Query"
        else:
            location = "None"

        # body + query 混用时，把 query 侧的属性名显式传下去，其余字段才进 body
        extra_query = []
        if location in ("JsonBody", "FormUrlEncoded", "Multipart") and query_fields:
            extra_query = [p["field"] for p in query_fields]

        # access_token 是否也要放进 body（少数接口要求）
        token_in_body = any(
            p["field"] == "AccessToken" and p["in"] == "body" for p in m["params"].values()
        )

        host = m["host"]
        path = m["path"]
        http_method = m["method"]

        location_note = {
            "JsonBody": "body(JSON)",
            "Query": "query",
            "FormUrlEncoded": "body(表单)",
            "Multipart": "body(multipart)",
            "None": "无业务参数",
        }[location]

        body.append("        /// <summary>\n")
        body.append("        /// %s %s%s<br/>\n" % (http_method, host, path))
        body.append("        /// 参数位置：%s\n" % location_note)
        body.append("        /// </summary>\n")
        body.append("        /// <param name=\"request\">请求参数，不可为 null。</param>\n")
        body.append("        /// <param name=\"runtime\">本次调用的运行时选项；为空则使用客户端全局配置。</param>\n")
        body.append("        /// <param name=\"cancellationToken\">取消令牌。</param>\n")
        body.append("        public async Task<%s> %sAsync(%s request, RuntimeOptions runtime = null, CancellationToken cancellationToken = default(CancellationToken))\n" % (
            resp_type, method_name, req_type))
        body.append("        {\n")
        body.append("            return await ExecuteCoreAsync<%s>(\n" % resp_type)
        body.append("                request, \"%s\", \"%s\", \"%s\",\n" % (host, path, http_method))
        body.append("                ParamLocation.%s, %s, %s, runtime, cancellationToken).ConfigureAwait(false);\n" % (
            location, render_query_array(extra_query), "true" if token_in_body else "false"))
        body.append("        }\n\n")

        body.append("        /// <summary>\n")
        body.append("        /// %s 的同步版本（内部以同步方式等待异步结果，WinForms/控制台可直接调用）。\n" % method_name)
        body.append("        /// </summary>\n")
        body.append("        public %s %s(%s request, RuntimeOptions runtime = null)\n" % (resp_type, method_name, req_type))
        body.append("        {\n")
        body.append("            return %sAsync(request, runtime).GetAwaiter().GetResult();\n" % method_name)
        body.append("        }\n\n")

    body.append("    }\n")

    header = (
        "// <auto-generated>\n"
        "//   本文件由 tools/gen_csharp.py 自动生成（模块：%s）。请勿手工修改。\n"
        "// </auto-generated>\n"
        "#pragma warning disable CS1591\n"
        "\n"
        "using System.Threading;\n"
        "using System.Threading.Tasks;\n"
        "using DouyinOpenApi.Models;\n"
        "\n"
        "namespace DouyinOpenApi\n"
        "{\n" % module
    )
    return header + "".join(body) + "}\n"


def render_query_array(fields):
    """渲染额外 query 字段数组字面量。"""
    if not fields:
        return "null"
    quoted = ", ".join('"%s"' % f for f in sorted(set(fields)))
    return "new[] { %s }" % quoted


def escape_xml(text):
    """转义 XML 文档注释中的特殊字符。"""
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


# ---------------------------------------------------------------- 入口

def main():
    go_file = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "_ref/go-sdk/client/client.go"
    out_root = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "src/DouyinOpenApi"

    print("解析 %s" % go_file)
    lines = go_file.read_text(encoding="utf-8").splitlines()
    ir = {
        "structs": parse_structs(lines),
        "methods": [analyze_method(m) for m in parse_methods(lines)],
    }
    print("  struct=%d method=%d" % (len(ir["structs"]), len(ir["methods"])))

    models_dir = out_root / "Models"
    apis_dir = out_root / "Apis"
    models_dir.mkdir(parents=True, exist_ok=True)
    apis_dir.mkdir(parents=True, exist_ok=True)

    # 清理旧产物，避免改名后残留
    for p in list(models_dir.glob("GenModels_*.cs")) + list(apis_dir.glob("DouyinClient.*.cs")):
        p.unlink()

    model_files = gen_models(ir)
    api_files = gen_apis(ir)

    for name, content in model_files:
        (models_dir / name).write_text(content, encoding="utf-8")
    for name, content in api_files:
        (apis_dir / name).write_text(content, encoding="utf-8")

    print("生成模型文件 %d 个，接口文件 %d 个" % (len(model_files), len(api_files)))
    print("  模型总字节: %.1f KB" % (sum(len(c) for _, c in model_files) / 1024.0))
    print("  接口总字节: %.1f KB" % (sum(len(c) for _, c in api_files) / 1024.0))

    if UNKNOWN_TYPE_REFS:
        print("\n[警告] 发现 %d 处指向未知类型的字段引用：" % len(UNKNOWN_TYPE_REFS))
        for item in sorted(UNKNOWN_TYPE_REFS)[:20]:
            print("  " + item)


if __name__ == "__main__":
    main()
