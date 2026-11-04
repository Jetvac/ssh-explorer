using SSHExplorer.Core;
using SSHExplorer.Monitoring;

/// <summary>The same deterministic data is compiled into baseline and current render runners.
/// Process states pass through the production /proc parser; no server is contacted.</summary>
internal static class CompositionFixtures
{
    public static readonly DateTimeOffset Timestamp=new(2026,11,3,12,0,0,TimeSpan.Zero);
    public static MonitorSnapshot Snapshot()
    {
        string[] states=new[]{'R','S','D'}.Select(State).ToArray();
        var rows=Enumerable.Range(0,160).Select(i=>new ProcessMetric(new ProcessIdentity(100+i,(ulong)(8000+i),1000),i==3?"long-service-name-with-readable-context-and-worker-arguments":"service-"+i,"fixture",states[i%states.Length],i switch{0=>0,1=>.2,2=>13.35,3=>86.75,4=>null,_=>(i%23)+.125},32L*1024*1024+i*1048576,i%4==0?null:2048+i*512,i%5==0?null:1024,"/usr/bin/service --fixture")).ToArray();
        return new(Timestamp,new(46,4,8L<<30,5L<<30,3L<<30,0,0,TimeSpan.FromDays(3),.7,.9,1.1,100,false),rows,[new("eth0",4096,8192,2048,1024)],[new("vda",4096,8192,1024,2048)],[new("ext4","/",16L<<30,4L<<30,12L<<30)],["Детерминированный UI fixture; I/O недоступен для части процессов."]);
    }
    public static IReadOnlyList<HistoryPoint> History()=>[new(Timestamp.AddSeconds(-120),10,35,0,0),new(Timestamp.AddSeconds(-119),30,36,0,0),new(Timestamp.AddSeconds(-60),20,37,0,0),new(Timestamp.AddSeconds(-59),50,38,0,0),new(Timestamp,46,37.5,0,0)];
    public static RemoteEntry[] Files()
    {
        return Enumerable.Range(0,180).Select(i=>
        {
            bool folder=i==160,link=i==168;
            string name=i switch{160=>"000_Документы",161=>"000_Очень длинное имя документа — проверка Unicode и выравнивания.txt",162=>".bashrc",163=>"Фото_пример.png",164=>"таблица.csv",165=>"архив.zip",166=>"script.sh",167=>"отчёт.pdf",168=>"ссылка-на-документ",169=>"server.conf",170=>"settings.json",171=>"unknown.sshx-unknown",_=>$"document-{i:000}.txt"};
            return new RemoteEntry{Name=name,FullPath="/srv/fixture/"+name,IsDirectory=folder,IsSymbolicLink=link,IsRegularFile=!folder&&!link,Length=folder?0:4096+i,LastWriteTimeUtc=Timestamp.UtcDateTime.AddHours(-i)};
        }).ToArray();
    }
    static string State(char value)
    {
        var fields=Enumerable.Repeat("0",22).ToArray();fields[0]=value.ToString();fields[11]="20";fields[12]="10";fields[19]="777";fields[21]="20";
        return ProcParser.ParseProcessStat("42 (fixture) "+string.Join(" ",fields))?.State??throw new InvalidOperationException("Production state parser rejected the render fixture.");
    }
}
