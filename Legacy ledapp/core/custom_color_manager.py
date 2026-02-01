"""Manage custom color definitions for the LED application."""

import json

from config import CUSTOM_COLORS_FILE, CUSTOM_COLORS, COLORS

# Logolás importálása
try:
    from .reconnect_handler import log_event
except ImportError:
    try:
        from core.reconnect_handler import log_event
    except Exception:

        def log_event(msg):
            """Fallback logger if imports fail."""
            print(f"[LOG - Dummy CustomColorManager]: {msg}")


def save_custom_colors_list():
    """Persist the current list of custom colors to disk."""
    try:
        with open(CUSTOM_COLORS_FILE, "w", encoding="utf-8") as f:
            json.dump(CUSTOM_COLORS, f, ensure_ascii=False, indent=4)
    except Exception as e:
        log_event(f"Hiba a színek mentésekor: {e}")
        raise


def add_custom_color(name: str, hex_code: str):
    """Add a new custom color and persist it."""
    hex_code = hex_code.lstrip("#")
    entry = {"name": name, "hex": f"#{hex_code}"}
    CUSTOM_COLORS.append(entry)
    COLORS.append((name, f"#{hex_code}", f"7e000503{hex_code}00ef"))
    save_custom_colors_list()


def delete_custom_color(name: str):
    """Delete a custom color by name and persist the list."""
    CUSTOM_COLORS[:] = [c for c in CUSTOM_COLORS if c.get("name") != name]
    COLORS[:] = [c for c in COLORS if c[0] != name]
    save_custom_colors_list()
