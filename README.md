# 链接失效核验工具

面向中文内容平台和新闻网站的 Windows 批量链接状态核验工具。当前版本为 4.5.5。

工具把结论分为“仍可访问”“已失效”“暂时异常”和“人工复核”。确定结论必须有目标内容编号、标题、正文、作者、平台官方状态或明确目标错误页等证据。HTTP 200、403、502、登录页、验证码、WAF 和通用页面本身都不足以证明内容有效或失效。

## 使用

1. 下载并完整解压便携包。
2. 双击 `启动工具.cmd`。
3. 粘贴链接或导入 Excel，启动基础核验。
4. 基础核验完成后，根据需要手动启动“自动补证”。

进度按任务逐条保存。程序中断后可以恢复上次核验，不会把未取得充分证据的链接自动归为有效。

完整操作说明见 `使用说明.txt`，判定边界见 `核验逻辑说明.txt`。

## 本地验证

首次构建会从 NuGet 下载 Microsoft WebView2 SDK：

```powershell
.\prepare-runtime.ps1
```

运行核心回归测试：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\开发文件\Run-CoreTests.ps1' -RegressionOnly
```

运行发布验证契约测试：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\开发文件\Test-ValidationScripts.ps1'
```

如所在网络需要显式 HTTP 代理，可在启动前设置 `LINK_CHECKER_HTTP_PROXY`（例如
`http://127.0.0.1:7893`）。未设置时自动读取 Windows 固定 WinINET 代理及绕过列表（PAC/WPAD 仍交给系统代理）；代理、403、验证码或通用空壳都不会被当作目标内容已失效。

## 发布边界

没有足量、近期、人工确认的真值样本时，生成的包只能标记为候选版。轮换样本的确定率用于衡量覆盖能力，不等同于真实准确率，也不能代替人工验收。

本仓库不提交 API Token、Cookie、登录账号、运行日志、客户原始数据或本地核验结果。
