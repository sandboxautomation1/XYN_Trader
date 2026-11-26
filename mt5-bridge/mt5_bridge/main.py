#!/usr/bin/env python3

from fastapi import FastAPI, HTTPException, Query
from pydantic import BaseModel, Field
from typing import List, Optional
import uvicorn
import asyncio
import argparse
import os
import sys
import json
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation, ROUND_DOWN
import math
import pandas as pd
from importlib.metadata import version, PackageNotFoundError

# Try relative imports (package mode), fallback to path manipulation (script mode)
try:
    from .mt5_handler import MT5Handler
    from .client import BridgeClient
except ImportError:
    sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    from mt5_bridge.mt5_handler import MT5Handler
    from mt5_bridge.client import BridgeClient

app = FastAPI(title="MT5 Bridge API")
mt5_handler = MT5Handler()

def parse_datetime(val: str) -> int:
    """Parse a string as a unix timestamp or a datetime string."""
    try:
        # Try as a numeric timestamp first
        return int(float(val))
    except ValueError:
        # Try as a datetime string using pandas for flexibility
        dt = pd.to_datetime(val)
        if dt.tzinfo is None:
            # Assume UTC if no timezone is provided
            dt = dt.tz_localize('UTC')
        else:
            # Convert to UTC if a timezone is provided
            dt = dt.tz_convert('UTC')
        return int(dt.timestamp())

class Rate(BaseModel):
    time: int
    open: float
    high: float
    low: float
    close: float
    tick_volume: int
    spread: int
    real_volume: int

class Tick(BaseModel):
    time: int
    time_msc: int
    bid: float
    ask: float
    last: float
    volume: int

class HistoricalTick(BaseModel):
    """過去ティックデータのモデル（ミリ秒精度対応）"""
    time: int              # 秒単位のタイムスタンプ (UTC)
    time_msc: int          # ミリ秒単位のタイムスタンプ (UTC)
    bid: float
    ask: float
    last: float
    volume: int
    flags: int             # ティック変更フラグ (Bid/Ask/Last/Volumeの変更種別)

class BookItem(BaseModel):
    """板情報のアイテムモデル"""
    type: str              # BUY, SELL, BUY_LIMIT, SELL_LIMIT, OTHER
    price: float
    volume: float
    #volume_real: float
    volume_dbl: float

class Account(BaseModel):
    login: int
    balance: float
    equity: float
    margin: float
    margin_free: float
    margin_level: float
    leverage: int
    currency: str
    server: str

class Position(BaseModel):
    ticket: int
    symbol: str
    type: str
    volume: float
    price_open: float
    comment: str
    magic: int
    sl: float
    tp: float
    price_current: float
    profit: float
    time: int
    time_msc: int
    digits: int = 2
    point: float = 0.0
    stops_level: int = 0

class Deal(BaseModel):
    ticket: int
    order: int
    time: int
    time_msc: int
    type: str
    entry: str
    position_id: int
    volume: float
    price: float
    commission: float
    swap: float
    profit: float
    comment: str
    magic: int
    symbol: str

class SignalRiskInput(BaseModel):
    signal_id: str
    symbol: str
    direction: str
    entry: float
    stop_loss: float
    tp1: float
    tp2: float
    tp3: float

class SignalRiskRequest(BaseModel):
    risk_percent: float = Field(ge=0.01, le=100)
    pnl_lots: float = Field(default=0.01, ge=0.01, le=1000)
    signals: List[SignalRiskInput] = Field(max_length=200)

class SignalRiskResult(BaseModel):
    signal_id: str
    status: str
    message: Optional[str] = None
    volume_lots: float = 0
    target_risk_amount: float = 0
    stop_loss_pnl: Optional[float] = None
    tp1_pnl: Optional[float] = None
    tp2_pnl: Optional[float] = None
    tp3_pnl: Optional[float] = None

class SignalRiskResponse(BaseModel):
    account_balance: float
    account_equity: float
    account_currency: str
    risk_percent: float
    pnl_lots: float
    calculated_at_utc: datetime
    results: List[SignalRiskResult]

