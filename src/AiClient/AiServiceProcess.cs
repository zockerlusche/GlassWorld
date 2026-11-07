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

    private AiServiceProcess(Process process, IntPtr job, int port, string token)
    {
        _process = process;
        _job = job;
        Port = port;
        Token = token;
    }

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
    public static AiServiceProcess Start(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan readyTimeout)
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

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var job = OperatingSystem.IsWindows() ? JobObject.CreateKillOnClose(process) : IntPtr.Zero;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        var port = WaitForReadyLine(process, readyTimeout);
        if (port is null)
        {
            Kill(process);
            JobObject.Close(job);
            throw new TimeoutException($"AI service did not announce its port within {readyTimeout.TotalSeconds:F0} s.");
        }

        return new AiServiceProcess(process, job, port.Value, token);
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

    private static int? WaitForReadyLine(Process process, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (ParseReadyLine(line) is { } port)
                {
                    return (int?)port;
                }
            }

            return null;
        });
        return read.Wait(timeout) ? read.Result : null;
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
