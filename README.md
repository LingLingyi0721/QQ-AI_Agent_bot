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

## 架构

- 接入方式：**OneBot11 正向 WebSocket**。bot 作为 WS 客户端连 NapCat 暴露的 `ws://127.0.0.1:3001`（NapCat 侧开启「WebSocket 服务器」），不用反向 HTTP。
- 大模型：**OpenAI 兼容**的 `/chat/completions` 接口（DeepSeek / OpenAI / 第三方中转均可），只依赖 `aiohttp`，不引入官方 SDK。
- 代码结构：
  - `src/config.py` 配置加载（yaml + .env + 环境变量覆盖）
  - `src/onebot.py` WS 连接、收事件、调 action、断线指数退避重连
  - `src/llm.py` chat/completions 调用
  - `src/context.py` 按会话存上下文（JSON 原子落盘 + TTL）
  - `src/handler.py` 事件解析、触发判定、冷却、回复切分
  - `src/main.py` 入口，SIGINT 优雅退出
- 上下文 key：私聊 `private:<user_id>`，群聊 `group:<group_id>:<user_id>`；只保留最近 `history_turns * 2` 条消息，超 `history_ttl_minutes` 未使用自动丢弃。
- 触发规则（`config/config.yaml` 的 `reply` 段）：
  1. 自己发的消息、`blacklist_users` 里的用户 → 不回
  2. 私聊：`private_enabled` 为 true 才回
  3. 群聊：`group_enabled` 必须为 true；`whitelist_groups` 非空时只回白名单群；`group_require_at` 为 true 时必须 @ 到 bot
  4. `command_prefix`（默认 `/ai `）优先于 @ 要求：`/ai 你好` 在允许的群里不 @ 也生效，前缀会被剥离
  5. 清洗后文本为空 → 不回
- 配置分工：
  - `config/config.yaml`：行为与连接参数（WS 地址、触发规则、冷却、切分长度、存储路径），**不入库**
  - `.env`：密钥与模型（`LLM_BASE_URL` / `LLM_API_KEY` / `LLM_MODEL`），**不入库**
  - `.env` 里的 `LLM_*`、`ONEBOT_WS_URL`、`ONEBOT_ACCESS_TOKEN` 会覆盖 yaml 同名项
- 回复超过 `max_reply_chars` 时按段落 / 句号切分成多条发送；同一会话串行处理，避免乱序。

人设按 `llm.model` 自动选择：`config/personas/` 下 deepseek / claude / gpt / gemini / qwen / glm 六份社区二创人格，由 `llm.persona_map` 关键字匹配（小写包含，长关键字优先），未命中走 default。显式写 `system_prompt_file` 可强制覆盖。

人设素材来源：[ai-gijinka-art-world](https://github.com/Tests4gptplay/ai-gijinka-art-world)（commit c2e0492）。该库为社区二创视觉/人物档案，非厂商官方设定，明确留白的性格我们也不补。
本地人设文本为按档案重写的中文精简版，仅用于本 bot 的 system prompt。
署名链（DeepSeek 部分）：上善无形（鲸鱼娘原型）→ ZipZipPipe（DeepSeek 女仆二创）。
许可：上述来源库与人设文本改编部分均以 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/) 提供——署名、非商业性使用、相同方式共享。
适用范围：仅限 `config/personas/` 下的人设文本，不涉及本项目代码与其它文件。

## 快速开始

```powershell
# 1. 装依赖（aiohttp / python-dotenv / PyYAML）
pip install -r requirements.txt

# 2. 复制配置
Copy-Item config\config.example.yaml config\config.yaml
Copy-Item .env.example .env
# 编辑 .env，至少填 LLM_API_KEY（默认 DeepSeek，可换任意 OpenAI 兼容地址）

# 3. 自检（只检查不启动）
pwsh -NoProfile -File .\scripts\start-bot.ps1 -DryRun

# 4. 启动
pwsh -NoProfile -File .\scripts\start-bot.ps1
```

启动前 NapCat 需已登录并开启 OneBot11 的 WebSocket 服务端（默认端口 3001）。WS 地址与 access token 在 `config/config.yaml` 的 `onebot` 段或 `.env` 里改。

## 使用

```powershell
# 先校验（不启动）
pwsh -File .\scripts\start-qq-napcat.ps1 -DryRun

# 实际启动（需管理员）
pwsh -File .\scripts\start-qq-napcat.ps1
```

## 风险

非官方接口，违反 QQ 协议，有封号风险，建议小号。
