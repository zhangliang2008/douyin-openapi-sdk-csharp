#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析抖音开放平台 Go SDK 的生成代码 client.go，产出中间表示（IR, JSON）。

设计说明：
  Go SDK 是 Darabonba 生成器产出的大文件（约 21 万行），结构高度规整：
    1. 每个 API 一对 struct：XxxRequest / XxxResponse（另有大量嵌套子模型）
    2. 每个 API 一个 (*Client).Xxx 方法，方法体内把"业务字段"显式塞进
       bodyParam / queryParam 两个 map
  第 2 点非常关键：它等价于一份"字段属于 body 还是 query"的权威清单，
  借此可以在 C# 端用反射按位置自动组装参数，无需逐个 API 手写参数拼装。

本脚本只做"读 + 校验"，不生成 C#，方便单独排查解析假设是否成立。
"""

import json
import re
import sys
from pathlib import Path

GO_FILE_DEFAULT = Path(__file__).resolve().parent.parent / "_ref/go-sdk/client/client.go"

RE_TYPE = re.compile(r"^type\s+(\w+)\s+struct\s*\{$")
RE_FIELD = re.compile(r'^\s+(\w+)\s+(\S+)\s+`json:"([^"]*)"')
RE_METHOD = re.compile(r"^func \(client \*Client\) (\w+)\(request \*(\w+)\)")
RE_METHOD_BODY = re.compile(r"^func \(s \*Client\) (\w+)\(")

RE_REQ_METHOD = re.compile(r'request_\.Method = tea\.String\("(\w+)"\)')
RE_PATH = re.compile(r'request_\.Pathname = tea\.String\("([^"]*)"\)')
RE_HOST = re.compile(r'Headers\["host"\] = tea\.String\("([^"]*)"\)')
RE_CTYPE = re.compile(r'Headers\["content-type"\] = tea\.String\("([^"]*)"\)')
RE_PARAM_START = re.compile(r"^\s+(bodyParam|queryParam) := map\[string\]interface\{\}\{$")
RE_PARAM_ITEM = re.compile(r'^\s+"(\w+)":\s*(.+),$')
RE_ACCESSORS = re.compile(r"tea\.(\w+Value\w*)\(request\.(\w+)\)")
# 部分生成代码不走 tea.XxxValue，直接写 "Base": request.Base（值本身是结构体/切片）
RE_BARE_FIELD = re.compile(r"^request\.(\w+)$")


def parse_structs(lines):
    """提取所有 struct 定义。返回 {name: [(csharpish_name, go_type, json_name), ...]}"""
    structs = {}
    i, n = 0, len(lines)
    while i < n:
        m = RE_TYPE.match(lines[i])
        if not m:
            i += 1
            continue
        name = m.group(1)
        fields = []
        nested = False
        i += 1
        while i < n and lines[i].rstrip() != "}":
            fm = RE_FIELD.match(lines[i])
            if fm:
                # json 标签形如 `json:"sort_type,omitempty"`，逗号后是选项，字段名要截断
                json_name = fm.group(3).split(",")[0].strip()
                if json_name and json_name != "-":
                    fields.append((fm.group(1), fm.group(2), json_name))
            elif "struct {" in lines[i]:
                nested = True
            i += 1
        structs[name] = {"fields": fields, "nested": nested}
        i += 1
    return structs


def parse_methods(lines):
    """提取所有 (*Client).Xxx 方法的元数据。"""
    methods = []
    i, n = 0, len(lines)
    while i < n:
        m = RE_METHOD.match(lines[i])
        if not m:
            i += 1
            continue
        name, req_type = m.group(1), m.group(2)
        i += 1
        body = []
        while i < n and lines[i].rstrip() != "}":
            body.append(lines[i])
            i += 1
        methods.append({"name": name, "request": req_type, "body": body})
        i += 1
    return methods


def analyze_method(m):
    """从一个方法体里抽取调用元数据。"""
    text = "\n".join(m["body"])
    info = {"name": m["name"], "request": m["request"], "has_body_fn": False}

    mm = RE_REQ_METHOD.search(text)
    info["method"] = mm.group(1) if mm else None
    pm = RE_PATH.search(text)
    info["path"] = pm.group(1) if pm else None
    hm = RE_HOST.search(text)
    info["host"] = hm.group(1) if hm else None
    cm = RE_CTYPE.search(text)
    info["content_type"] = cm.group(1) if cm else None
    info["multipart"] = "util.ToFileForm(" in text

    # bodyParam / queryParam 是一份显式的字段位置清单，逐项记录
    params = {}
    kind = None
    for line in m["body"]:
        sm = RE_PARAM_START.match(line)
        if sm:
            kind = sm.group(1)
            continue
        if kind:
            im = RE_PARAM_ITEM.match(line)
            if im:
                key, expr = im.group(1), im.group(2).strip()
                am = RE_ACCESSORS.search(expr)
                bm = RE_BARE_FIELD.match(expr)
                params[key] = {
                    "in": "body" if kind == "bodyParam" else "query",
                    "field": am.group(2) if am else (bm.group(1) if bm else None),
                    "go_value_fn": am.group(1) if am else None,
                    "raw": expr,
                }
            elif line.strip() == "}":
                kind = None
    info["params"] = params
    return info


def go_type_to_csharp(go_type):
    """Go 类型 -> (C# 类型, 是否为值类型, 是否可空)"""
    t = go_type.lstrip("*")
    if t == "string":
        return "string"
    if t == "int64":
        return "long?"
    if t == "int32":
        return "int?"
    if t == "int":
        return "int?"
    if t == "float64":
        return "double?"
    if t == "float32":
        return "float?"
    if t == "bool":
        return "bool?"
    if t == "interface{}":
        return "object"
    if go_type.startswith("[]"):
        inner = go_type[2:]
        if inner == "interface{}":
            return "List<object>"
        if inner.startswith("map["):
            return "List<Dictionary<string, object>>"
        return "List<%s>" % go_type_to_csharp(inner)
    if go_type.startswith("map["):
        if "interface{}" in go_type:
            return "Dictionary<string, object>"
        return "Dictionary<string, string>"
    return go_type.lstrip("*")


def main():
    go_file = Path(sys.argv[1]) if len(sys.argv) > 1 else GO_FILE_DEFAULT
    out_file = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).parent / "ir.json"

    print("读取 %s" % go_file)
    lines = go_file.read_text(encoding="utf-8").splitlines()
    print("总行数: %d" % len(lines))

    structs = parse_structs(lines)
    methods = [analyze_method(m) for m in parse_methods(lines)]

    print("struct 数: %d" % len(structs))
    print("方法数: %d" % len(methods))

    # ---- 校验 1：是否存在嵌套匿名 struct（C# 生成需要特殊处理）----
    nested = [k for k, v in structs.items() if v["nested"]]
    print("\n[校验1] 嵌套匿名 struct: %d %s" % (len(nested), nested[:5]))

    # ---- 校验 2：Header / AccessToken 是否所有请求都有 ----
    reqs = {k: v for k, v in structs.items() if k.endswith("Request")}
    no_header, no_at = [], []
    for k, v in reqs.items():
        fnames = [f[0] for f in v["fields"]]
        if "Header" not in fnames:
            no_header.append(k)
        if "AccessToken" not in fnames:
            no_at.append(k)
    print("[校验2] 请求结构体 %d 个；缺 Header: %d %s；缺 AccessToken: %d %s"
          % (len(reqs), len(no_header), no_header[:5], len(no_at), no_at[:5]))

    # ---- 校验 3：字段集合 == 参数清单 + {Header, AccessToken} ----
    mismatches = []
    for m in methods:
        st = structs.get(m["request"])
        if not st:
            mismatches.append((m["name"], "缺少请求结构体 %s" % m["request"]))
            continue
        fields = {f[0] for f in st["fields"]} - {"Header", "AccessToken"}
        listed = {p["field"] for p in m["params"].values() if p["field"]}
        missing = fields - listed   # 结构体有字段但方法没送出去
        extra = listed - fields     # 方法送了但结构体没这个字段
        if missing or extra:
            mismatches.append((m["name"], "未送出=%s 多余=%s" % (sorted(missing), sorted(extra))))
    print("[校验3] 字段集合与参数清单不一致的方法: %d" % len(mismatches))
    for x in mismatches[:10]:
        print("        %s -> %s" % x)

    # ---- 校验 4：C# 命名安全 ----
    cs_keywords = set("""abstract as base bool break byte case catch char checked class const
        continue decimal default delegate do double else enum event explicit extern false
        finally fixed float for foreach goto if implicit in int interface internal is lock long
        namespace new null object operator out override params private protected public readonly
        ref return sbyte sealed short sizeof stackalloc static string struct switch this throw
        true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while""".split())
    bad_field, bad_type, same_name = [], [], []
    used_types = set()
    for name, st in structs.items():
        if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", name):
            bad_type.append(name)
        for fname, ftype, jname in st["fields"]:
            if fname.lower() in cs_keywords or not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", fname):
                bad_field.append((name, fname))
            if fname == name:
                same_name.append((name, fname))
            used_types.add(ftype)
    print("[校验4] 非法类型名: %d；非法/关键字字段名: %d %s；字段与类型同名: %d %s"
          % (len(bad_type), len(bad_field), bad_field[:5], len(same_name), same_name[:5]))

    # ---- 校验 5：类型清单（确认映射覆盖）----
    unknown = sorted({t for t in used_types
                      if go_type_to_csharp(t) not in ("string", "long?", "int?", "double?",
                                                      "float?", "bool?", "object")
                      and not go_type_to_csharp(t).startswith(("List<", "Dictionary<"))
                      and t.lstrip("*") not in structs})
    print("[校验5] 无法映射到已知类型的字段类型: %d %s" % (len(unknown), unknown[:10]))

    # ---- 输出 IR ----
    ir = {
        "structs": structs,
        "methods": methods,
    }
    out_file.write_text(json.dumps(ir, ensure_ascii=False), encoding="utf-8")
    print("\nIR 已写入 %s" % out_file)


if __name__ == "__main__":
    main()
