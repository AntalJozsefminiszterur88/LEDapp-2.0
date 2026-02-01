# LEDapp/core/registry_utils.py

"""Cross-platform autostart utilities.

On Windows the application is registered in the user's ``Run`` registry key.
On Linux a ``.desktop`` file is created under ``~/.config/autostart``.
"""

import sys
import os
from pathlib import Path

if sys.platform.startswith("win"):
    import winreg  # type: ignore

# Logolás (ha a reconnect_handler elérhető)
try:
    # Próbáljuk meg relatívan importálni
    from .reconnect_handler import log_event
except ImportError:
    # Vagy abszolútan, ha a core mappán kívülről hívják
    try:
        from core.reconnect_handler import log_event
    except ImportError:
        # Dummy logger végső esetben
        def log_event(msg):
            print(f"[LOG - Dummy RegistryUtils]: {msg}")


APP_NAME = "LEDApp"  # Az alkalmazás neve a rendszerben
APP_PATH = os.path.abspath(
    sys.executable if getattr(sys, "frozen", False) else sys.argv[0]
)

RUN_KEY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
AUTOSTART_DIR = Path.home() / ".config" / "autostart"
AUTOSTART_FILE = AUTOSTART_DIR / f"{APP_NAME}.desktop"


def _get_startup_command():
    """Return the command used for autostart."""
    return f'"{APP_PATH}" --tray'


if sys.platform.startswith("win"):

    def add_to_startup():
        """Register the app in the Windows registry."""
        try:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH) as key:
                command = _get_startup_command()
                winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, command)
            log_event(f"Alkalmazás hozzáadva az indítópulthoz: '{command}'")
            return True
        except OSError as e:
            log_event(f"Hiba az indítópulthoz adás során: {e}")
            return False
        except Exception as e:
            log_event(f"Váratlan hiba az indítópulthoz adás során: {e}")
            return False


    def remove_from_startup():
        """Remove the registry entry."""
        try:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_SET_VALUE
            ) as key:
                winreg.DeleteValue(key, APP_NAME)
            log_event("Alkalmazás eltávolítva az indítópultból.")
            return True
        except FileNotFoundError:
            log_event("Alkalmazás nem volt az indítópultban (nem található a kulcs).")
            return True
        except OSError as e:
            log_event(f"Hiba az indítópultból való eltávolítás során: {e}")
            return False
        except Exception as e:
            log_event(f"Váratlan hiba az indítópultból való eltávolítás során: {e}")
            return False


    def is_in_startup():
        """Return True if the registry key exists."""
        try:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_READ
            ) as key:
                winreg.QueryValueEx(key, APP_NAME)
            return True
        except FileNotFoundError:
            return False
        except OSError as e:
            log_event(f"Hiba az indítópult ellenőrzése során: {e}")
            return False
        except Exception as e:
            log_event(f"Váratlan hiba az indítópult ellenőrzése során: {e}")
            return False

else:

    def _write_desktop_file():
        content = (
            "[Desktop Entry]\n"
            "Type=Application\n"
            f"Name={APP_NAME}\n"
            f"Exec={_get_startup_command()}\n"
            "X-GNOME-Autostart-enabled=true\n"
        )
        AUTOSTART_DIR.mkdir(parents=True, exist_ok=True)
        AUTOSTART_FILE.write_text(content, encoding="utf-8")


    def add_to_startup():
        """Create a .desktop file for autostart on Linux."""
        try:
            _write_desktop_file()
            log_event(f"Autostart file created: {AUTOSTART_FILE}")
            return True
        except Exception as e:
            log_event(f"Hiba az autostart fájl létrehozásakor: {e}")
            return False


    def remove_from_startup():
        """Remove the .desktop file."""
        try:
            AUTOSTART_FILE.unlink(missing_ok=True)
            log_event("Autostart file removed.")
            return True
        except Exception as e:
            log_event(f"Hiba az autostart fájl törlésekor: {e}")
            return False


    def is_in_startup():
        """Check if the .desktop file exists."""
        return AUTOSTART_FILE.exists()


