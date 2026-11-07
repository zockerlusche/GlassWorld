using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GlassWorld.AiClient;

/// <summary>
/// Starts and stops the local AI service as a child process (ADR-0006):
/// <list type="bullet">
/// <item>port chosen by the OS (<c>--port 0</c>), announced on stdout as <c>AI_SERVICE_READY port=N</c>;</item>
/// <item>a random session token per start, passed in the environment (<c>GW_AI_TOKEN</c>), never on the command line;</item>
/// <item>no orphans: on Windows the service runs in a Job Object with KILL_ON_JOB_CLOSE (also kills the
/// interpreter behind a venv launcher), everywhere it watches <c>--parent-pid</c>;</item>
/// <item>stop: <c>POST /shutdown</c> (unloads the model, frees VRAM), then kill after a timeout.</item>
/// </list>
/// </summary>
public sealed partial class AiServiceProcess : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _job;

    private AiServiceProcess(Process process, IntPtr job, int port, string token, int? staleKilled)
    {
        _process = process;
        _job = job;
        Port = port;
        Token = token;
        StaleKilled = staleKilled;
    }

    /// <summary>PID of a leftover service from an earlier session that was ended at start (null if none).</summary>
    public int? StaleKilled { get; }

    /// <summary>Port the service listens on (127.0.0.1).</summary>
    public int Port { get; }

    /// <summary>Session token of this start.</summary>
    public string Token { get; }

    /// <summary>Base address of the service.</summary>
    public Uri BaseAddress => new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/"));

    /// <summary>True while the process runs.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>Starts the service and waits for its ready line.</summary>
    /// <param name="fileName">Program, e.g. <c>uv</c>.</param>
    /// <param name="arguments">Arguments before the lifecycle flags, e.g. <c>run python -m glassworld_ai</c>.</param>
    /// <param name="workingDirectory">Working directory (repository root for <c>uv run</c>).</param>
    /// <param name="readyTimeout">How long to wait for the ready line (the model loads in the background later).</param>
    /// <param name="environment">Extra environment, e.g. GW_AI_LOCK_DIR and GW_AI_LOG_DIR in the user data folder.</param>
    public static AiServiceProcess Start(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan readyTimeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--port");
        info.ArgumentList.Add("0");
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.Environment["GW_AI_TOKEN"] = token;
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var job = OperatingSystem.IsWindows() ? JobObject.CreateKillOnClose(process) : IntPtr.Zero;
        var errors = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                errors.Enqueue(line);
                while (errors.Count > 20 && errors.TryDequeue(out string? _))
                {
                }
            }
        };
        process.BeginErrorReadLine();

        var reader = new OutputReader(process);
        var (finished, port) = reader.WaitForReady(readyTimeout);
        if (port is null)
        {
            Kill(process);
            JobObject.Close(job);
            var tail = string.Join(" | ", errors.TakeLast(3));
            if (finished)
            {
                // The service ended before announcing its port (bad arguments, missing dependency, ...).
                throw new InvalidOperationException($"AI service exited before its ready line: {tail}");
            }

            throw new TimeoutException($"AI service did not announce its port within {readyTimeout.TotalSeconds:F0} s. {tail}");
        }

        return new AiServiceProcess(process, job, port.Value, token, reader.StaleKilled);
    }

    /// <summary>Asks the service to shut down (unload the model), then kills it after the timeout.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = timeout };
                http.DefaultRequestHeaders.Add(HttpDecisionPort.TokenHeader, Token);
                using var response = await http.PostAsync(new Uri("shutdown", UriKind.Relative), null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not reachable: fall through to the kill below.
            }

            using var wait = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(_process);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Kill(_process);
        JobObject.Close(_job);
        _process.Dispose();
    }

    /// <summary>Parses the ready line of the service.</summary>
    public static int? ParseReadyLine(string? line)
    {
        var match = line is null ? null : ReadyLine().Match(line);
        return match is { Success: true } ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Parses <c>AI_SERVICE_STALE pid=N port=M</c> (leftover of an earlier session, verified by the service).</summary>
    public static int? ParseStaleLine(string? line)
    {
        var match = line is null ? null : StaleLine().Match(line);
        return match is { Success: true } ? int.Parse(match.Groups["pid"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Reads stdout for the whole life of the process: the stale and ready lines at start, and afterwards
    /// everything else, so the pipe buffer never fills and the service never blocks on writing (GW2 #30).
    /// </summary>
    private sealed class OutputReader
    {
        private readonly TaskCompletionSource<int?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OutputReader(Process process) => _ = Task.Run(() => Drain(process));

        public int? StaleKilled { get; private set; }

        public (bool Finished, int? Port) WaitForReady(TimeSpan timeout)
        {
            if (!_ready.Task.Wait(timeout))
            {
                return (false, null);
            }

            var port = _ready.Task.Result;
            return (port is null, port);
        }

        private void Drain(Process process)
        {
            try
            {
                string? line;
                while ((line = process.StandardOutput.ReadLine()) is not null)
                {
                    if (ParseStaleLine(line) is { } stale)
                    {
                        StaleKilled = EndLeftover(stale);
                    }
                    else if (ParseReadyLine(line) is { } port)
                    {
                        _ready.TrySetResult(port);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Process gone or disposed.
            }

            _ready.TrySetResult(null);
        }

        /// <summary>The service reports a leftover only if PID and start time match, so ending it is safe.</summary>
        private static int? EndLeftover(int pid)
        {
            try
            {
                using var leftover = Process.GetProcessById(pid);
                leftover.Kill(entireProcessTree: true);
                leftover.WaitForExit(5000);
                return pid;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return null;
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    [GeneratedRegex(@"^AI_SERVICE_READY port=(?<port>[0-9]{1,5})\s*$")]
    private static partial Regex ReadyLine();

    [GeneratedRegex(@"^AI_SERVICE_STALE pid=(?<pid>[0-9]{1,10}) port=[0-9]{1,5}\s*$")]
    private static partial Regex StaleLine();

    /// <summary>Windows Job Object with KILL_ON_JOB_CLOSE: when the game dies, the service dies with it.</summary>
    private static partial class JobObject
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        public static IntPtr CreateKillOnClose(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return IntPtr.Zero;
            }

            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size) || !AssignProcessToJobObject(job, process.Handle))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }

        public static void Close(IntPtr job)
        {
            if (job != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                CloseHandle(job);
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [SupportedOSPlatform("windows")]
        private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [SupportedOSPlatform("windows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [SupportedOSPlatform("windows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [SupportedOSPlatform("windows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