async def monitor_connection():
    """Periodically check MT5 connection and reconnect if needed."""
    while True:
        try:
            if not mt5_handler.check_connection():
                print("WARNING: MT5 connection lost. Reconnecting...")
            await asyncio.sleep(5)  # Check every 5 seconds
        except Exception as e:
            print(f"Error in connection monitor: {e}")
            await asyncio.sleep(5)

@app.on_event("startup")
async def startup_event():
    """Initialize MT5 connection on startup."""
    # Only try to initialize if we are on Windows (checked in main types, but safe here too)
    if sys.platform == "win32":
        if not mt5_handler.initialize():
            print("WARNING: Failed to initialize MT5 on startup. Will retry in background.")
        
        # Start connection monitor
        asyncio.create_task(monitor_connection())
    else:
        print("Non-Windows platform detected: MT5 connection disabled.")

@app.on_event("shutdown")
async def shutdown_event():
    """Shutdown MT5 connection."""
    mt5_handler.shutdown()

@app.get("/health")
def health_check():
    try:
        app_version = version("mt5-bridge")
    except PackageNotFoundError:
        app_version = "unknown"
    return {
        "status": "ok", 
        "mt5_connected": mt5_handler.connected,
        "version": app_version
    }

@app.get("/version")
def get_version():
    """現在稼働中の mt5-bridge パッケージのバージョンを取得します。"""
    try:
        return {"version": version("mt5-bridge")}
    except PackageNotFoundError:
        return {"version": "unknown"}

@app.get("/symbols", response_model=List[str])
def get_symbols():
    return mt5_handler.get_symbols()

@app.get("/rates/{symbol}", response_model=List[Rate])
def get_rates(
    symbol: str, 
    timeframe: str = Query(..., description="Timeframe (e.g., M1, H1)"), 
    count: int = Query(1000, description="Number of bars")
):
    rates = mt5_handler.get_rates(symbol, timeframe, count)
    if rates is None:
        raise HTTPException(status_code=500, detail=f"Failed to get rates for {symbol}")
    return rates

@app.get("/rates_range/{symbol}", response_model=List[Rate])
def get_rates_range(
    symbol: str,
    timeframe: str = Query(..., description="Timeframe (e.g., M1, H1)"),
    start: str = Query(..., description="Start timestamp or datetime string (UTC)"),
    end: str = Query(..., description="End timestamp or datetime string (UTC)")
):
    try:
        start_ts = parse_datetime(start)
        end_ts = parse_datetime(end)
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid date format: {e}")

    date_from = datetime.fromtimestamp(start_ts, tz=timezone.utc)
    date_to = datetime.fromtimestamp(end_ts, tz=timezone.utc)
    rates = mt5_handler.get_rates_range(symbol, timeframe, date_from, date_to)
    if rates is None:
        raise HTTPException(status_code=500, detail=f"Failed to get rates range for {symbol}")
    return rates

@app.get("/tick/{symbol}", response_model=Tick)
def get_tick(symbol: str):
    tick = mt5_handler.get_tick(symbol)
    if tick is None:
        raise HTTPException(status_code=500, detail=f"Failed to get tick for {symbol}")
    return tick

@app.get("/book/{symbol}", response_model=List[BookItem])
def get_book(symbol: str):
    book = mt5_handler.get_market_book(symbol)
    if book is None:
        raise HTTPException(status_code=500, detail=f"Failed to get market book for {symbol}")
    return book

@app.get("/ticks_from/{symbol}", response_model=List[HistoricalTick])
def get_ticks_from(
    symbol: str,
    start: str = Query(..., description="Start timestamp or datetime string (UTC)"),
    count: int = Query(1000, description="Number of ticks to retrieve"),
    flags: str = Query("ALL", description="Tick type: ALL, INFO (bid/ask changes), TRADE (last/volume changes)")
):
    """
    指定日時から過去ティックデータを取得する。
    
    秒スキャルピングの強化学習訓練データなどに使用。
    """
    try:
        start_ts = parse_datetime(start)
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid date format: {e}")
    
    date_from = datetime.fromtimestamp(start_ts, tz=timezone.utc)
    ticks = mt5_handler.get_ticks_from(symbol, date_from, count, flags)
    if ticks is None:
        raise HTTPException(status_code=500, detail=f"Failed to get ticks from {symbol}")
    return ticks

