# LEDapp/gui/async_helper.py (Végleges, javított)

import asyncio
import threading
import traceback
from concurrent.futures import Future

from PySide6.QtCore import Signal

# Logolás importálása
try:
    from core.reconnect_handler import log_event
except ImportError:
    try:
        from ..core.reconnect_handler import log_event
    except ImportError:
        print("HIBA: Nem sikerült importálni a log_eventet az async_helper.py-ban.")

        # Definiáljunk egy dummy loggert, hogy ne álljon le a program
        def log_event(msg):
            print(f"[LOG - Dummy AsyncHelper]: {msg}")


class AsyncHelper:
    """Segédosztály az aszinkron műveletek kezelésére."""

    def __init__(self, app_instance):
        """
        Inicializálás.

        Args:
            app_instance: A fő LEDApp_BaseWindow példány.
        """
        self.app = app_instance  # Referencia a fő alkalmazásra
        self.loop = asyncio.new_event_loop()
        self.event_loop_thread = threading.Thread(target=self._run_dedicated_asyncio_loop, daemon=True)
        self.event_loop_thread.start()

    def _run_dedicated_asyncio_loop(self):
        """A dedikált szálon futó asyncio eseményhurok."""
        log_event("Asyncio event loop thread started.")
        asyncio.set_event_loop(self.loop)
        try:
            self.loop.run_forever()
        finally:
            log_event("Asyncio event loop finishing...")
            try:
                # Leállítjuk a futó taskokat (kivéve a leállító taskot, ha van)
                all_tasks = asyncio.all_tasks(self.loop)
                if all_tasks:
                    # A current_task csak futó loopban lekérdezhető biztonságosan
                    current_task = asyncio.current_task(self.loop) if self.loop.is_running() else None
                    tasks_to_cancel = {task for task in all_tasks if task is not current_task}
                    if tasks_to_cancel:
                        log_event(f"Cancelling {len(tasks_to_cancel)} asyncio tasks...")
                        for task in tasks_to_cancel:
                            task.cancel()
                        # Várakozás a task-okra (hiba esetén is folytatódik)
                        # Timeout hozzáadása a végtelen várakozás elkerülésére
                        gather_future = asyncio.gather(*tasks_to_cancel, return_exceptions=True)
                        try:
                            if self.loop.is_running():
                                self.loop.run_until_complete(asyncio.wait_for(gather_future, timeout=2.0))
                        except asyncio.TimeoutError:
                            log_event("Timeout during asyncio task cancellation.")
                        except RuntimeError as e:
                            log_event(f"RuntimeError during asyncio task gather: {e}")

            except RuntimeError as e:
                log_event(f"RuntimeError during asyncio task cleanup: {e}")
            except Exception as e:
                log_event(f"Error during asyncio task cleanup: {e}")

            # Loop bezárása, ha még futott (a stop után)
            if self.loop.is_running():
                try:
                    # Rövid várakozás a stop hatására, ha még nem zárt be
                    self.loop.run_until_complete(asyncio.sleep(0.1))
                except RuntimeError as e:
                    # Előfordulhat, hogy a loop már bezárult a run_until_complete hívása előtt
                    log_event(f"RuntimeError during final sleep before loop close: {e}")
            log_event("Closing asyncio event loop.")
            self.loop.close()
            log_event("Asyncio event loop thread finished.")

    def run_async_task(self, coro, callback_success_signal=None, callback_error_signal=None):
        """
        Futtat egy coroutine-t és signalokat bocsát ki az eredménnyel/hibával.

        Args:
            coro: A futtatandó asyncio coroutine.
            callback_success_signal: A sikeres végrehajtáskor kibocsátandó Signal objektum.
            callback_error_signal: Hiba esetén kibocsátandó Signal objektum.

        Returns:
            A Future objektum, vagy None, ha a hurok nem fut.
        """
        if not self.loop.is_running():
            error_msg = "Hiba: Az asyncio eseményhurok nem fut."
            log_event(error_msg)
            if callback_error_signal and isinstance(callback_error_signal, Signal):
                callback_error_signal.emit(error_msg)
            else:
                log_event(f"HIBA: Nem található vagy nem Signal a megadott error callback: {callback_error_signal}")
            return None

        future: Future = asyncio.run_coroutine_threadsafe(coro, self.loop)

        def done_callback(f):
            try:
                result = f.result()
                log_event(f"AsyncHelper: Task successful. Result type: {type(result)}, Value: {result}")
                if callback_success_signal and isinstance(callback_success_signal, Signal):
                    # Név logolása nélkül
                    log_event(f"AsyncHelper: Emitting success signal with data type {type(result)}...")
                    callback_success_signal.emit(result)
                # else: # Ezt a logot kikommentezhetjük, ha zavaró
                #    log_event(f"Figyelmeztetés: Nincs vagy nem Signal a megadott success callback: {callback_success_signal}")

            except Exception as e:
                if isinstance(e, asyncio.CancelledError):
                    log_event("Asyncio task cancelled.")
                    return

                bleak_error_msg = ""
                if hasattr(e, "dbus_error"):
                    bleak_error_msg = f" (DBus Error: {getattr(e, 'dbus_error_details', '')})"
                elif hasattr(e, "winrt_error"):
                    bleak_error_msg = f" (WinRT Error: {e.winrt_error})"
                error_message = f"{type(e).__name__}: {e}{bleak_error_msg}"
                log_event(f"AsyncHelper: Task failed. Error: {error_message}")
                log_event(f"Traceback:\n{traceback.format_exc()}")  # Hibakereséshez adjuk hozzá a tracebacket
                if callback_error_signal and isinstance(callback_error_signal, Signal):
                    # Név logolása nélkül
                    log_event(f"AsyncHelper: Emitting error signal...")
                    callback_error_signal.emit(error_message)
                # else: # Ezt a logot kikommentezhetjük, ha zavaró
                #    log_event(f"HIBA: Nem található vagy nem Signal a megadott error callback: {callback_error_signal}")

        future.add_done_callback(done_callback)
        return future

    def stop_loop(self):
        """Leállítja az asyncio eseményhurkot."""
        if self.loop.is_running():
            log_event("Stopping asyncio loop (requested)...")
            self.loop.call_soon_threadsafe(self.loop.stop)
