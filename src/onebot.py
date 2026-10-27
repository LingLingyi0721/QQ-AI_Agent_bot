"""OneBot11 正向 WebSocket 客户端：收事件、调 action、断线重连。"""

from __future__ import annotations

import asyncio
import json
import logging
import uuid
from typing import Any, Awaitable, Callable, Optional

import aiohttp

logger = logging.getLogger(__name__)

EventCallback = Callable[[dict], Awaitable[None]]


class OneBotError(Exception):
    """OneBot11 调用失败（未连接 / 超时 / retcode 非 0）。"""


class OneBotClient:
    def __init__(
        self,
        ws_url: str,
        access_token: str = "",
        reconnect_initial: float = 3,
        reconnect_max: float = 30,
        on_event: Optional[EventCallback] = None,
        action_timeout: float = 15,
    ) -> None:
        self._ws_url = ws_url
        self._access_token = (access_token or "").strip()
        self._reconnect_initial = max(1.0, float(reconnect_initial or 3))
        self._reconnect_max = max(self._reconnect_initial, float(reconnect_max or 30))
        self._action_timeout = float(action_timeout or 15)
        self.on_event = on_event

        self.self_id: Any = None
        self._session: Optional[aiohttp.ClientSession] = None
        self._ws: Optional[aiohttp.ClientWebSocketResponse] = None
        self._pending: dict[str, asyncio.Future] = {}
        self._send_lock = asyncio.Lock()
        self._runner: Optional[asyncio.Task] = None
        self._event_tasks: set[asyncio.Task] = set()
        self._closed = False

    # ---------- 连接 / 重连 ----------

    @property
    def connected(self) -> bool:
        return self._ws is not None and not self._ws.closed

    def _headers(self) -> dict[str, str]:
        headers = {"User-Agent": "QQ-AI-Agent/0.1"}
        if self._access_token:
            headers["Authorization"] = f"Bearer {self._access_token}"
        return headers

    async def connect(self) -> None:
        """建立一次连接并阻塞收包，直到连接断开后返回。"""
        if self._session is None or self._session.closed:
            self._session = aiohttp.ClientSession()
        logger.info("连接 OneBot11 正向 WS: %s", self._ws_url)
        ws = await self._session.ws_connect(
            self._ws_url,
            headers=self._headers(),
            heartbeat=30,
            max_msg_size=0,
        )
        self._ws = ws
        logger.info("OneBot11 WS 已连接")
        try:
            async for msg in ws:
                if msg.type == aiohttp.WSMsgType.TEXT:
                    self._on_text(msg.data)
                elif msg.type == aiohttp.WSMsgType.BINARY:
                    try:
                        self._on_text(msg.data.decode("utf-8", "replace"))
                    except Exception as exc:  # noqa: BLE001
                        logger.warning("二进制消息解码失败: %s", exc)
                elif msg.type == aiohttp.WSMsgType.ERROR:
                    logger.warning("WS 异常: %s", ws.exception())
                    break
        finally:
            self._ws = None
            self._fail_pending(OneBotError("OneBot11 连接已断开"))
            try:
                await ws.close()
            except Exception:  # noqa: BLE001
                pass
            logger.warning("OneBot11 WS 已断开")

    async def start(self) -> asyncio.Task:
        """启动后台重连循环，立即返回任务句柄。"""
        self._closed = False
        self._runner = asyncio.create_task(self._run_forever(), name="onebot-run")
        return self._runner

    async def _run_forever(self) -> None:
        delay = self._reconnect_initial
        while not self._closed:
            try:
                await self.connect()
                delay = self._reconnect_initial
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001
                logger.warning("OneBot11 连接失败: %s", exc)
            if self._closed:
                break
            logger.warning("%.1f 秒后重连 OneBot11", delay)
            try:
                await asyncio.sleep(delay)
            except asyncio.CancelledError:
                raise
            delay = min(delay * 2, self._reconnect_max)

    async def stop(self) -> None:
        self._closed = True
        if self._ws is not None:
            try:
                await self._ws.close()
            except Exception:  # noqa: BLE001
                pass
        if self._runner is not None:
            self._runner.cancel()
            try:
                await self._runner
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass
            self._runner = None
        for task in list(self._event_tasks):
            task.cancel()
        self._event_tasks.clear()
        self._fail_pending(OneBotError("客户端已停止"))
        if self._session is not None and not self._session.closed:
            await self._session.close()
        self._session = None

    # ---------- 收包 ----------

    def _on_text(self, raw: str) -> None:
        try:
            data = json.loads(raw)
        except (json.JSONDecodeError, TypeError):
            logger.warning("收到非 JSON 数据: %.200s", raw)
            return
        if not isinstance(data, dict):
            return

        echo = data.get("echo")
        if echo is not None:
            fut = self._pending.pop(str(echo), None)
            if fut is not None and not fut.done():
                fut.set_result(data)
            return

        if "post_type" not in data:
            return
        if data.get("post_type") == "meta_event" and data.get("meta_event_type") == "lifecycle":
            self.self_id = data.get("self_id")
            logger.info("OneBot11 生命周期: %s self_id=%s", data.get("sub_type"), self.self_id)
        self._dispatch(data)

    def _dispatch(self, event: dict) -> None:
        if self.on_event is None:
            return
        task = asyncio.create_task(self._safe_event(event), name="onebot-event")
        self._event_tasks.add(task)
        task.add_done_callback(self._event_tasks.discard)

    async def _safe_event(self, event: dict) -> None:
        try:
            await self.on_event(event)  # type: ignore[misc]
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001
            logger.exception("处理事件时出错")

    def _fail_pending(self, exc: BaseException) -> None:
        for echo in list(self._pending):
            fut = self._pending.pop(echo)
            if not fut.done():
                fut.set_exception(exc)

    # ---------- 调用 action ----------

    async def call_action(self, action: str, **params: Any) -> dict:
        if self._ws is None or self._ws.closed:
            raise OneBotError("OneBot11 未连接，无法调用 " + action)
        echo = uuid.uuid4().hex
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[echo] = fut
        payload = {"action": action, "params": params, "echo": echo}
        try:
            async with self._send_lock:
                ws = self._ws
                if ws is None or ws.closed:
                    raise OneBotError("OneBot11 未连接，无法调用 " + action)
                await ws.send_str(json.dumps(payload, ensure_ascii=False))
            try:
                resp = await asyncio.wait_for(fut, timeout=self._action_timeout)
            except asyncio.TimeoutError:
                raise OneBotError(f"调用 {action} 超时（{self._action_timeout:.0f}s）") from None
        finally:
            self._pending.pop(echo, None)

        retcode = resp.get("retcode") if isinstance(resp, dict) else None
        if retcode not in (0, None):
            detail = resp.get("wording") or resp.get("msg") or ""
            raise OneBotError(f"调用 {action} 失败 retcode={retcode} {detail}")
        return resp

    async def send_private(self, user_id: Any, text: str) -> dict:
        return await self.call_action(
            "send_msg", message_type="private", user_id=user_id, message=text
        )

    async def send_group(self, group_id: Any, text: str) -> dict:
        return await self.call_action(
            "send_msg", message_type="group", group_id=group_id, message=text
        )