@app.get("/ticks_range/{symbol}", response_model=List[HistoricalTick])
def get_ticks_range(
    symbol: str,
    start: str = Query(..., description="Start timestamp or datetime string (UTC)"),
    end: str = Query(..., description="End timestamp or datetime string (UTC)"),
    flags: str = Query("ALL", description="Tick type: ALL, INFO (bid/ask changes), TRADE (last/volume changes)")
):
    """
    指定日時範囲の過去ティックデータを取得する。
    
    秒スキャルピングの強化学習訓練データなどに使用。
    注意: 大量のティックデータを取得する場合は時間がかかる可能性があります。
    """
    try:
        start_ts = parse_datetime(start)
        end_ts = parse_datetime(end)
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid date format: {e}")
    
    date_from = datetime.fromtimestamp(start_ts, tz=timezone.utc)
    date_to = datetime.fromtimestamp(end_ts, tz=timezone.utc)
    ticks = mt5_handler.get_ticks_range(symbol, date_from, date_to, flags)
    if ticks is None:
        raise HTTPException(status_code=500, detail=f"Failed to get ticks range for {symbol}")
    return ticks

@app.get("/account", response_model=Account)
def get_account():
    account = mt5_handler.get_account_info()
    if account is None:
        raise HTTPException(status_code=500, detail="Failed to get account info")
    return account

