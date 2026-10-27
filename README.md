# QQ-AI_Agent_bot

基于本机非安装版 QQNT（工作目录 `E:\APP\QQ-AI_Agent`）做非官方 AI 聊天 bot，通过 NapCat 进程内注入接入 OneBot11。本项目位于该工作区内 `QQ-AI_Agent_bot` 子目录，与 QQ 本体目录同处一个工作区。

## 目录结构

```
runtime/napcat/extracted/   NapCat 解包产物（运行期依赖，不入库）
scripts/          启动脚本
qq-root.txt       本机 QQ 根目录配置（不入库）
src/              bot 主体代码（预留）
```

## 前置条件

- 非安装版 QQ 位于 `qq-root.txt` 指定的目录
- 版本定位只读该目录内 `versions\config.json`，不读注册表、不用全局变量

## 使用

```powershell
# 先校验（不启动）
pwsh -File .\scripts\start-qq-napcat.ps1 -DryRun

# 实际启动（需管理员）
pwsh -File .\scripts\start-qq-napcat.ps1
```

## 风险

非官方接口，违反 QQ 协议，有封号风险，建议小号。
