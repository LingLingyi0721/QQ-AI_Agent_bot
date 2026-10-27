"""OneBot11 消息事件解析、触发判定与回复流程。"""

from __future__ import annotations

import asyncio
import logging
import re
import time
from typing import Any

from .context import ContextStore
from .llm import LLMClient
from .onebot import OneBotClient

logger = logging.getLogger(__name__)

_CQ_RE = re.compile(r"\[CQ:[^\]]*\]")
_SENTENCE_RE = re.compile(r"(?<=[。！？!?；;…])")


# ---------- 事件解析 ----------

def extract_text(event: dict, self_id: Any) -> tuple[str, bool]:
    """返回 (纯文本, 是否被 @)。message 为数组时拼接 text 段，at 段不计入文本。"""
    message = event.get("message")
    if isinstance(message, str):
        return _strip_cq(message, self_id)
    if not isinstance(message, list):
        raw = event.get("raw_message")
        if isinstance(raw, str):
            return _strip_cq(raw, self_id)
        return "", False

    parts: list[str] = []
    at_me = False
    me = str(self_id) if self_id is not None else ""
    for seg in message:
        if not isinstance(seg, dict):
            continue
        seg_type = seg.get("type")
        data = seg.get("data") or {}
        if seg_type == "text":
            parts.append(str(data.get("text", "")))
        elif seg_type == "at":
            qq = str(data.get("qq", ""))
            if me and qq == me:
                at_me = True
    return "".join(parts), at_me


def _strip_cq(raw: str, self_id: Any) -> tuple[str, bool]:
    me = str(self_id) if self_id is not None else ""
    at_me = False

    def repl(match: re.Match) -> str:
        nonlocal at_me
        if me and f"qq={me}" in match.group(0):
            at_me = True
        return ""

    return _CQ_RE.sub(repl, raw), at_me


def context_key(event: dict) -> str:
    user_id = event.get("user_id")
    if event.get("message_type") == "group":
        return f"group:{event.get('group_id')}:{user_id}"
    return f"private:{user_id}"


# ---------- 触发判定（纯函数） ----------

def should_reply(event: dict, cfg: dict, self_id: Any) -> tuple[bool, str]:
    """按规则顺序判断是否回复，返回 (是否回复, 清洗后文本)。"""
    if event.get("post_type") != "message":
        return False, ""
    reply_cfg = cfg.get("reply") or {}
    user_id = str(event.get("user_id", ""))
    me = str(self_id) if self_id is not None else ""

    # 1. 忽略自己
    if me and user_id == me:
        return False, ""
    # 2. 黑名单
    blacklist = {str(x) for x in reply_cfg.get("blacklist_users") or []}
    if user_id in blacklist:
        return False, ""

    text, at_me = extract_text(event, self_id)
    text = text.strip()
    prefix = str(reply_cfg.get("command_prefix") or "")
    has_prefix = bool(prefix) and text.startswith(prefix)

    message_type = event.get("message_type")
    if message_type == "private":
        # 3. 私聊开关
        if not reply_cfg.get("private_enabled", True):
            return False, ""
    elif message_type == "group":
        # 4. 群聊开关 / 白名单 / @ 要求
        if not reply_cfg.get("group_enabled", True):
            return False, ""
        whitelist = {str(x) for x in reply_cfg.get("whitelist_groups") or []}
        if whitelist and str(event.get("group_id", "")) not in whitelist:
            return False, ""
        if reply_cfg.get("group_require_at", True) and not (at_me or has_prefix):
            return False, ""
    else:
        return False, ""

    # 5. 命令前缀（优先级高于 @ 要求）
    if has_prefix:
        text = text[len(prefix) :].strip()

    # 6. 清洗后为空不回复
    if not text:
        return False, ""
    return True, text


# ---------- 回复切分 ----------

def split_reply(text: str, max_chars: int) -> list[str]:
    """按段落 / 句子切分，单条不超过 max_chars。"""
    text = (text or "").strip()
    if not text:
        return []
    limit = int(max_chars or 0) or 800
    if limit <= 0:
        limit = 800
    if len(text) <= limit:
        return [text]

    segments: list[str] = []
    for para in re.split(r"\n+", text):
        para = para.strip()
        if not para:
            continue
        if len(para) <= limit:
            segments.append(para)
            continue
        for sentence in _SENTENCE_RE.split(para):
            sentence = sentence.strip()
            if not sentence:
                continue
            if len(sentence) <= limit:
                segments.append(sentence)
            else:
                segments.extend(
                    sentence[i : i + limit] for i in range(0, len(sentence), limit)
                )

    chunks: list[str] = []
    buf = ""
    for seg in segments:
        if not buf:
            buf = seg
        elif len(buf) + 1 + len(seg) <= limit:
            buf = f"{buf}\n{seg}"
        else:
            chunks.append(buf)
            buf = seg
    if buf:
        chunks.append(buf)
    return chunks or [text[:limit]]


# ---------- 处理器 ----------

class MessageHandler:
    def __init__(
        self,
        cfg: dict,
        client: OneBotClient,
        llm: LLMClient,
        store: ContextStore,
        persona: str,
    ) -> None:
        self._cfg = cfg
        self._client = client
        self._llm = llm
        self._store = store
        self._persona = persona
        reply_cfg = cfg.get("reply") or {}
        self._cooldown = max(0.0, float(reply_cfg.get("cooldown_seconds") or 0))
        self._max_chars = int(reply_cfg.get("max_reply_chars") or 800)
        self._error_reply = str(reply_cfg.get("error_reply") or "")
        self._locks: dict[str, asyncio.Lock] = {}
        self._last_reply: dict[str, float] = {}

    async def handle_event(self, event: dict) -> None:
        if event.get("post_type") != "message":
            return
        self_id = self._client.self_id or event.get("self_id")
        ok, text = should_reply(event, self._cfg, self_id)
        if not ok:
            return

        key = context_key(event)
        lock = self._locks.setdefault(key, asyncio.Lock())
        async with lock:
            now = time.monotonic()
            last = self._last_reply.get(key)
            if last is not None and now - last < self._cooldown:
                logger.info("冷却中，丢弃 %s 的消息", key)
                return
            self._last_reply[key] = now
            await self._reply(event, key, text)

    async def _reply(self, event: dict, key: str, text: str) -> None:
        history = await self._store.get(key)
        messages: list[dict[str, str]] = [{"role": "system", "content": self._persona}]
        messages.extend(history)
        messages.append({"role": "user", "content": text})

        answer = ""
        try:
            answer = await self._llm.chat(messages)
        except Exception as exc:  # noqa: BLE001
            logger.warning("LLM 调用失败: %s", exc)

        if not answer:
            await self._safe_send(event, self._error_reply)
            return

        for chunk in split_reply(answer, self._max_chars):
            if not await self._safe_send(event, chunk):
                return

        await self._store.append(key, "user", text)
        await self._store.append(key, "assistant", answer)

    async def _safe_send(self, event: dict, text: str) -> bool:
        if not text:
            return False
        try:
            if event.get("message_type") == "group":
                await self._client.send_group(event.get("group_id"), text)
            else:
                await self._client.send_private(event.get("user_id"), text)
            return True
        except Exception as exc:  # noqa: BLE001
            logger.warning("发送消息失败: %s", exc)
            return False