@app.post("/signal-calculator", response_model=SignalRiskResponse)
def calculate_signal_risk(request: SignalRiskRequest):
    account = mt5_handler.get_account_info()
    if account is None:
        raise HTTPException(status_code=503, detail="MT5 account data is unavailable.")

    try:
        balance = Decimal(str(account["balance"]))
        equity = Decimal(str(account["equity"]))
        risk_percent = Decimal(str(request.risk_percent))
        if (not balance.is_finite() or balance <= 0 or
                not equity.is_finite() or not risk_percent.is_finite() or
                risk_percent < Decimal("0.01") or risk_percent > Decimal("100")):
            raise InvalidOperation
        target_risk = balance * risk_percent / Decimal("100")
    except (KeyError, InvalidOperation, ValueError):
        raise HTTPException(status_code=503, detail="MT5 returned invalid account values.")

    results = []
    for signal in request.signals:
        normalized_direction = signal.direction.strip().upper()
        if normalized_direction in {"BUY", "BUY BELOW"}:
            side = "BUY"
        elif normalized_direction in {"SELL", "SELL BELOW"}:
            side = "SELL"
        else:
            side = ""

        prices = (signal.entry, signal.stop_loss, signal.tp1, signal.tp2, signal.tp3)
        if not side or not signal.symbol.strip() or not all(math.isfinite(price) and price > 0 for price in prices):
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="invalid",
                message="Символът, посоката или ценовите нива на сигнала са невалидни.",
                target_risk_amount=float(target_risk),
            ))
            continue

        correct_levels = (
            signal.stop_loss < signal.entry < signal.tp1 <= signal.tp2 <= signal.tp3
            if side == "BUY"
            else signal.stop_loss > signal.entry > signal.tp1 >= signal.tp2 >= signal.tp3
        )
        if not correct_levels:
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="invalid",
                message="Stop Loss и целите не съответстват на посоката на сигнала.",
                target_risk_amount=float(target_risk),
            ))
            continue

        symbol = signal.symbol.strip()
        pnl_values = [
            mt5_handler.calculate_order_profit(symbol, side, request.pnl_lots, signal.entry, price)
            for price in (signal.stop_loss, signal.tp1, signal.tp2, signal.tp3)
        ]
        pnl_fields = {
            field: value if value is not None and math.isfinite(value) else None
            for field, value in zip(
                ("stop_loss_pnl", "tp1_pnl", "tp2_pnl", "tp3_pnl"),
                pnl_values,
            )
        }
        pnl_available = all(value is not None for value in pnl_fields.values())

        volume_constraints = mt5_handler.get_symbol_volume_constraints(symbol)
        if volume_constraints is None:
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="unavailable",
                message=f"MT5 не върна търговските параметри за {symbol}.",
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        try:
            volume_min = Decimal(str(volume_constraints["volume_min"]))
            volume_max = Decimal(str(volume_constraints["volume_max"]))
            volume_step = Decimal(str(volume_constraints["volume_step"]))
            if not all(value.is_finite() and value > 0 for value in (volume_min, volume_max, volume_step)):
                raise InvalidOperation
        except (KeyError, InvalidOperation, ValueError):
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="unavailable",
                message=f"MT5 върна невалидна минимална стъпка за обем за {symbol}.",
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        one_lot_stop_pnl = mt5_handler.calculate_order_profit(
            symbol, side, 1.0, signal.entry, signal.stop_loss
        )
        if one_lot_stop_pnl is None or not math.isfinite(one_lot_stop_pnl):
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="unavailable",
                message=f"MT5 не успя да изчисли загубата при Stop Loss за {symbol}.",
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue
        if one_lot_stop_pnl >= 0:
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="invalid",
                message="Stop Loss не води до загуба при зададената посока.",
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        try:
            raw_volume = target_risk / abs(Decimal(str(one_lot_stop_pnl)))
            volume = (raw_volume / volume_step).to_integral_value(rounding=ROUND_DOWN) * volume_step
            max_step_volume = (volume_max / volume_step).to_integral_value(rounding=ROUND_DOWN) * volume_step
            volume = min(volume, max_step_volume)
        except (InvalidOperation, ZeroDivisionError):
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="unavailable",
                message="Обемът не може да бъде изчислен от MT5 параметрите.",
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        if not pnl_available:
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="unavailable",
                message=f"MT5 не върна пълните P/L изчисления за {symbol} при {request.pnl_lots} lot.",
                volume_lots=float(volume),
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        if volume < volume_min:
            results.append(SignalRiskResult(
                signal_id=signal.signal_id,
                status="below_minimum",
                message=f"Обемът за изпълнение според риска е под минималния за {symbol} ({volume_min} lot). Показаните P/L суми са изчислени за {request.pnl_lots} lot.",
                volume_lots=float(volume),
                target_risk_amount=float(target_risk),
                **pnl_fields,
            ))
            continue

        results.append(SignalRiskResult(
            signal_id=signal.signal_id,
            status="calculated",
            volume_lots=float(volume),
            target_risk_amount=float(target_risk),
            **pnl_fields,
        ))

    return SignalRiskResponse(
        account_balance=float(balance),
        account_equity=float(equity),
        account_currency=str(account["currency"]),
        risk_percent=float(risk_percent),
        pnl_lots=float(request.pnl_lots),
        calculated_at_utc=datetime.now(timezone.utc),
        results=results,
    )

@app.get("/positions", response_model=List[Position])
def get_positions(
    symbols: Optional[str] = Query(None, description="Comma-separated list of symbols to filter (e.g., 'XAUUSD,BTCUSD')"),
    magic: Optional[int] = Query(None, description="Magic number to filter positions by"),
):
    # symbols パラメータがあればリストに変換
    symbol_list = None
    if symbols:
        symbol_list = [s.strip() for s in symbols.split(",") if s.strip()]
        if not symbol_list:
            symbol_list = None
    
    positions = mt5_handler.get_positions(symbols=symbol_list, magic=magic)
    if positions is None:
        raise HTTPException(status_code=500, detail="Failed to get positions")
    return positions

