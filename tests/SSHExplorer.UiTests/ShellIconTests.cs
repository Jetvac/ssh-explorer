using System.Windows.Media.Imaging;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class ShellIconTests
{
    public static int Run()
    {
        try
        {
            int count = 0;
            void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
            var folder = new RemoteEntry { Name = "Documents", IsDirectory = true };
            foreach (int tier in new[] { 16, 32, 48, 256 })
            {
                var icon = SystemFileIconProvider.GetAsync(folder, tier).GetAwaiter().GetResult() as BitmapSource;
                if (icon is null || !icon.IsFrozen || icon.PixelWidth < tier || icon.PixelHeight < tier) throw new Exception($"Native Shell folder tier {tier} was not supplied at its actual resolution.");
            }
            Check(true, "native Shell supplies frozen folder resources at all four native size tiers rather than enlarging a small bitmap");
            var file = new RemoteEntry { Name = "script.sh", IsRegularFile = true };
            var requests = Enumerable.Range(0, 1000).Select(_ => SystemFileIconProvider.GetAsync(file, 20)).ToArray(); Task.WhenAll(requests).GetAwaiter().GetResult();
            Check(requests.All(t => ReferenceEquals(t, requests[0])) && requests[0].Result is BitmapSource { IsFrozen: true, PixelWidth: >= 32 }, "one background Shell request is shared by a thousand same-type file objects");
            int before = SystemFileIconProvider.CachedKinds;
            var models = Enumerable.Range(0, 10_000).Select(i => new FileItemViewModel(new RemoteEntry { Name = $"virtual-{i}.json", IsRegularFile = true })).ToArray();
            foreach (var model in models) _ = model.FileIcon;
            Check(SystemFileIconProvider.CachedKinds == before, "ten-thousand unmaterialized models do not queue ten-thousand Shell calls");
            var fractional = SystemFileIconProvider.GetAsync(file, 24).GetAwaiter().GetResult();
            Check(ReferenceEquals(fractional, requests[0].Result), "fractional DPI requests reuse a native resource with enough pixels");
            SystemFileIconProvider.Invalidate(); var next = SystemFileIconProvider.GetAsync(file, 20).GetAwaiter().GetResult();
            Check(next is BitmapSource { IsFrozen: true } && !ReferenceEquals(next, fractional), "environment invalidation refreshes the association cache without changing document models");
            Console.WriteLine($"RESULT: {count} native Shell icon checks passed; real installed Windows resources, no windows, no document file queries or execution"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
