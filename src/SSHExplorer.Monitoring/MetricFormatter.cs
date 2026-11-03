using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>Display roles only; calculated values and numeric sorting retain full precision.</summary>
public static class MetricFormatter
{
    public static string ProcessState(string state)=>state switch
    { "R"=>"Выполняется","S"=>"Ожидание","D"=>"Ожидание I/O","Z"=>"Зомби","T"=>"Остановлен","t"=>"Отладка","I"=>"Бездействие","X" or "x"=>"Завершён",_=>state };
    public const string Unknown = "—";
    public static string AggregatePercent(double? value, CultureInfo? culture = null) => Percent(value,"0",culture);
    public static string ProcessPercent(double? value, CultureInfo? culture = null) => Percent(value,"0.0",culture);
    static string Percent(double? value,string format,CultureInfo? culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        return value is { } number && double.IsFinite(number) && number >= 0
            ? number.ToString(format,culture) + culture.NumberFormat.PercentSymbol : Unknown;
    }
    public static string Number(double? value,CultureInfo? culture=null) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.00",culture ?? CultureInfo.CurrentCulture) : Unknown;
    public static string Size(long? bytes,CultureInfo? culture=null) => bytes is { } value && value >= 0 ? Bytes(value,culture) : Unknown;
    public static string Speed(double? bytesPerSecond,CultureInfo? culture=null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return bytesPerSecond is { } value && double.IsFinite(value) && value >= 0
            ? Bytes(value,culture) + (culture.TwoLetterISOLanguageName == "ru" ? "/с" : "/s") : Unknown;
    }
    static string Bytes(double bytes,CultureInfo? culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        string[] units = culture.TwoLetterISOLanguageName == "ru" ? ["Б","КБ","МБ","ГБ","ТБ","ПБ","ЭБ"] : ["B","KB","MB","GB","TB","PB","EB"];
        int unit=0;
        while(bytes>=1024 && unit<units.Length-1){bytes/=1024;unit++;}
        return bytes.ToString(unit==0?"N0":"N1",culture)+" "+units[unit];
    }
    public static string Time(DateTimeOffset value,CultureInfo? culture=null) => value.LocalDateTime.ToString("T",culture ?? CultureInfo.CurrentCulture);
    public static string Duration(TimeSpan? value,CultureInfo? culture=null)
    {
        if(value is not { } time || time < TimeSpan.Zero)return Unknown;
        culture ??= CultureInfo.CurrentCulture;
        string days=culture.TwoLetterISOLanguageName=="ru"?"дн.":"d";
        return $"{((long)time.TotalDays).ToString(culture)} {days} {time.ToString(@"hh\:mm\:ss",culture)}";
    }
    public static string Seconds(TimeSpan value,CultureInfo? culture=null) => value.TotalSeconds.ToString("0.000",culture ?? CultureInfo.CurrentCulture)
        +( (culture ?? CultureInfo.CurrentCulture).TwoLetterISOLanguageName=="ru"?" с":" s");
}
