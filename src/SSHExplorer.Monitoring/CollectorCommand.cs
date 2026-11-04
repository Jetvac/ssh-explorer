using System.Globalization;
using System.Text;

namespace SSHExplorer.Monitoring;

/// <summary>What one monitoring exec has to collect. Everything else is cached or fetched on demand.</summary>
/// <param name="IncludeProcesses">Per-process table (Processes page). The Performance page only needs system counters.</param>
/// <param name="IncludeUsers">Read /etc/passwd for UID names. Requested on the first sample, when an unknown UID appears, and periodically.</param>
/// <param name="CommandLinePids">PIDs whose command line is not cached yet (first observation of a process). Bounded.</param>
public sealed record CollectorRequest(bool IncludeProcesses, bool IncludeUsers, IReadOnlyList<int> CommandLinePids, int MaxProcesses = 2048)
{
    public const int MaxCommandLinesPerSample = 512;
    public static CollectorRequest Full(int maxProcesses) => new(true, true, [], maxProcesses);
    public static CollectorRequest SystemOnly { get; } = new(false, false, []);
}

/// <summary>
/// Builds the POSIX sh collector. No server installation: only /proc, existing dd/base64/tr and,
/// when present, df/getconf.
/// </summary>
/// <remarks>
/// beta.5 framed every per-process file with its own <c>dd | base64</c> pair: five files per PID,
/// about 15 fork/exec per process (5 535 forks and 2.8 s for 371 processes on the reference
/// container, see docs/PACKAGING_AND_PERFORMANCE.md). Protocol 2 reads the per-process text files
/// with the shell's own <c>read</c> builtin inside one stream encoded by a single base64 process.
/// Each record has exactly five lines because <c>read</c> never returns an embedded newline:
/// <c>P pid</c>, stat, "uid [VmRSS kB]", "read_bytes write_bytes", stat re-read at the end (PID
/// reuse guard). VmRSS is read only when getconf PAGESIZE is unavailable (stat RSS × page size).
/// A stat whose comm contains a newline is truncated and is counted as unreadable, never misparsed.
/// Command lines (binary, NUL separated) are framed individually only for newly seen PIDs.
/// </remarks>
public static class CollectorCommand
{
    public static string Create(int maxProcesses) => Create(CollectorRequest.Full(maxProcesses));

    public static string Create(CollectorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxProcesses is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.CommandLinePids.Count > CollectorRequest.MaxCommandLinesPerSample || request.CommandLinePids.Any(pid => pid <= 0))
            throw new ArgumentOutOfRangeException(nameof(request));
        var script = new StringBuilder(Header.Replace("__ENCODER_SETUP__", EncoderSetup, StringComparison.Ordinal));
        script.Append('\n').Append(SystemFrames);
        if (request.IncludeUsers) script.Append("\nframe sys/passwd /etc/passwd 131072");
        if (request.IncludeProcesses)
        {
            script.Append('\n').Append(Processes.Replace("__MAX_PROCESSES__", request.MaxProcesses.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (request.CommandLinePids.Count > 0)
            {
                // Only validated positive integers reach the script.
                var list = string.Join(' ', request.CommandLinePids.Distinct().Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
                script.Append("\nfor p in ").Append(list).Append("; do frame \"p/$p/cmdline\" \"/proc/$p/cmdline\" 4096; done");
            }
        }
        script.Append("\nprintf 'END\\n'\n");
        return script.ToString();
    }

    internal const string EncoderSetup = """
        if printf x | base64 -w0 >/dev/null 2>&1; then
          encode() { base64 -w0; }
        else
          encode() { base64 | tr -d '\n'; }
        fi
        """;

    private const string Header = """
        LC_ALL=C; export LC_ALL
        for x in dd base64 tr; do
          if ! command -v "$x" >/dev/null 2>&1; then printf 'SSHXERROR:missing:%s\n' "$x"; exit 72; fi
        done
        if [ ! -d /proc ]; then printf 'SSHXERROR:proc-unavailable\n'; exit 73; fi
        __ENCODER_SETUP__
        printf 'SSHXMON2\n'
        frame() {
          printf '%s\t' "$1"
          if [ -r "$2" ]; then dd if="$2" bs="$3" count=1 2>/dev/null | encode; else printf '!'; fi
          printf '\n'
        }
        meta() { printf '%s\t' "$1"; printf '%s' "$2" | encode; printf '\n'; }
        """;

    private const string SystemFrames = """
        frame sys/stat /proc/stat 65536
        frame sys/meminfo /proc/meminfo 16384
        frame sys/uptime /proc/uptime 1024
        frame sys/loadavg /proc/loadavg 1024
        frame sys/boot_id /proc/sys/kernel/random/boot_id 128
        frame sys/net /proc/net/dev 65536
        frame sys/disks /proc/diskstats 131072
        ticks=''; pages=''
        if command -v getconf >/dev/null 2>&1; then ticks=$(getconf CLK_TCK 2>/dev/null); pages=$(getconf PAGESIZE 2>/dev/null); fi
        meta meta/ticks "$ticks"
        meta meta/pages "$pages"
        printf 'sys/df\t'
        if command -v df >/dev/null 2>&1; then df -Pk 2>/dev/null | dd bs=131072 count=1 2>/dev/null | encode; else printf '!'; fi
        printf '\n'
        """;

    private const string Processes = """
        printf 'procs\t'
        {
          n=0; limited=0
          for d in /proc/[0-9]*; do
            set -f; p=${d##*/}
            case "$p" in ''|*[!0-9]*) continue;; esac
            if [ "$n" -ge __MAX_PROCESSES__ ]; then limited=1; break; fi
            n=$((n+1))
            s=''; IFS= read -r s 2>/dev/null < "$d/stat" || { printf 'X %s\n' "$p"; continue; }
            u=''; r=''; i=0
            while [ "$i" -lt 40 ] && IFS= read -r l; do
              i=$((i+1))
              case "$l" in
                Uid:*) set -- ${l#Uid:}; u=${1-}; [ -n "$pages" ] && break;;
                VmRSS:*) set -- ${l#VmRSS:}; r=${1-}; break;;
              esac
            done 2>/dev/null < "$d/status"
            rb=''; wb=''
            while IFS= read -r l; do
              case "$l" in read_bytes:*) rb=${l#read_bytes: };; write_bytes:*) wb=${l#write_bytes: };; esac
            done 2>/dev/null < "$d/io"
            e=''; IFS= read -r e 2>/dev/null < "$d/stat"
            printf 'P %s\n%s\n%s %s\n%s %s\n%s\n' "$p" "$s" "$u" "$r" "$rb" "$wb" "$e"
          done
          printf 'C %s %s\n' "$n" "$limited"
        } | encode
        printf '\n'
        """;
}
