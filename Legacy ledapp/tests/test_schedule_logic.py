import types
import sys
import importlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import config  # noqa: E402


def setup_pyside(monkeypatch):
    widgets = types.SimpleNamespace(
        QMessageBox=types.SimpleNamespace(
            critical=lambda *a, **k: None,
            information=lambda *a, **k: None,
        )
    )
    core = types.SimpleNamespace(Qt=object())
    pyside = types.ModuleType("PySide6")
    monkeypatch.setitem(sys.modules, "PySide6", pyside)
    monkeypatch.setitem(sys.modules, "PySide6.QtWidgets", widgets)
    monkeypatch.setitem(sys.modules, "PySide6.QtCore", core)
    # minimal pytz substitute
    tz = types.SimpleNamespace(zone="UTC")
    pytz_dummy = types.SimpleNamespace(
        timezone=lambda x: types.SimpleNamespace(zone=x),
        UnknownTimeZoneError=Exception,
        utc=tz,
    )
    monkeypatch.setitem(sys.modules, "pytz", pytz_dummy)
    monkeypatch.setitem(
        sys.modules,
        "core.sun_logic",
        types.SimpleNamespace(
            get_local_sun_info=lambda *a, **k: None,
            get_hungarian_day_name=lambda x: x,
            DAYS_HU={},
        ),
    )
    monkeypatch.setitem(
        sys.modules,
        "core.location_utils",
        types.SimpleNamespace(get_sun_times=lambda *a, **k: None),
    )


def test_get_default_schedule(monkeypatch):
    setup_pyside(monkeypatch)
    glogic = importlib.import_module("gui.gui2_schedule_logic")
    schedule = glogic.get_default_schedule()
    assert set(schedule.keys()) == set(config.DAYS)
    for day in config.DAYS:
        assert set(schedule[day].keys()) == {
            "color",
            "on_time",
            "off_time",
            "sunrise",
            "sunrise_offset",
            "sunset",
            "sunset_offset",
        }


def test_check_profile_conflicts(monkeypatch):
    setup_pyside(monkeypatch)
    glogic = importlib.import_module("gui.gui2_schedule_logic")
    default = glogic.get_default_schedule()
    prof1 = {k: v.copy() for k, v in default.items()}
    prof2 = {k: v.copy() for k, v in default.items()}
    prof1["Hétfő"].update({"on_time": "08:00", "off_time": "10:00"})
    prof2["Hétfő"].update({"on_time": "09:00", "off_time": "11:00"})
    app = types.SimpleNamespace(
        profiles={
            "P1": {"active": True, "schedule": prof1},
            "P2": {"active": True, "schedule": prof2},
        }
    )
    conflicts = glogic.check_profile_conflicts(app, "P1")
    assert "Hétfő - P2" in conflicts


def test_check_profiles_turns_off_when_after_last_interval(monkeypatch):
    setup_pyside(monkeypatch)
    glogic = importlib.import_module("gui.gui2_schedule_logic")

    class DummyTZ:
        zone = "UTC"

        def localize(self, dt):
            from datetime import timezone

            return dt.replace(tzinfo=timezone.utc)

    from datetime import datetime as dt, timezone

    fixed_now = dt(2023, 1, 2, 20, 0, tzinfo=timezone.utc)

    class FixedDateTime(dt):
        @classmethod
        def now(cls, tz=None):
            return fixed_now

    monkeypatch.setattr(glogic, "datetime", FixedDateTime)
    monkeypatch.setattr(glogic, "LOCAL_TZ", DummyTZ())
    monkeypatch.setattr(
        glogic,
        "DAYS_HU",
        {
            "Monday": "Hétfő",
            "Sunday": "Vasárnap",
        },
    )

    schedule = glogic.get_default_schedule()
    schedule["Hétfő"].update(
        {"on_time": "08:00", "off_time": "09:00", "color": glogic.COLORS[0][0]}
    )

    main_app = types.SimpleNamespace(
        profiles={"P1": {"active": True, "schedule": schedule}},
        sunrise=None,
        sunset=None,
        is_led_on=True,
        last_color_hex="dummy",
    )
    log = {}
    controls = types.SimpleNamespace(
        send_color_command=lambda *a, **k: log.setdefault("sent", True),
        turn_off_led=lambda *a, **k: log.setdefault("off", True),
    )
    widget = types.SimpleNamespace(main_app=main_app, controls_widget=controls)

    glogic.check_profiles(widget)

    assert "sent" not in log
    assert log.get("off")


def test_get_color_by_visualization(monkeypatch):
    setup_pyside(monkeypatch)
    glogic = importlib.import_module("gui.gui2_schedule_logic")

    class DummyTZ:
        zone = "UTC"

        def localize(self, dt):
            from datetime import timezone

            return dt.replace(tzinfo=timezone.utc)

    from datetime import datetime as dt, timezone

    fixed_now = dt(2023, 1, 2, 8, 30, tzinfo=timezone.utc)

    class FixedDateTime(dt):
        @classmethod
        def now(cls, tz=None):
            return fixed_now

    monkeypatch.setattr(glogic, "datetime", FixedDateTime)
    monkeypatch.setattr(glogic, "LOCAL_TZ", DummyTZ())
    monkeypatch.setattr(glogic, "DAYS_HU", {"Monday": "Hétfő"})

    schedule = glogic.get_default_schedule()
    schedule["Hétfő"].update({"on_time": "08:00", "off_time": "09:00", "color": glogic.COLORS[0][0]})

    main_app = types.SimpleNamespace(profiles={"P": {"active": True, "schedule": schedule}})

    color_hex = glogic.get_color_by_visualization(main_app)
    assert color_hex == glogic.COLORS[0][1]


def test_apply_color_from_visualization_turns_off(monkeypatch):
    setup_pyside(monkeypatch)
    glogic = importlib.import_module("gui.gui2_schedule_logic")

    class DummyTZ:
        zone = "UTC"

        def localize(self, dt):
            from datetime import timezone

            return dt.replace(tzinfo=timezone.utc)

    from datetime import datetime as dt, timezone

    fixed_now = dt(2023, 1, 2, 10, 0, tzinfo=timezone.utc)

    class FixedDateTime(dt):
        @classmethod
        def now(cls, tz=None):
            return fixed_now

    monkeypatch.setattr(glogic, "datetime", FixedDateTime)
    monkeypatch.setattr(glogic, "LOCAL_TZ", DummyTZ())
    monkeypatch.setattr(glogic, "DAYS_HU", {"Monday": "Hétfő"})

    schedule = glogic.get_default_schedule()
    schedule["Hétfő"].update({"on_time": "08:00", "off_time": "09:00", "color": glogic.COLORS[0][0]})

    main_app = types.SimpleNamespace(
        profiles={"P": {"active": True, "schedule": schedule}},
        is_led_on=True,
        last_color_hex="dummy",
    )
    log = {}
    controls = types.SimpleNamespace(
        send_color_command=lambda *a, **k: log.setdefault("sent", True),
        turn_off_led=lambda *a, **k: log.setdefault("off", True),
    )
    widget = types.SimpleNamespace(main_app=main_app, controls_widget=controls)

    glogic.apply_color_from_visualization(widget)

    assert "sent" not in log
    assert log.get("off")
