#if CORE_TESTS
using System.Text;
using System.Text.Json;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal static class CollectorEncodingChecks
{
    public static async Task<int> RunAsync(string configurationFile)
    {
        try
        {
            await using var session=await MonitorBenchmark.OpenSessionAsync(configurationFile);
            var payload=Encoding.UTF8.GetBytes("line\n\0binary\t'\"Ω"+new string('x',160));const int bound=97;
            var format=string.Concat(payload.Select(b=>"\\"+Convert.ToString(b,8).PadLeft(3,'0')));
            var modes=new List<object>();
            foreach(var fallback in new[]{false,true})
            {
                string wrapper=fallback?"base64() { if [ \"$1\" = -w0 ]; then return 2; fi; command base64 \"$@\"; }\n":"";
                string command="LC_ALL=C; export LC_ALL\n"+wrapper+CollectorCommand.EncoderSetup+"\n"
                    +"printf 'SSHXMON1\\nsys/check\\t'\n"
                    +"printf "+PosixPath.Quote(format)+" | dd bs="+bound+" count=1 2>/dev/null | encode\n"
                    +"printf '\\nsys/missing\\t!\\nEND\\n'\n";
                var output=await session.ExecuteAsync(command);
                if(output.ExitStatus!=0)throw new InvalidOperationException("Read-only encoder check failed.");
                var frames=ProcParser.DecodeFrames(output.Output);
                if(frames["sys/check"]!=Encoding.UTF8.GetString(payload.AsSpan(0,bound))||frames["sys/missing"]is not null)
                    throw new InvalidDataException("Encoder did not preserve bounded binary UTF-8 framing.");
                // A >76-character field must remain one record with unchanged missing-value sentinel.
                if(output.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length!=4)
                    throw new InvalidDataException("Encoder wrapped a protocol field.");
                modes.Add(new{mode=fallback?"forced-unsupported-nowrap-fallback":"automatic-nowrap-probe",result="PASS",boundedBytes=bound,nulAndNewlinePreserved=true,missingSentinelPreserved=true});
            }
            Console.WriteLine(JsonSerializer.Serialize(new{label="read-only-collector-encoding",result="PASS",checks=modes},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error)
        { Console.WriteLine(JsonSerializer.Serialize(new{label="read-only-collector-encoding",result="FAIL",exception=error.GetType().FullName,hresult=error.HResult.ToString("X8")}));return 1; }
    }
}
#endif