@app.get("/history/deals", response_model=List[Deal])
def get_history_deals(
    position: Optional[int] = Query(None, description="Position ID to filter deals by"),
    ticket: Optional[int] = Query(None, description="Deal ticket to filter by"),
    start: Optional[str] = Query(None, description="Start timestamp or datetime string (UTC)"),
    end: Optional[str] = Query(None, description="End timestamp or datetime string (UTC)"),
):
    try:
        date_from = parse_datetime(start) if start else None
        date_to = parse_datetime(end) if end else None
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Invalid date format: {e}")

    dt_from = datetime.fromtimestamp(date_from, tz=timezone.utc) if date_from else None
    dt_to = datetime.fromtimestamp(date_to, tz=timezone.utc) if date_to else None

    deals = mt5_handler.get_history_deals(
        position=position,
        ticket=ticket,
        date_from=dt_from,
        date_to=dt_to
    )
    if deals is None:
        raise HTTPException(status_code=500, detail="Failed to get historical deals")
    return deals

class OrderRequest(BaseModel):
    symbol: str
    type: str # "BUY" or "SELL"
    volume: float
    sl: float = 0.0
    tp: float = 0.0
    comment: str = ""
    magic: int = 123456

class CloseRequest(BaseModel):
    ticket: int

class ModifyRequest(BaseModel):
    ticket: int
    sl: Optional[float] = None
    tp: Optional[float] = None
    update_sl: bool = False
    update_tp: bool = False

@app.post("/order")
def send_order(order: OrderRequest):
    ticket, error = mt5_handler.send_order(
        order.symbol, 
        order.type, 
        order.volume, 
        order.sl, 
        order.tp, 
        order.comment,
        magic=order.magic
    )
    if ticket is None:
        detail = error or "Failed to send order"
        raise HTTPException(status_code=500, detail=detail)
    return {"status": "ok", "ticket": ticket}

@app.post("/close")
def close_position(req: CloseRequest):
    success, message = mt5_handler.close_position(req.ticket)
    if not success:
        raise HTTPException(status_code=500, detail=f"Failed to close position: {message}")
    return {"status": "ok"}

@app.post("/modify")
def modify_position(req: ModifyRequest):
    success, message = mt5_handler.modify_position(
        req.ticket,
        req.sl,
        req.tp,
        req.update_sl,
        req.update_tp,
    )
    if not success:
        raise HTTPException(status_code=500, detail=f"Failed to modify position: {message}")
    return {"status": "ok"}

