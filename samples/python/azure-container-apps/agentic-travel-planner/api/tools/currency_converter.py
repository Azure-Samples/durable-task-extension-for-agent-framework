"""
Currency conversion tools for the Travel Planner agents.
"""
import json
from typing import Dict, Optional
from datetime import datetime, timedelta
from agent_framework import tool
# Cache for exchange rates to avoid repeated API calls
_exchange_rate_cache: Dict[str, dict] = {}
_cache_expiry: Optional[datetime] = None
_CACHE_DURATION = timedelta(hours=1)

# Fallback exchange rates (when API is unavailable)
_FALLBACK_RATES = {
    "USD": 1.0,
    "EUR": 0.92,
    "GBP": 0.79,
    "JPY": 149.50,
    "CAD": 1.36,
    "AUD": 1.53,
    "CHF": 0.88,
    "CNY": 7.24,
    "INR": 83.12,
    "MXN": 17.15,
    "BRL": 4.97,
    "KRW": 1320.0,
    "SGD": 1.34,
    "HKD": 7.82,
    "NOK": 10.85,
    "SEK": 10.42,
    "DKK": 6.87,
    "NZD": 1.64,
    "ZAR": 18.65,
    "THB": 35.50,
}


@tool
async def get_exchange_rate(from_currency: str, to_currency: str) -> float:
    """
    Get the exchange rate between two currencies.
    
    Args:
        from_currency: The source currency code (e.g., 'USD')
        to_currency: The target currency code (e.g., 'EUR')
    
    Returns:
        The exchange rate as a float
    """
    from_currency = from_currency.upper()
    to_currency = to_currency.upper()
    
    # Same currency
    if from_currency == to_currency:
        return 1.0
    
    # Use fallback rates
    if from_currency in _FALLBACK_RATES and to_currency in _FALLBACK_RATES:
        # Convert through USD as base
        from_rate = _FALLBACK_RATES[from_currency]
        to_rate = _FALLBACK_RATES[to_currency]
        return to_rate / from_rate
    
    # Default to 1.0 if currencies not found
    return 1.0


@tool
async def convert_currency(
    amount: float,
    from_currency: str,
    to_currency: str
) -> dict:
    """
    Convert an amount from one currency to another.
    
    Args:
        amount: The amount to convert
        from_currency: The source currency code (e.g., 'USD')
        to_currency: The target currency code (e.g., 'EUR')
    
    Returns:
        A dictionary with conversion details
    """
    rate = await get_exchange_rate(from_currency, to_currency)
    converted_amount = amount * rate
    
    return {
        "original_amount": amount,
        "original_currency": from_currency.upper(),
        "converted_amount": round(converted_amount, 2),
        "target_currency": to_currency.upper(),
        "exchange_rate": round(rate, 4),
        "timestamp": datetime.utcnow().isoformat()
    }


def format_currency(amount: float, currency: str) -> str:
    """
    Format an amount with the appropriate currency symbol.
    
    Args:
        amount: The amount to format
        currency: The currency code
    
    Returns:
        Formatted string with currency symbol
    """
    currency = currency.upper()
    symbols = {
        "USD": "$",
        "EUR": "€",
        "GBP": "£",
        "JPY": "¥",
        "CAD": "C$",
        "AUD": "A$",
        "CHF": "CHF",
        "CNY": "¥",
        "INR": "₹",
        "MXN": "MX$",
        "BRL": "R$",
        "KRW": "₩",
        "SGD": "S$",
        "HKD": "HK$",
        "NOK": "kr",
        "SEK": "kr",
        "DKK": "kr",
        "NZD": "NZ$",
        "ZAR": "R",
        "THB": "฿",
    }
    
    symbol = symbols.get(currency, currency + " ")
    
    # Japanese Yen and Korean Won don't use decimals
    if currency in ["JPY", "KRW"]:
        return f"{symbol}{int(amount):,}"
    
    return f"{symbol}{amount:,.2f}"


def get_supported_currencies() -> list:
    """
    Get a list of supported currency codes.
    
    Returns:
        List of supported currency codes
    """
    return list(_FALLBACK_RATES.keys())
