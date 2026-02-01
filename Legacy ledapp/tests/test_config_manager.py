import json
import importlib
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import core.config_manager as cm  # noqa: E402


def setup_module(module):
    # ensure module is fresh for each session
    importlib.reload(cm)


def _init_tmp(monkeypatch, tmp_path):
    monkeypatch.setattr(cm, "BASE_DIR", Path(tmp_path))
    monkeypatch.setattr(cm, "CURRENT_SETTINGS", cm.DEFAULT_SETTINGS.copy())
    monkeypatch.setattr(cm, "log_event", lambda *a, **k: None)


def test_load_settings_default(tmp_path, monkeypatch):
    _init_tmp(monkeypatch, tmp_path)
    settings = cm.load_settings()
    assert settings == cm.DEFAULT_SETTINGS


def test_set_setting_persists(tmp_path, monkeypatch):
    _init_tmp(monkeypatch, tmp_path)
    cm.set_setting("brightness_level", 50)
    settings_path = Path(tmp_path) / cm.SETTINGS_FILE
    assert settings_path.exists()
    data = json.loads(settings_path.read_text(encoding="utf-8"))
    assert data["brightness_level"] == 50
    assert cm.CURRENT_SETTINGS["brightness_level"] == 50


def test_set_setting_invalid_type(tmp_path, monkeypatch):
    _init_tmp(monkeypatch, tmp_path)
    cm.set_setting("brightness_level", "bad")
    settings_path = Path(tmp_path) / cm.SETTINGS_FILE
    assert not settings_path.exists()
    assert cm.CURRENT_SETTINGS["brightness_level"] == cm.DEFAULT_SETTINGS["brightness_level"]