def main():
    parser = argparse.ArgumentParser(description="MT5 Bridge CLI")
    
    try:
        app_version = version("mt5-bridge")
    except PackageNotFoundError:
        app_version = "unknown"

    parser.add_argument(
        "--version",
        action="version",
        version=f"mt5-bridge version: {app_version}\nPython version: {sys.version}"
    )

    subparsers = parser.add_subparsers(dest="command", help="Command to run")

    # Serve command
    server_parser = subparsers.add_parser("server", help="Run MT5 Bridge Server (Windows Only)")
    server_parser.add_argument(
        "--host",
        default="0.0.0.0",
        help="Host interface to bind (default: 0.0.0.0)",
    )
    server_parser.add_argument(
        "--port",
        type=int,
        default=8000,
        help="Port to listen on (default: 8000)",
    )
    server_parser.add_argument("--mt5-path", default=None, help="Path to MT5 executable")
    server_parser.add_argument("--mt5-login", type=int, default=None, help="MT5 Login ID")
    server_parser.add_argument("--mt5-password", default=None, help="MT5 Password")
    server_parser.add_argument("--mt5-server", default=None, help="MT5 Server Name")
    server_parser.add_argument("--no-utc", action="store_true", help="Disable UTC conversion")

    # Client command
    client_parser = subparsers.add_parser("client", help="Run MT5 Bridge Client")
    client_parser.add_argument("--url", default="http://localhost:8000", help="Server URL")
    
    client_subs = client_parser.add_subparsers(dest="client_command", help="Client command", required=True)
    
    # Client Subcommands
    client_subs.add_parser("health", help="Check server health")
    
    client_subs.add_parser("version", help="Get server version")
    
    client_subs.add_parser("symbols", help="List all available symbols")
    
    rates_p = client_subs.add_parser("rates", help="Get historical rates")
    rates_p.add_argument("symbol", type=str)
    rates_p.add_argument("--timeframe", default="M1")
    rates_p.add_argument("--count", type=int, default=1000)
    
    # 日付範囲指定でのレート取得コマンド
    rates_range_p = client_subs.add_parser("rates_range", help="Get historical rates by date range")
    rates_range_p.add_argument("symbol", type=str)
    rates_range_p.add_argument("--timeframe", default="M1", help="Timeframe (e.g. M1, H1)")
    rates_range_p.add_argument("--start", type=str, required=True, help="Start timestamp or datetime string (e.g. 2025-01-01)")
    rates_range_p.add_argument("--end", type=str, required=True, help="End timestamp or datetime string (e.g. 2025-01-01 12:00)")

    tick_p = client_subs.add_parser("tick", help="Get latest tick")
    tick_p.add_argument("symbol", type=str)
    
    client_subs.add_parser("account", help="Get account information")

    positions_p = client_subs.add_parser("positions", help="Get open positions")
    positions_p.add_argument("--symbols", help="Comma-separated list of symbols (e.g. BTCUSD,ETHUSD)")
    positions_p.add_argument("--magic", type=int, help="Magic number filter")

    # Order command
    order_p = client_subs.add_parser("order", help="Send a market order")
    order_p.add_argument("symbol", type=str)
    order_p.add_argument("type", type=str, choices=["BUY", "SELL"])
    order_p.add_argument("volume", type=float)
    order_p.add_argument("--sl", type=float, default=0.0)
    order_p.add_argument("--tp", type=float, default=0.0)
    order_p.add_argument("--comment", type=str, default="")
    order_p.add_argument("--magic", type=int, default=123456)

    # Close command
    close_p = client_subs.add_parser("close", help="Close a position")
    close_p.add_argument("ticket", type=int)

    # Modify command
    modify_p = client_subs.add_parser("modify", help="Modify position SL/TP")
    modify_p.add_argument("ticket", type=int)
    modify_p.add_argument("--sl", type=float, default=None)
    modify_p.add_argument("--tp", type=float, default=None)

    # Tick data commands (for tick scalping / high-frequency trading research)
    ticks_from_p = client_subs.add_parser("ticks_from", help="Get historical ticks from a specific date")
    ticks_from_p.add_argument("symbol", type=str)
    ticks_from_p.add_argument("--start", type=str, required=True, help="Start timestamp or datetime string (e.g. 2025-01-01)")
    ticks_from_p.add_argument("--count", type=int, default=1000, help="Number of ticks to retrieve")
    ticks_from_p.add_argument("--flags", type=str, default="ALL", choices=["ALL", "INFO", "TRADE"], 
                               help="Tick type: ALL, INFO (bid/ask), TRADE (last/volume)")
    
    ticks_range_p = client_subs.add_parser("ticks_range", help="Get historical ticks within a date range")
    ticks_range_p.add_argument("symbol", type=str)
    ticks_range_p.add_argument("--start", type=str, required=True, help="Start timestamp or datetime string")
    ticks_range_p.add_argument("--end", type=str, required=True, help="End timestamp or datetime string")
    ticks_range_p.add_argument("--flags", type=str, default="ALL", choices=["ALL", "INFO", "TRADE"],
                                help="Tick type: ALL, INFO (bid/ask), TRADE (last/volume)")

    # Market Book (Level 2) command
    book_p = client_subs.add_parser("book", help="Get current market depth (Level 2)")
    book_p.add_argument("symbol", type=str)

    # History deals command
    history_deals_p = client_subs.add_parser("history_deals", help="Get historical deals (completed transactions)")
    history_deals_p.add_argument("--position", type=int, help="Filter by Position ID")
    history_deals_p.add_argument("--ticket", type=int, help="Filter by Deal Ticket ID")
    history_deals_p.add_argument("--start", type=str, help="Start timestamp or datetime string (UTC)")
    history_deals_p.add_argument("--end", type=str, help="End timestamp or datetime string (UTC)")

    args = parser.parse_args()

    if args.command == "server":
        if sys.platform != "win32":
            print("Error: Server functionality is only supported on Windows.")
            sys.exit(1)

        # Configure MT5 handler with CLI args
        if args.mt5_path:
            mt5_handler.program_path = args.mt5_path
        if args.mt5_login:
            mt5_handler.login = args.mt5_login
        if args.mt5_password:
            mt5_handler.password = args.mt5_password
        if args.mt5_server:
            mt5_handler.server = args.mt5_server
        
        # Configure UTC conversion
        mt5_handler.use_utc = not args.no_utc
        if mt5_handler.use_utc:
            print("UTC conversion enabled (Server Time -> UTC)")
        else:
            print("UTC conversion disabled (Raw Server Time)")

        # Run Server
        uvicorn.run(app, host=args.host, port=args.port, timeout_keep_alive=60)

    elif args.command == "client":
        client = BridgeClient(base_url=args.url)
        if args.client_command == "health":
            print(json.dumps(client.check_health(), indent=2))
        elif args.client_command == "version":
            print(json.dumps(client.get_version(), indent=2))
        elif args.client_command == "symbols":
            print(json.dumps(client.get_symbols(), indent=2))
        elif args.client_command == "rates":
            print(json.dumps(client.get_rates(args.symbol, args.timeframe, args.count), indent=2))
        elif args.client_command == "rates_range":
            try:
                start_ts = parse_datetime(args.start)
                end_ts = parse_datetime(args.end)
                print(json.dumps(client.get_rates_range(args.symbol, args.timeframe, start_ts, end_ts), indent=2))
            except Exception as e:
                print(f"Error parsing date: {e}")
                sys.exit(1)
        elif args.client_command == "tick":
            print(json.dumps(client.get_tick(args.symbol), indent=2))
        elif args.client_command == "account":
            print(json.dumps(client.get_account_info(), indent=2))
        elif args.client_command == "positions":
            symbols = args.symbols.split(",") if args.symbols else None
            print(json.dumps(client.get_positions(symbols=symbols, magic=args.magic), indent=2))
        elif args.client_command == "order":
            print(json.dumps(client.send_order(
                args.symbol, args.type, args.volume, args.sl, args.tp, args.comment, args.magic
            ), indent=2))
        elif args.client_command == "close":
            print(json.dumps(client.close_position(args.ticket), indent=2))
        elif args.client_command == "modify":
            print(json.dumps(client.modify_position(args.ticket, args.sl, args.tp), indent=2))
        elif args.client_command == "ticks_from":
            try:
                start_ts = parse_datetime(args.start)
                result = client.get_ticks_from(args.symbol, start_ts, args.count, args.flags)
                print(f"Retrieved {len(result)} ticks")
                print(json.dumps(result[:10] if len(result) > 10 else result, indent=2))
                if len(result) > 10:
                    print(f"... and {len(result) - 10} more ticks")
            except Exception as e:
                print(f"Error: {e}")
                sys.exit(1)
        elif args.client_command == "ticks_range":
            try:
                start_ts = parse_datetime(args.start)
                end_ts = parse_datetime(args.end)
                result = client.get_ticks_range(args.symbol, start_ts, end_ts, args.flags)
                print(f"Retrieved {len(result)} ticks")
                print(json.dumps(result[:10] if len(result) > 10 else result, indent=2))
                if len(result) > 10:
                    print(f"... and {len(result) - 10} more ticks")
            except Exception as e:
                print(f"Error: {e}")
                sys.exit(1)
        elif args.client_command == "book":
            print(json.dumps(client.get_book(args.symbol), indent=2))
        elif args.client_command == "history_deals":
            try:
                start_ts = parse_datetime(args.start) if args.start else None
                end_ts = parse_datetime(args.end) if args.end else None
                print(json.dumps(client.get_history_deals(
                    position=args.position,
                    ticket=args.ticket,
                    start=start_ts,
                    end=end_ts
                ), indent=2))
            except Exception as e:
                print(f"Error: {e}")
                sys.exit(1)
    else:
        parser.print_help()

if __name__ == "__main__":
    main()
