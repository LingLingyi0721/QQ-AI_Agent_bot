"""对话上下文存储：按 key 保存最近若干轮消息，JSON 落盘 + TTL。"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


class ContextStore:
    def __init__(
        self,
        path: str | Path,
        history_turns: int = 8,
        ttl_minutes: float = 60,
    ) -> None:
        self._path = Path(path)
        self._max_messages = max(1, int(history_turns)) * 2
        self._ttl_seconds = max(0.0, float(ttl_minutes)) * 60
        self._data: dict[str, dict[str, Any]] = {}
        self._lock = asyncio.Lock()
        self._load()

    # ---------- 对外接口 ----------

    async def get(self, key: str) -> list[dict[str, str]]:
        async with self._lock:
            entry = self._data.get(key)
            if entry is None:
                return []
            if self._expired(entry):
                self._data.pop(key, None)
                return []
            return [dict(msg) for msg in entry["messages"]]

    async def append(self, key: str, role: str, content: str) -> None:
        async with self._lock:
            now = time.time()
            entry = self._data.get(key)
            if entry is None or self._expired(entry):
                entry = {"updated": now, "messages": []}
                self._data[key] = entry
            messages = entry["messages"]
            messages.append({"role": str(role), "content": str(content)})
            if len(messages) > self._max_messages:
                entry["messages"] = messages[-self._max_messages :]
            entry["updated"] = now
            self._save_locked()

    async def clear(self, key: str) -> bool:
        async with self._lock:
            existed = self._data.pop(key, None) is not None
            if existed:
                self._save_locked()
            return existed

    async def keys(self) -> list[str]:
        async with self._lock:
            return [key for key, entry in self._data.items() if not self._expired(entry)]

    # ---------- 内部实现 ----------

    def _expired(self, entry: dict) -> bool:
        if self._ttl_seconds <= 0:
            return False
        return (time.time() - float(entry.get("updated", 0))) > self._ttl_seconds

    def _load(self) -> None:
        if not self._path.is_file():
            return
        try:
            raw = json.loads(self._path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            logger.warning("上下文文件不可读，已忽略 %s: %s", self._path, exc)
            return
        contexts = raw.get("contexts") if isinstance(raw, dict) else None
        if not isinstance(contexts, dict):
            logger.warning("上下文文件结构异常，已忽略 %s", self._path)
            return
        now = time.time()
        for key, entry in contexts.items():
            if not isinstance(entry, dict):
                continue
            messages = [
                {"role": str(m["role"]), "content": str(m["content"])}
                for m in entry.get("messages", [])
                if isinstance(m, dict) and "role" in m and "content" in m
            ]
            if not messages:
                continue
            self._data[str(key)] = {
                "updated": float(entry.get("updated") or now),
                "messages": messages[-self._max_messages :],
            }
        logger.info("已加载 %d 个上下文会话", len(self._data))

    def _save_locked(self) -> None:
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            payload = {"version": 1, "contexts": self._data}
            tmp = self._path.with_name(self._path.name + ".tmp")
            tmp.write_text(
                json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
            )
            os.replace(tmp, self._path)
        except OSError as exc:
            logger.warning("上下文落盘失败 %s: %s", self._path, exc)
