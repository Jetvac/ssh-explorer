using System.Globalization;

namespace SSHExplorer.Monitoring;

public static class CollectorCommand
{
    public static string Create(int maxProcesses)
    {
        if (maxProcesses is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxProcesses));
        return Script.Replace("__MAX_PROCESSES__", maxProcesses.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__ENCODER_SETUP__", EncoderSetup, StringComparison.Ordinal);
    }

    internal const string EncoderSetup = """
        if printf x | base64 -w0 >/dev/null 2>&1; then
          encode() { base64 -w0; }
        else
          encode() { base64 | tr -d '\n'; }
        fi
        """;

    private const string Script = """
        LC_ALL=C; export LC_ALL
        for x in dd base64 tr; do
          if ! command -v "$x" >/dev/null 2>&1; then printf 'SSHXERROR:missing:%s\n' "$x"; exit 72; fi
        done
        if [ ! -d /proc ]; then printf 'SSHXERROR:proc-unavailable\n'; exit 73; fi
        __ENCODER_SETUP__
        printf 'SSHXMON1\n'
        frame() {
          printf '%s\t' "$1"
          if [ -r "$2" ]; then dd if="$2" bs="$3" count=1 2>/dev/null | encode; else printf '!'; fi
          printf '\n'
        }
        meta() { printf '%s\t' "$1"; printf '%s' "$2" | encode; printf '\n'; }
        frame sys/stat /proc/stat 65536
        frame sys/meminfo /proc/meminfo 16384
        frame sys/uptime /proc/uptime 1024
        frame sys/loadavg /proc/loadavg 1024
        frame sys/boot_id /proc/sys/kernel/random/boot_id 128
        frame sys/net /proc/net/dev 65536
        frame sys/disks /proc/diskstats 131072
        frame sys/passwd /etc/passwd 131072
        ticks=''; pages=''
        if command -v getconf >/dev/null 2>&1; then ticks=$(getconf CLK_TCK 2>/dev/null); pages=$(getconf PAGESIZE 2>/dev/null); fi
        meta meta/ticks "$ticks"
        meta meta/pages "$pages"
        printf 'sys/df\t'
        if command -v df >/dev/null 2>&1; then df -Pk 2>/dev/null | dd bs=131072 count=1 2>/dev/null | encode; else printf '!'; fi
        printf '\n'
        n=0; limited=0
        for d in /proc/[0-9]*; do
          [ -d "$d" ] || continue
          n=$((n+1))
          if [ "$n" -gt __MAX_PROCESSES__ ]; then limited=1; n=$((n-1)); break; fi
          p=${d##*/}
          case "$p" in ''|*[!0-9]*) continue;; esac
          frame "p/$p/stat" "$d/stat" 4096
          frame "p/$p/status" "$d/status" 8192
          frame "p/$p/cmdline" "$d/cmdline" 4096
          frame "p/$p/io" "$d/io" 4096
          frame "p/$p/stat_end" "$d/stat" 4096
        done
        meta meta/count "$n"
        meta meta/limited "$limited"
        printf 'END\n'
        """;
}
