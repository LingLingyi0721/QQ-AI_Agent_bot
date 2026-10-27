"""配置加载：config/config.yaml + .env，环境变量可覆盖关键项。"""

from __future__ import annotations

import copy
import os
from pathlib import Path
from typing import Any

import yaml
from dotenv import load_dotenv

ROOT = Path(__file__).resolve().parents[1]

DEFAULT_PERSONA = (
    "你是一个在 QQ 上聊天的普通网友，说话直接、口语化，不端着。"
    "回答简短、准确、有信息量；不知道就说不知道，不编造。"
)

DEFAULT_CONFIG: dict[str, Any] = {
    "onebot": {
        "ws_url": "ws://127.0.0.1:3001",
        "access_token": "",
        "reconnect_initial": 3,
        "reconnect_max": 30,
    },
    "llm": {
        "temperature": 0.7,
        "max_tokens": 1024,
        "timeout": 60,
        "history_turns": 8,
        "history_ttl_minutes": 60,
        "system_prompt_file": "",
        "persona_dir": "config/personas",
        "persona_map": {
            "deepseek": "deepseek.txt",
            "claude": "claude.txt",
            "gpt": "gpt.txt",
            "o1": "gpt.txt",
            "o3": "gpt.txt",
            "gemini": "gemini.txt",
            "qwen": "qwen.txt",
            "tongyi": "qwen.txt",
            "glm": "glm.txt",
            "chatglm": "glm.txt",
            "default": "default.txt",
        },
    },
    "reply": {
        "private_enabled": True,
        "group_enabled": True,
        "group_require_at": True,
        "command_prefix": "/ai ",
        "cooldown_seconds": 3,
        "max_reply_chars": 800,
        "error_reply": "（我这边暂时想不出话，等会儿再说）",
        "whitelist_groups": [],
        "blacklist_users": [],
    },
    "storage": {
        "context_file": "data/context.json",
    },
}


def _deep_merge(base: dict, override: dict) -> dict:
    result = copy.deepcopy(base)
    for key, value in override.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = copy.deepcopy(value)
    return result


def _read_yaml(path: Path) -> dict:
    try:
        with path.open("r", encoding="utf-8") as fp:
            data = yaml.safe_load(fp)
    except OSError as exc:
        print(f"[config] 读取配置失败 {path}: {exc}")
        return {}
    except yaml.YAMLError as exc:
        print(f"[config] 配置不是合法 YAML {path}: {exc}")
        return {}
    if data is None:
        return {}
    if not isinstance(data, dict):
        print(f"[config] 配置顶层应为映射 {path}，已忽略")
        return {}
    return data


def load_config(config_path: str | Path | None = None) -> dict[str, Any]:
    """读取配置，返回 dict；config.yaml 缺失时回退 config.example.yaml。"""
    path = Path(config_path) if config_path else ROOT / "config" / "config.yaml"
    example = ROOT / "config" / "config.example.yaml"
    warnings: list[str] = []
    user_cfg: dict = {}

    if path.is_file():
        user_cfg = _read_yaml(path)
    elif example.is_file():
        print(f"[config] 未找到 {path}，改用 {example}，请复制为 config.yaml 后再改")
        user_cfg = _read_yaml(example)
    else:
        print(f"[config] 未找到 {path} 与 {example}，使用内置默认配置")

    cfg = _deep_merge(DEFAULT_CONFIG, user_cfg)

    env_file = ROOT / ".env"
    if env_file.is_file():
        load_dotenv(env_file, override=False)
    else:
        print(f"[config] 未找到 {env_file}，仅使用系统环境变量")

    env = os.environ
    if env.get("ONEBOT_WS_URL"):
        cfg["onebot"]["ws_url"] = env["ONEBOT_WS_URL"]
    if env.get("ONEBOT_ACCESS_TOKEN"):
        cfg["onebot"]["access_token"] = env["ONEBOT_ACCESS_TOKEN"]
    if env.get("LLM_BASE_URL"):
        cfg["llm"]["base_url"] = env["LLM_BASE_URL"]
    if env.get("LLM_MODEL"):
        cfg["llm"]["model"] = env["LLM_MODEL"]
    if env.get("LLM_API_KEY"):
        cfg["llm"]["api_key"] = env["LLM_API_KEY"]
    else:
        cfg["llm"]["api_key"] = cfg["llm"].get("api_key", "")

    cfg["llm"].setdefault("model", "deepseek-chat")
    cfg["llm"].setdefault("base_url", "https://api.deepseek.com/v1")

    if not str(cfg["llm"].get("api_key") or "").strip():
        warnings.append("未配置 LLM_API_KEY，收到消息时只会回复错误提示")

    cfg["root"] = ROOT
    cfg["config_file"] = path
    cfg["warnings"] = warnings
    return cfg


def mask_key(key: str) -> str:
    """API key 打码，仅保留首尾少量字符。"""
    key = str(key or "")
    if not key:
        return "(未设置)"
    if len(key) <= 8:
        return key[:2] + "***"
    return f"{key[:4]}***{key[-4:]}"


def resolve_path(cfg: dict[str, Any], value: str | Path) -> Path:
    p = Path(value)
    if p.is_absolute():
        return p
    return Path(cfg.get("root", ROOT)) / p


def resolve_persona_path(cfg: dict[str, Any]) -> Path:
    """人设文件：显式 system_prompt_file 优先；否则按 model 名关键字匹配 persona_map。"""
    llm = cfg.get("llm") or {}
    explicit = str(llm.get("system_prompt_file") or "").strip()
    if explicit:
        return resolve_path(cfg, explicit)
    persona_dir = str(llm.get("persona_dir") or "config/personas")
    mapping = llm.get("persona_map") or {}
    model = str(llm.get("model") or "").lower()
    chosen = ""
    for key in sorted((k for k in mapping if k != "default"), key=len, reverse=True):
        if key and key in model:
            chosen = str(mapping[key])
            break
    if not chosen:
        chosen = str(mapping.get("default") or "default.txt")
    return resolve_path(cfg, f"{persona_dir.rstrip('/')}/{chosen}")


def load_persona(cfg: dict[str, Any]) -> str:
    """读取人设文本；文件缺失或为空时回退内置人设。"""
    path = resolve_persona_path(cfg)
    try:
        text = path.read_text(encoding="utf-8").strip()
    except OSError as exc:
        print(f"[config] 读取人设失败 {path}: {exc}，使用内置人设")
        cfg.setdefault("warnings", []).append(f"人设文件不可用: {path}")
        return DEFAULT_PERSONA
    if not text:
        print(f"[config] 人设文件为空 {path}，使用内置人设")
        return DEFAULT_PERSONA
    return text
