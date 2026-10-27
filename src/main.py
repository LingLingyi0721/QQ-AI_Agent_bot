"""入口：加载配置 → 组装组件 → 连接 OneBot11 正向 WS。"""

from __future__ import annotations

import asyncio
import logging
import signal
import sys

from .config import load_config, load_persona, mask_key, resolve_path, resolve_persona_path
from .context import ContextStore
from .handler import MessageHandler
from .llm import LLMClient
from .onebot import OneBotClient

logger = logging.getLogger("bot")


def _setup_logging() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )


def _rule_summary(cfg: dict) -> str:
    reply = cfg.get("reply") or {}
    private = "开" if reply.get("private_enabled", True) else "关"
    group = "开" if reply.get("group_enabled", True) else "关"
    need_at = "是" if reply.get("group_require_at", True) else "否"
    return (
        f"私聊={private} 群聊={group} 群里必须艾特={need_at} "
        f"前缀={reply.get('command_prefix')} "
        f"冷却={reply.get('cooldown_seconds')}s "
        f"单条上限={reply.get('max_reply_chars')}字 "
        f"群白名单={len(reply.get('whitelist_groups') or [])}个 "
        f"用户黑名单={len(reply.get('blacklist_users') or [])}个"
    )


def _log_startup(cfg: dict, persona_path) -> None:
    onebot = cfg["onebot"]
    llm = cfg["llm"]
    logger.info("ws_url   : %s", onebot.get("ws_url"))
    logger.info(
        "model    : %s at %s (key=%s)",
        llm.get("model"),
        llm.get("base_url"),
        mask_key(llm.get("api_key", "")),
    )
    logger.info("人设     : %s (%s)", persona_path.name, llm_cfg.get("model"))
    logger.info("触发规则 : %s", _rule_summary(cfg))


def _install_signal_handlers(stop: asyncio.Event) -> None:
    loop = asyncio.get_running_loop()
    try:
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, stop.set)
        return
    except (NotImplementedError, RuntimeError, ValueError, AttributeError):
        pass

    def _handler(signum, frame):
        try:
            loop.call_soon_threadsafe(stop.set)
        except RuntimeError:
            pass

    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            signal.signal(sig, _handler)
        except (ValueError, OSError):
            pass


async def run() -> int:
    cfg = load_config()
    for warning in cfg.get("warnings") or []:
        logger.warning(warning)

    llm_cfg = cfg["llm"]
    persona_path = resolve_persona_path(cfg)
    persona = load_persona(cfg)
    context_file = resolve_path(cfg, cfg["storage"].get("context_file", "data/context.json"))

    store = ContextStore(
        context_file,
        history_turns=llm_cfg.get("history_turns", 8),
        ttl_minutes=llm_cfg.get("history_ttl_minutes", 60),
    )
    llm = LLMClient(
        base_url=llm_cfg.get("base_url", ""),
        api_key=llm_cfg.get("api_key", ""),
        model=llm_cfg.get("model", "deepseek-chat"),
        temperature=llm_cfg.get("temperature", 0.7),
        max_tokens=llm_cfg.get("max_tokens", 1024),
        timeout=llm_cfg.get("timeout", 60),
    )

    onebot_cfg = cfg["onebot"]
    client = OneBotClient(
        ws_url=onebot_cfg.get("ws_url", ""),
        access_token=onebot_cfg.get("access_token", ""),
        reconnect_initial=onebot_cfg.get("reconnect_initial", 3),
        reconnect_max=onebot_cfg.get("reconnect_max", 30),
    )
    handler = MessageHandler(cfg, client, llm, store, persona)
    client.on_event = handler.handle_event

    _log_startup(cfg, persona_path)

    stop = asyncio.Event()
    _install_signal_handlers(stop)
    await client.start()

    logger.info("bot 已启动，按 Ctrl-C 退出")
    try:
        await stop.wait()
    except (KeyboardInterrupt, asyncio.CancelledError):
        pass
    finally:
        logger.info("正在退出...")
        await client.stop()
        await llm.close()
    return 0


def main() -> None:
    _setup_logging()
    try:
        code = asyncio.run(run())
    except KeyboardInterrupt:
        code = 0
    sys.exit(code)


if __name__ == "__main__":
    main()
