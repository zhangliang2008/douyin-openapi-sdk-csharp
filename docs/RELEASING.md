# 构建、测试与发布

> 本文档面向**仓库维护者**，不面向 SDK 使用者。
> 使用说明见 [README](../README.md)。
>
> 之所以单独拆出来：`README.md` 通过 `<PackageReadmeFile>` 被打进 nupkg，会原样显示在
> nuget.org 的包页面上；把发布流程写在里面，等于让每个装包的人都看到维护者的操作手册。

## 1. 持续集成（`.github/workflows/ci.yml`）

每次推 `main` 或提 PR 自动执行，全部跑在 `windows-latest`（`net47` 的测试需要 .NET Framework 运行时）：

1. `dotnet restore`
2. `dotnet build -c Release`（一次编出 net47 / netstandard2.0，测试工程再带 net8.0）
3. `dotnet test -c Release`（net47 + net8.0 两套运行时各跑 32 项）
4. `dotnet pack` 试打包 —— 防止「代码能编但包打不出来」
5. 上传测试报告与 nupkg 作为构建产物，便于人工核对包内容

只改文档（`*.md` / `LICENSE` / `NOTICE`）的提交不会触发，省 Actions 额度。

## 2. 自动发布（`.github/workflows/release.yml`）

打 tag 即发布：

```bash
git tag v1.0.4
git push origin v1.0.4
```

流水线会：按 tag 推导版本号 → 编译 → **跑全部测试（不过就不发）** → `dotnet pack -p:Version=1.0.4` →
推到 nuget.org → 创建 GitHub Release 并附上 nupkg。

也可以在 Actions 页面手动触发（`workflow_dispatch`），填入版本号发布，不依赖 tag。

## 3. 发布凭证：OIDC Trusted Publishing

仓库里不存任何长期密钥。GitHub 签发短期 OIDC token，`NuGet/login@v1` 用它换一个
**有效期 1 小时**的临时 API Key，`dotnet nuget push` 直接使用。

一次性准备两步：

**Step 1 — nuget.org** → 右上头像 → **Trusted Publishing** → Add new policy

| 字段 | 值 |
| --- | --- |
| Repository Owner | `zhangliang2008` |
| Repository | `douyin-openapi-sdk-csharp` |
| Workflow File | `release.yml`（只填文件名，**不带** `.github/workflows/` 前缀） |
| Environment | 留空（本工作流未用 GitHub Environments） |

同页配置 Scopes：勾 `Push new packages and package versions`，`Unlist or relist` 不勾（最小权限，
下架走网页操作即可）；Glob Pattern 填 `DouyinOpenApi`（精确匹配，必须与 `PackageId` 一致）。

**Step 2 — 本仓库** → `Settings → Secrets and variables → Actions` → New repository secret

| 名称 | 值 |
| --- | --- |
| `NUGET_USER` | nuget.org 的用户名（profile name，**不是**邮箱，也**不是** GitHub 用户名） |

## 4. 排查记录（真实踩过的坑）

按发生频率排序，遇到红色先查这三条。

### 4.1 `未配置 NUGET_USER` / `未配置 NUGET_API_KEY`

Secret 名字拼错，或者**改了 workflow 却没重新打 tag**。见下一条。

### 4.2 明明改了 workflow，跑的还是旧逻辑

**tag 触发时，GitHub Actions 用的是 tag 所指向那个 commit 里的 workflow 文件**，
不是工作区、也不是 `main` 的最新版本。所以「改完 workflow → 重推同一个 tag」不会生效。

```bash
# 确认 tag 指向哪个 commit、那个 commit 里的 workflow 长什么样
git rev-parse v1.0.3
git show v1.0.3:.github/workflows/release.yml | grep -n "NUGET_API_KEY\|NuGet/login"
```

处理方式：内容有改动就换新版本号重新打 tag。**只有在版本尚未发布成功时**才能删掉重建：

```bash
git tag -d v1.0.3
git push origin :refs/tags/v1.0.3
# 改完并推 main 之后
git tag v1.0.3 && git push origin v1.0.3
```

已发布成功的版本绝对不能删 tag 重建——包已进 nuget.org，版本号不可回收，重建只会让 tag
与线上产物对不上。

### 4.3 `NUGET_USER` 填成了 GitHub 用户名

两个平台的账户名往往不同。判断方法：nuget.org 的 Trusted Publishing 策略页上
`Package owner` 后面那个名字才是 profile name。填错的表现是 `NuGet/login` 认证失败
（nuget.org 去校验一个不存在的账户）。

### 4.4 推送时 `schannel: failed to receive handshake`

本机环境变量里的 `HTTPS_PROXY` 指向一个连不通 GitHub 的本地代理，而 git 会读它（curl 不一定）。
临时绕过：

```bash
git -c http.proxy= -c https.proxy= push origin main
```

## 5. 发布前检查清单

- [ ] `git grep` 一遍真实凭据与账号 ID，确认没有硬编码残留（示例应只读环境变量）
- [ ] `dotnet test` 在 net47 与 net8.0 上全绿
- [ ] `src/DouyinOpenApi/DouyinOpenApi.csproj` 的 `<Version>` 与新 tag 一致
- [ ] README 的「项目来源与测试覆盖」一节仍与实际情况相符（新增真实联调过的接口时更新）
- [ ] 确认新版本号在 nuget.org 上未被占用：`https://api.nuget.org/v3-flatcontainer/douyinopenapi/index.json`

⚠️ NuGet 的版本号**占用后不能删除**（只能 unlist），所以流水线把「跑测试」放在推送之前当闸门。
