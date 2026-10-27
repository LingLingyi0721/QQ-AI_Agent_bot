"""OpenAI 兼容 chat/completions 客户端（仅 aiohttp）。"""

from __future__ import annotations

import asyncio
import json
from typing import Any, Optional

import aiohttp


class LLMError(Exception):
    """LLM 调用失败。"""


class LLMClient:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        model: str,
        temperature: float = 0.7,
        max_tokens: int = 1024,
        timeout: float = 60,
    ) -> None:
        self._base_url = (base_url or "").rstrip("/")
        self._api_key = (api_key or "").strip()
        self._model = model
        self._temperature = float(temperature)
        self._max_tokens = int(max_tokens)
        self._timeout = float(timeout)
        self._session: Optional[aiohttp.ClientSession] = None

    @property
    def endpoint(self) -> str:
        return f"{self._base_url}/chat/completions"

    async def chat(self, messages: list[dict]) -> str:
        if not self._api_key:
            raise LLMError("未配置 LLM_API_KEY，无法调用模型")
        if self._session is None or self._session.closed:
            self._session = aiohttp.ClientSession()
        payload: dict[str, Any] = {
            "model": self._model,
            "messages": messages,
            "temperature": self._temperature,
            "max_tokens": self._max_tokens,
            "stream": False,
        }
        headers = {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }
        timeout = aiohttp.ClientTimeout(total=self._timeout)
        try:
            async with self._session.post(
                self.endpoint, json=payload, headers=headers, timeout=timeout
            ) as resp:
                body = await resp.text()
                if resp.status != 200:
                    raise LLMError(f"HTTP {resp.status}: {body[:300]}")
        except asyncio.TimeoutError as exc:
            raise LLMError(f"调用模型超时（{self._timeout:.0f}s）") from exc
        except aiohttp.ClientError as exc:
            raise LLMError(f"调用模型失败: {exc}") from exc

        try:
            data = json.loads(body)
        except json.JSONDecodeError as exc:
            raise LLMError(f"响应不是合法 JSON: {body[:300]}") from exc

        try:
            content = data["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError) as exc:
            raise LLMError(f"响应结构异常: {body[:300]}") from exc
        if content is None:
            raise LLMError(f"模型返回空内容: {body[:300]}")
        return str(content).strip()

    async def close(self) -> None:
        if self._session is not None and not self._session.closed:
            await self._session.close()
        self._session = None
