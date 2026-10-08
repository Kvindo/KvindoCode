using System.Diagnostics;
using System.Text;

namespace KvindoCode.Core.Tasks;

public enum TaskState { Running, Exited, Killed, Failed }

public sealed class BackgroundTask
{
    public int Id { get; init; }
    public string Kind { get; init; } = "bash";            // bash | monitor | prompt
    public string Command { get; init; } = "";
    public string Description { get; init; } = "";
    public bool WakeOnOutput { get; init; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Ended { get; internal set; }
    public TaskState State { get; internal set; } = TaskState.Running;
    public int? ExitCode { get; internal set; }
    public int? Pid { get; internal set; }

    internal Process? Proc;
    readonly StringBuilder _out = new();
    readonly object _gate = new();
    internal int LineCount;
    const int Cap = 200_000;

    internal void AppendLine(string line)
    {
        lock (_gate)
        {
            _out.Append(line).Append('\n'); LineCount++;
            if (_out.Length > Cap) _out.Remove(0, _out.Length - Cap * 3 / 4);
        }
    }

    public string Tail(int lines)
    {
        lock (_gate)
        {
            var all = _out.ToString().TrimEnd('\n').Split('\n');
            if (all.Length == 1 && all[0].Length == 0) return "";
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
    }

    public bool Running => State == TaskState.Running;
    public TimeSpan Elapsed => (Ended ?? DateTimeOffset.Now) - Started;
}

/// <summary>Runs shell commands that outlive a single tool call (servers, watchers, timers) and reports their output.</summary>
public sealed class BackgroundTaskManager : IDisposable
{
    readonly List<BackgroundTask> _tasks = new();
    readonly object _lock = new();
    int _next = 1;
    public const int MaxRunning = 8;

    /// <summary>Batched output lines (≤ ~0.5 s apart).</summary>
    public event Action<BackgroundTask, string[]>? Output;
    public event Action<BackgroundTask>? Exited;
    public event Action? Changed;

    public IReadOnlyList<BackgroundTask> All { get { lock (_lock) return _tasks.ToList(); } }
    public IReadOnlyList<BackgroundTask> Running { get { lock (_lock) return _tasks.Where(t => t.Running).ToList(); } }
    public BackgroundTask? Get(int id) { lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id); }

    public BackgroundTask Start(string command, string cwd, string description, string kind, bool wakeOnOutput)
    {
        lock (_lock)
            if (_tasks.Count(t => t.Running) >= MaxRunning)
                throw new InvalidOperationException($"Too many background tasks running ({MaxRunning}). Stop one first (TaskStop).");

        var psi = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command);
        psi.Environment["TERM"] = "dumb"; psi.Environment["PAGER"] = "cat"; psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PYTHONUNBUFFERED"] = "1"; psi.Environment["KVINDOCODE"] = "1";

        BackgroundTask task;
        lock (_lock) task = new BackgroundTask { Id = _next++, Kind = kind, Command = command, Description = description.Length > 0 ? description : Short(command), WakeOnOutput = wakeOnOutput };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        task.Proc = proc;
        try { proc.Start(); }
        catch (Exception e) { task.State = TaskState.Failed; task.Ended = DateTimeOffset.Now; task.AppendLine("Failed to start: " + e.Message); lock (_lock) _tasks.Add(task); Changed?.Invoke(); return task; }
        task.Pid = proc.Id;
        try { proc.StandardInput.Close(); } catch { }
        lock (_lock) _tasks.Add(task);

        var batch = new List<string>();
        var batchLock = new object();
        Timer? flushTimer = null;
        void Flush()
        {
            string[] lines;
            lock (batchLock) { lines = batch.ToArray(); batch.Clear(); }
            if (lines.Length > 0) Output?.Invoke(task, lines);
        }
        void Enqueue(string line)
        {
            task.AppendLine(line);
            lock (batchLock)
            {
                batch.Add(line);
                flushTimer ??= new Timer(_ => { lock (batchLock) { flushTimer?.Dispose(); flushTimer = null; } Flush(); }, null, 500, Timeout.Infinite);
            }
        }
        async Task Pump(StreamReader r)
        {
            try { string? l; while ((l = await r.ReadLineAsync()) != null) Enqueue(l); } catch { }
        }
        var pumps = Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError));

        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); } catch { }
            await Task.WhenAny(pumps, Task.Delay(1500));
            lock (batchLock) { flushTimer?.Dispose(); flushTimer = null; }
            Flush();
            if (task.State == TaskState.Running) task.State = TaskState.Exited;
            try { task.ExitCode = proc.ExitCode; } catch { }
            task.Ended = DateTimeOffset.Now;
            Changed?.Invoke();
            Exited?.Invoke(task);
        });
        Changed?.Invoke();
        return task;
    }

    public bool Stop(int id)
    {
        var t = Get(id);
        if (t is null || !t.Running) return false;
        t.State = TaskState.Killed;
        try { t.Proc?.Kill(true); } catch { }
        return true;
    }

    public void StopAll() { foreach (var t in Running) Stop(t.Id); }
    public void Dispose() => StopAll();

    static string Short(string c) { var l = c.Split('\n')[0].Trim(); return l.Length > 60 ? l[..60] + "…" : l; }
}
