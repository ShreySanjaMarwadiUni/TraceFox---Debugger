# debugger.py (FINAL VERSION - READY TO USE)
import sys
import json
import traceback
from types import FrameType
from bdb import Bdb
from io import StringIO
import datetime
import os
import __main__

DEBUGGER_FILE = getattr(__main__, "__file__", None)
LOG_FILE = "debugger_log.txt"


# --- Logger ---
def log_event(msg: str):
    """Logs to stderr and to a local file for inspection."""
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    try:
        sys.stderr.write(f"[{ts}] {msg}\n")
        sys.stderr.flush()
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(f"[{ts}] {msg}\n")
    except Exception:
        pass


# --- TraceFox Debugger ---
class TraceFoxDebugger(Bdb):
    def __init__(self, max_steps=10000, max_output_chars=20000):
        super().__init__()
        self.output = StringIO()
        self.done = False
        self.steps_count = 0
        self.max_steps = max_steps
        self.max_output_chars = max_output_chars
        log_event("TraceFoxDebugger initialized.")

    def _safe_repr_locals(self, frame: FrameType):
        """Safely capture variable names and stringified values."""
        try:
            return {
                k: repr(v) for k, v in frame.f_locals.items()
                if not k.startswith("__") and not callable(v)
            }
        except Exception as e:
            log_event(f"Error capturing locals: {e}")
            return {}

    def user_line(self, frame: FrameType):
        """Triggered on every line execution within user code."""
        filename = getattr(frame.f_code, "co_filename", None)
        if filename != "<string>" or self.done:
            return

        if self.steps_count >= self.max_steps:
            log_event("Reached max_steps; stopping trace.")
            self.done = True
            self.set_quit()
            sys.__stdout__.write(json.dumps({"info": "max_steps_reached"}) + "\n")
            sys.__stdout__.flush()
            return

        try:
            vars_state = self._safe_repr_locals(frame)
            step_info = {
                "line": frame.f_lineno,
                "filename": "<string>",
                "vars": vars_state,
                "output": self.output.getvalue()
            }

            sys.__stdout__.write(json.dumps(step_info) + "\n")
            sys.__stdout__.flush()
            log_event(f"Emitted step at line={frame.f_lineno}")

            self.steps_count += 1

            # Trim output buffer if too large
            if self.output.tell() > self.max_output_chars:
                log_event("Trimming oversized output buffer.")
                content = self.output.getvalue()[-self.max_output_chars:]
                self.output = StringIO()
                self.output.write(content)

            # Wait for command from controller
            cmd = sys.stdin.readline().strip()
            if not cmd:
                log_event("No command received; exiting debugger.")
                self.done = True
                self.set_quit()
                return

            log_event(f"Received command: {cmd}")
            cmd = cmd.lower()

            if cmd == "stop":
                self.done = True
                self.set_quit()
            elif cmd in ("step", "next"):
                self.set_step()
            elif cmd == "continue":
                self.set_continue()
            else:
                log_event(f"Unknown command '{cmd}', defaulting to step.")
                self.set_step()

        except Exception as e:
            trace = traceback.format_exc()
            log_event(f"Error in user_line: {e}\n{trace}")
            sys.__stdout__.write(json.dumps({"error": str(e), "trace": trace}) + "\n")
            sys.__stdout__.flush()
            self.done = True
            self.set_quit()

    def run_code(self, code: str):
        """Run code under debugging with output and error capture."""
        old_out, old_err = sys.stdout, sys.stderr
        sys.stdout = sys.stderr = self.output
        log_event("Debugger run_code() started.")

        try:
            compiled = compile(code, "<string>", "exec")
            log_event("Code compiled successfully.")
            self.run(compiled)
            log_event("Execution finished normally.")

            # Final output to parent
            final = {"finished": True, "output": self.output.getvalue()}
            sys.__stdout__.write(json.dumps(final) + "\n")
            sys.__stdout__.flush()
            log_event("Sent final output to controller.")

            # Keep alive for post-run commands
            while True:
                cmd = sys.stdin.readline()
                if not cmd:
                    log_event("Post-run EOF; exiting.")
                    break
                cmd = cmd.strip().lower()
                if cmd == "exit":
                    log_event("Exit command received.")
                    break
                elif cmd == "restart":
                    sys.__stdout__.write(json.dumps({"waiting_for_new_code": True}) + "\n")
                    sys.__stdout__.flush()
                    break
                else:
                    sys.__stdout__.write(json.dumps({"heartbeat": True, "cmd": cmd}) + "\n")
                    sys.__stdout__.flush()

        except SyntaxError:
            tb = traceback.format_exc()
            sys.__stdout__.write(json.dumps({"error": "syntax", "trace": tb}) + "\n")
            sys.__stdout__.flush()
            log_event("SyntaxError during compile or run.")
        except Exception:
            tb = traceback.format_exc()
            sys.__stdout__.write(json.dumps({"error": "exception", "trace": tb}) + "\n")
            sys.__stdout__.flush()
            log_event(f"Runtime exception: {tb}")
        finally:
            sys.stdout, sys.stderr = old_out, old_err
            log_event("Restored stdout/stderr and ended run_code().")


# --- Main Entry Point ---
def main():
    try:
        sys.__stdout__.write(json.dumps({"ready": True}) + "\n")
        sys.__stdout__.flush()
        log_event("Debugger ready handshake sent.")

        session_id = sys.stdin.readline().strip()
        if not session_id:
            log_event("No session id; exiting.")
            return
        log_event(f"Session ID received: {session_id}")

        code_lines = []
        while True:
            line = sys.stdin.readline()
            if not line:
                break
            if line.strip() == "===END_OF_CODE===":
                break
            code_lines.append(line)
        code = "".join(code_lines)
        log_event(f"User code loaded ({len(code_lines)} lines).")

        TraceFoxDebugger().run_code(code)
        log_event("Debugger session completed.")

    except Exception as e:
        tb = traceback.format_exc()
        sys.__stdout__.write(json.dumps({"error": "fatal", "msg": str(e), "trace": tb}) + "\n")
        sys.__stdout__.flush()
        log_event(f"Fatal error: {e}\n{tb}")


if __name__ == "__main__":
    main()
