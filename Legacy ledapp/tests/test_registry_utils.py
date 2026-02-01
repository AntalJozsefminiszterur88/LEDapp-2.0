import importlib
import sys
from pathlib import Path


def test_linux_autostart(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    monkeypatch.setattr(sys, "platform", "linux")
    if "core.registry_utils" in sys.modules:
        del sys.modules["core.registry_utils"]
    ru = importlib.import_module("core.registry_utils")

    desktop_file = tmp_path / ".config" / "autostart" / "LEDApp.desktop"
    if desktop_file.exists():
        desktop_file.unlink()

    assert ru.add_to_startup() is True
    assert desktop_file.exists()
    assert ru.is_in_startup() is True
    assert ru.remove_from_startup() is True
    assert not desktop_file.exists()
