using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TraceFox___Debugger.Models;
using TraceFox___Debugger.Extensions;

namespace TraceFox___Debugger.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DebugController : ControllerBase
    {
        private readonly IHttpContextAccessor _httpCtx;
        private readonly IWebHostEnvironment _env;
        private readonly string _pythonPath;
        private readonly string _debuggerScript;
        private const string SessionKey = "DebugSession";

        private static readonly ConcurrentDictionary<string, Process> ActiveProcesses = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> IoLocks = new();

        // ⚙️ Lazy static single-time initialization
        private static readonly Lazy<string> PythonPathLazy = new(() =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PYTHON] Searching for Python...");
            var candidates = new[] { "python", "python3" };
            foreach (var cmd in candidates)
            {
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = cmd,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    p.Start();
                    p.WaitForExit(2000);
                    if (p.ExitCode == 0)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PYTHON] Found: {cmd}");
                        return cmd;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PYTHON] Failed check for {cmd}: {ex.Message}");
                }
            }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PYTHON] Defaulting to 'python'");
            return "python";
        });

        private static Lazy<string>? DebuggerScriptLazy;

        public DebugController(IHttpContextAccessor httpCtx, IWebHostEnvironment env)
        {
            _httpCtx = httpCtx;
            _env = env;

            // Lazy init once per application lifetime
            if (DebuggerScriptLazy == null)
            {
                DebuggerScriptLazy = new Lazy<string>(() =>
                {
                    var scriptPath = Path.Combine(env.ContentRootPath, "TraceFoxPython", "debugger.py");
                    var full = Path.GetFullPath(scriptPath);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INIT] debugger.py path: {full}");
                    if (!System.IO.File.Exists(full))
                        throw new FileNotFoundException($"debugger.py not found at: {full}");
                    return full;
                });
            }

            _pythonPath = PythonPathLazy.Value;
            _debuggerScript = DebuggerScriptLazy.Value;
        }

        private DebugSession? Session => _httpCtx.HttpContext!.Session.Get<DebugSession>(SessionKey);

        private void SaveSession(DebugSession s)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SESSION] Saving SessionId={s.SessionId}, PID={s.ProcessId}, Steps={s.Steps.Count}, Running={s.IsRunning}");
            _httpCtx.HttpContext!.Session.Set(SessionKey, s);
        }

        private Process? GetProcess(string sessionId)
        {
            if (ActiveProcesses.TryGetValue(sessionId, out var p))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] Found active process for SessionId={sessionId}, PID={p.Id}");
                return p;
            }

            var sess = Session;
            if (sess?.ProcessId != null)
            {
                try
                {
                    var proc = Process.GetProcessById(sess.ProcessId.Value);
                    if (!proc.HasExited)
                    {
                        ActiveProcesses[sessionId] = proc;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] Reconnected to process PID={proc.Id} for SessionId={sessionId}");
                        return proc;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] Reconnect failed: {ex.Message}");
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] No active process for SessionId={sessionId}");
            return null;
        }

        private void SetProcess(string sessionId, Process p)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] Registering process PID={p.Id} for SessionId={sessionId}");
            ActiveProcesses[sessionId] = p;
            IoLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        }

        private void RemoveProcess(string sessionId)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [PROC] Removing process for SessionId={sessionId}");
            ActiveProcesses.TryRemove(sessionId, out _);
            if (IoLocks.TryRemove(sessionId, out var sem))
            {
                try { sem.Dispose(); } catch { }
            }
        }

        // ------------------------ START DEBUGGING ----------------------------
        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] StartRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return BadRequest(new { error = "Code is empty" });

            var sess = new DebugSession { Code = req.Code, IsRunning = false };
            SaveSession(sess);

            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{_debuggerScript}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_debuggerScript),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stderr = new StringBuilder();

            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    stderr.AppendLine(e.Data);
                    Console.WriteLine($"[PY-ERR] {e.Data}");
                }
            };

            proc.Exited += (s, e) =>
            {
                Console.WriteLine($"[PROC] Exited PID={proc.Id}");
                RemoveProcess(sess.SessionId);
            };

            if (!proc.Start())
                return StatusCode(500, new { error = "Failed to start Python process" });

            proc.BeginErrorReadLine();
            proc.StandardInput.AutoFlush = true;

            sess.ProcessId = proc.Id;
            SetProcess(sess.SessionId, proc);
            SaveSession(sess);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [START] Python PID={proc.Id} launched successfully.");

            var sem = IoLocks.GetOrAdd(sess.SessionId, _ => new SemaphoreSlim(1, 1));

            var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool ready = false;

            await sem.WaitAsync();
            try
            {
                while (!readyCts.IsCancellationRequested && !proc.HasExited)
                {
                    var line = await proc.StandardOutput.ReadLineAsync();
                    if (line == null) continue;

                    Console.WriteLine($"[READ-HANDSHAKE] {line}");

                    if (line.TrimStart().StartsWith("{") && line.Contains("\"ready\""))
                    {
                        Console.WriteLine("[READ-HANDSHAKE] Python debugger ready ✅");
                        ready = true;
                        break;
                    }
                }
            }
            finally
            {
                sem.Release();
            }

            if (!ready)
            {
                Console.WriteLine("[READ-HANDSHAKE] No 'ready' signal received. Aborting.");
                return StatusCode(500, new { error = "Python debugger failed to initialize." });
            }

            await sem.WaitAsync();
            try
            {
                await proc.StandardInput.WriteLineAsync(sess.SessionId);
                await proc.StandardInput.WriteLineAsync(req.Code);
                await proc.StandardInput.WriteLineAsync("===END_OF_CODE===");
                await proc.StandardInput.FlushAsync();
                Console.WriteLine("[START] Sent code to Python debugger.");
            }
            finally
            {
                sem.Release();
            }

            StepInfo? step = null;
            await sem.WaitAsync();
            try
            {
                step = await ReadFirstStepAsync(sess.SessionId, proc, stderr);
            }
            finally
            {
                sem.Release();
            }

            if (step == null || proc.HasExited)
            {
                Console.WriteLine("[START] No step received or process exited prematurely.");
                return StatusCode(500, new { error = "No response from debugger", details = stderr.ToString() });
            }

            sess.IsRunning = true;
            sess.Steps.Add(step);
            sess.CurrentStep = 0;
            sess.ConsoleOutput = step.Output ?? "";
            SaveSession(sess);

            Console.WriteLine($"[START] Debug session ready: SessionId={sess.SessionId}, PID={proc.Id}");

            return Ok(new { sessionId = sess.SessionId, step });
        }

        private async Task<StepInfo?> ReadFirstStepAsync(string sessionId, Process proc, StringBuilder stderr)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-FIRST] Waiting for handshake and first step...");
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            while (!cts.IsCancellationRequested && !proc.HasExited)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                Console.WriteLine($"[READ-FIRST] Line: {line}");

                if (line.Contains("\"ready\""))
                    continue;

                if (line.TrimStart().StartsWith("{"))
                {
                    if (line.Contains("\"line\""))
                    {
                        var step = JsonSerializer.Deserialize<StepInfo>(line);
                        return step;
                    }
                    else if (line.Contains("\"finished\""))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STEP] Script finished execution.");
                        return new StepInfo
                        {
                            Line = -1,
                            Filename = "<string>",
                            Vars = new Dictionary<string, string>(),
                            Output = "Execution finished."
                        };
                    }
                }

            }

            Console.WriteLine("[READ-FIRST] Timeout or process exited before first step.");
            return null;
        }

        // ------------------------ STEP COMMAND (UPDATED FINAL) ----------------------------
        [HttpPost("step")]
        public async Task<IActionResult> Step()
        {
            var sess = Session;
            if (sess == null || !sess.IsRunning)
                return BadRequest("No active debug session.");

            var proc = GetProcess(sess.SessionId);
            if (proc == null || proc.HasExited)
                return BadRequest("Python debugger process not found or has exited.");

            var sem = IoLocks.GetOrAdd(sess.SessionId, _ => new SemaphoreSlim(1, 1));
            StepInfo? step = null;

            await sem.WaitAsync();
            try
            {
                await proc.StandardInput.WriteLineAsync("step");
                await proc.StandardInput.FlushAsync();

                step = await ReadStepAsync(sess.SessionId, proc);

                if (step == null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STEP] No step response received for {sess.SessionId}.");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STEP] Received step: Line={step.Line} VarsCount={step.Vars?.Count ?? 0}");
                }
            }
            finally
            {
                sem.Release();
            }


            if (step != null)
            {
                // Clean variable values and ensure they’re JSON-safe
                if (step.Vars != null)
                {
                    var cleanVars = new Dictionary<string, string>();
                    foreach (var kv in step.Vars)
                    {
                        cleanVars[kv.Key] = kv.Value?.Length > 500 ? kv.Value.Substring(0, 500) + "..." : kv.Value;
                    }
                    step.Vars = cleanVars;
                }

                // Update session state
                sess.Steps.Add(step);
                sess.CurrentStep = sess.Steps.Count - 1;
                sess.ConsoleOutput = step.Output ?? "";
                sess.LastUpdate = DateTime.Now;
                SaveSession(sess);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STEP] Line={step.Line}, Vars={step.Vars?.Count ?? 0}");
                return Ok(step);
            }

            // Handle process end or crash
            if (proc.HasExited)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STEP] Debug process PID={proc.Id} exited unexpectedly.");
                sess.IsRunning = false;
                SaveSession(sess);
                RemoveProcess(sess.SessionId);
                return BadRequest("Process finished or crashed.");
            }

            // If still running but no valid step (e.g., timeout)
            return BadRequest("No valid step data received.");
        }


        // ------------------------ FULL RUN ----------------------------
        [HttpPost("fullrun")]
        public async Task<IActionResult> FullRun()
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [FULLRUN] Starting full run...");
            var sess = Session;
            var proc = GetProcess(sess.SessionId);

            if (string.IsNullOrEmpty(sess.SessionId) || !sess.IsRunning || proc == null)
                return BadRequest("No active session");

            var sem = IoLocks.GetOrAdd(sess.SessionId, _ => new SemaphoreSlim(1, 1));

            try
            {
                while (!proc.HasExited)
                {
                    await sem.WaitAsync();
                    try
                    {
                        await proc.StandardInput.WriteLineAsync("continue");
                        await proc.StandardInput.FlushAsync();
                        await Task.Delay(10);
                        await ReadStepAsync(sess.SessionId, proc);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }

                sess.ConsoleOutput = sess.Steps.LastOrDefault()?.Output ?? "";
                SaveSession(sess);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [FULLRUN] Completed run. Steps={sess.Steps.Count}");
                return Ok(new { finalOutput = sess.ConsoleOutput, steps = sess.Steps });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR][FULLRUN] {ex}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ------------------------ STATE ----------------------------
        [HttpGet("state")]
        public IActionResult State()
        {
            var sess = Session;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STATE] Fetching current state for SessionId={sess?.SessionId}");
            var current = sess?.CurrentStep >= 0 ? sess.Steps[sess.CurrentStep] : null;
            return Ok(new
            {
                steps = sess?.Steps,
                current,
                console = sess?.ConsoleOutput,
                isRunning = sess?.IsRunning
            });
        }

        // ------------------------ READ STEP ----------------------------
        private async Task<StepInfo?> ReadStepAsync(string sessionId, Process proc)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            while (!cts.IsCancellationRequested && !proc.HasExited)
            {
                string? line;
                try
                {
                    line = await proc.StandardOutput.ReadLineAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-STEP] ReadLineAsync failed: {ex}");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                // Log the raw JSON line for debugging (very useful)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-STEP] RAW: {line}");

                // filter out "ready" / heartbeat messages that your python may emit
                if (line.Contains("\"ready\"") || line.Contains("\"heartbeat\""))
                    continue;

                // Accept lines that look like JSON
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("{")) continue;

                try
                {
                    // First attempt: deserialize into StepInfo directly
                    var step = JsonSerializer.Deserialize<StepInfo>(line, options);
                    if (step != null)
                    {
                        // Defensive: if Vars is null, try to re-parse as generic JSON
                        if (step.Vars == null)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                if (doc.RootElement.TryGetProperty("vars", out var vElement) && vElement.ValueKind == JsonValueKind.Object)
                                {
                                    var dict = new Dictionary<string, string>();
                                    foreach (var prop in vElement.EnumerateObject())
                                    {
                                        // convert any JSON value to a string safely
                                        dict[prop.Name] = prop.Value.ValueKind switch
                                        {
                                            JsonValueKind.String => prop.Value.GetString() ?? "",
                                            JsonValueKind.Number => prop.Value.GetRawText(),
                                            JsonValueKind.True => "True",
                                            JsonValueKind.False => "False",
                                            _ => prop.Value.ToString() ?? ""
                                        };
                                    }
                                    step.Vars = dict;
                                }
                                else
                                {
                                    step.Vars = new Dictionary<string, string>();
                                }
                            }
                            catch
                            {
                                step.Vars = new Dictionary<string, string>();
                            }
                        }

                        // Normalize step.Line minimum value
                        if (step.Line <= 0) step.Line = 1;

                        return step;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-STEP] JSON parse error: {ex.Message}");
                    // fallthrough to try manual parsing below
                }

                // fallback: attempt manual parse of vars if JSON not matching StepInfo shape
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var step = new StepInfo
                    {
                        Line = root.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : 1,
                        Filename = root.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null,
                        Output = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
                        Error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                        Vars = new Dictionary<string, string>()
                    };

                    if (root.TryGetProperty("vars", out var varsEl) && varsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in varsEl.EnumerateObject())
                        {
                            step.Vars[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString() ?? "",
                                JsonValueKind.Number => prop.Value.GetRawText(),
                                JsonValueKind.True => "True",
                                JsonValueKind.False => "False",
                                _ => prop.Value.ToString() ?? ""
                            };
                        }
                    }

                    return step;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-STEP] Manual parse failed: {ex.Message}");
                    // continue loop until timeout
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [READ-STEP] Timeout or process exited while waiting for step.");
            return null;
        }

    }

    public class StartRequest
    {
        public string Code { get; set; } = "";
    }
}
