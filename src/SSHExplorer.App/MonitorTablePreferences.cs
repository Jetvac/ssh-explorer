using System.Collections;
using System.ComponentModel;
using System.Globalization;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

internal sealed class MonitorTablePreferences
{
    internal static readonly string[] Keys=["Name","Pid","User","State","Cpu","Memory","Read","Write"];
    readonly Dictionary<string,MonitorColumnPreference> values=[];
    public string? SortColumn { get; private set; }
    public ListSortDirection SortDirection { get; private set; }
    public MonitorTablePreferences(MonitorPreferences? saved=null)
    {
        for(int index=0;index<Keys.Length;index++)
        {
            string key=Keys[index];var source=saved?.Columns?.FirstOrDefault(c=>c is not null&&c.Key==key);
            bool mandatory=key is "Name" or "Cpu" or "Memory";
            var widthMode=source?.WidthMode??(key=="Name"?MonitorColumnWidthMode.Star:MonitorColumnWidthMode.Pixel);
            double width=source?.Width??DefaultWidth(key);
            if(!Enum.IsDefined(widthMode)||!double.IsFinite(width)||width<=0||width>4000){width=DefaultWidth(key);widthMode=key=="Name"?MonitorColumnWidthMode.Star:MonitorColumnWidthMode.Pixel;}
            if(widthMode==MonitorColumnWidthMode.Star)width=Math.Clamp(width,.1,10);
            if(widthMode==MonitorColumnWidthMode.Pixel)width=Math.Max(key=="Name"?180:56,width);
            if(widthMode==MonitorColumnWidthMode.Auto)width=1;
            values[key]=new(){Key=key,Visible=mandatory||(source?.Visible??(key is "Pid" or "State")),ExplicitVisibility=source?.ExplicitVisibility??false,
                Order=source is {Order:>=0 and <8}?source.Order:index,Width=width,WidthMode=widthMode};
        }
        var order=values.Values.OrderBy(c=>c.Order).ThenBy(c=>Array.IndexOf(Keys,c.Key)).ToArray();
        for(int i=0;i<order.Length;i++)order[i].Order=i;
        if(saved?.SortColumn is { } column && values.ContainsKey(column)&&Enum.IsDefined(saved.SortDirection)){SortColumn=column;SortDirection=saved.SortDirection;}
    }
    static double DefaultWidth(string key)=>key switch{"Name"=>1,"Pid"=>78,"User"=>120,"State"=>120,"Cpu"=>96,"Memory"=>126,_=>118};
    public MonitorColumnPreference this[string key]=>values[key];
    public bool EffectiveVisible(string key,double width)=>values[key].Visible&&(values[key].ExplicitVisibility||key is "Name" or "Cpu" or "Memory"||width>=(key is "Pid" or "State"?650:900));
    public void SetVisible(string key,bool visible){values[key].Visible=key is "Name" or "Cpu" or "Memory"||visible;values[key].ExplicitVisibility=true;}
    public void SetWidth(string key,double width,MonitorColumnWidthMode mode)
    { if(!Enum.IsDefined(mode)||!double.IsFinite(width)||width<=0||width>4000)return;values[key].Width=mode==MonitorColumnWidthMode.Star?Math.Clamp(width,.1,10):mode==MonitorColumnWidthMode.Auto?1:Math.Max(key=="Name"?180:56,width);values[key].WidthMode=mode; }
    public void SetOrder(IEnumerable<string> keys){int i=0;foreach(var key in keys.Concat(Keys).Distinct())if(values.TryGetValue(key,out var column))column.Order=i++;}
    public void SetSort(string? key,ListSortDirection direction){SortColumn=key is not null&&values.ContainsKey(key)?key:null;SortDirection=Enum.IsDefined(direction)?direction:ListSortDirection.Ascending;}
    public MonitorPreferences Snapshot()=>new(){SortColumn=SortColumn,SortDirection=SortDirection,Columns=values.Values.OrderBy(c=>c.Order).Select(c=>new MonitorColumnPreference
        {Key=c.Key,Visible=c.Visible,ExplicitVisibility=c.ExplicitVisibility,Order=c.Order,Width=c.Width,WidthMode=c.WidthMode}).ToList()};
    public static string Caption(string key)=>key switch{"Name"=>"Имя","Pid"=>"PID","User"=>"Пользователь","State"=>"Состояние","Cpu"=>"ЦП","Memory"=>"Память","Read"=>"Чтение",_=>"Запись"};
    public static string Property(string key)=>key switch{"Name"=>nameof(ProcessItemViewModel.Name),"Pid"=>nameof(ProcessItemViewModel.Pid),"User"=>nameof(ProcessItemViewModel.User),"State"=>nameof(ProcessItemViewModel.State),
        "Cpu"=>nameof(ProcessItemViewModel.Cpu),"Memory"=>nameof(ProcessItemViewModel.MemoryBytes),"Read"=>nameof(ProcessItemViewModel.ReadBytesPerSecond),_=>nameof(ProcessItemViewModel.WriteBytesPerSecond)};
}

internal sealed class ProcessSortComparer(string key,ListSortDirection direction,CultureInfo? culture=null):IComparer
{
    readonly CompareInfo strings=(culture??CultureInfo.CurrentCulture).CompareInfo;
    public int Compare(object? left,object? right)
    {
        if(ReferenceEquals(left,right))return 0;
        if(left is not ProcessItemViewModel a)return 1;if(right is not ProcessItemViewModel b)return -1;
        object? x=Value(a),y=Value(b);
        bool unknownX=x is null||x is double dx&&!double.IsFinite(dx),unknownY=y is null||y is double dy&&!double.IsFinite(dy);
        if(unknownX!=unknownY)return unknownX?1:-1;
        int result=unknownX?0:x is string sx&&y is string sy?strings.Compare(sx,sy,CompareOptions.IgnoreCase|CompareOptions.StringSort):((IComparable)x!).CompareTo(y);
        if(result!=0)return direction==ListSortDirection.Ascending?result:-Math.Sign(result);
        int identity=a.Pid.CompareTo(b.Pid);if(identity!=0)return identity;
        identity=a.Metric.Identity.StartTimeTicks.CompareTo(b.Metric.Identity.StartTimeTicks);return identity!=0?identity:a.Metric.Identity.UserId.CompareTo(b.Metric.Identity.UserId);
    }
    object? Value(ProcessItemViewModel row)=>key switch{"Name"=>row.Name,"Pid"=>row.Pid,"User"=>row.User,"State"=>row.State,"Cpu"=>row.Cpu,"Memory"=>row.MemoryBytes,"Read"=>row.ReadBytesPerSecond,"Write"=>row.WriteBytesPerSecond,_=>row.Name};
}
