using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.Core;

/// <summary>0.5: RDP imports through the real commands — mRemoteNG with passwords on request, .rdp files, folder defaults.</summary>
internal static class RdpImportUiChecks
{
    public static void Run(MainWindow main, AppStore store, Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshx-rdp-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var before = store.Profiles.Select(p => p.Id).ToHashSet();
        try
        {
            // mRemoteNG with its built-in password: RDP and SSH, a folder with an inherited password.
            string E(string text) => MRemoteNg.EncryptForTest(text, MRemoteNg.DefaultPassword);
            var xml = $"<?xml version=\"1.0\"?><mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"false\" Protected=\"{E("ThisIsNotProtected")}\" ConfVersion=\"2.6\">"
                + $"<Node Name=\"Бухгалтерия\" Type=\"Container\" Username=\"buh\" Domain=\"CORP\" Password=\"{E("Папка-1!")}\">"
                + "<Node Name=\"buh-pc\" Type=\"Connection\" Protocol=\"RDP\" Hostname=\"buh-pc.corp.test\" Port=\"3389\" InheritUsername=\"true\" InheritDomain=\"true\" InheritPassword=\"true\" RedirectPrinters=\"true\" />"
                + $"<Node Name=\"buh-ssh\" Type=\"Connection\" Protocol=\"SSH2\" Hostname=\"buh-ssh.corp.test\" Port=\"22\" Username=\"ops\" Password=\"{E("Ssh-2!")}\" />"
                + "</Node></mrng:Connections>";
            var file = Path.Combine(dir, "confCons.xml"); File.WriteAllText(file, xml);
            int asked = 0;
            main.MRemoteNgPathOverride = () => file;
            main.MRemoteNgPasswordOverride = (_, locked) => { asked++; if (locked) return null; return (true, (string?)null); };
            ImportProfilesWindow? preview = null;
            main.ImportPreviewOverride = w => { preview = w; return true; };
            Wait(main.ImportMRemoteNgAsync());
            var pc = store.Profiles.SingleOrDefault(p => p.Name == "buh-pc"); var ssh = store.Profiles.SingleOrDefault(p => p.Name == "buh-ssh");
            var folder = store.Settings.Folders.SingleOrDefault(f => f.Path == "Бухгалтерия");
            check(asked == 1 && preview is not null && pc is { IsRdp: true, Group: "Бухгалтерия" } && pc.Inherit.HasFlag(InheritFlags.Password) && ssh is { SaveSecret: true } && folder is { Username: "buh", Domain: "CORP", HasPassword: true },
                $"mRemoteNG: one question about passwords; RDP and SSH profiles, the folder with its values: asked={asked} pc={pc?.Inherit} ssh={ssh?.SaveSecret} folder={folder?.Username}");
            check(ssh is not null && Read(store, ssh.Id) == "Ssh-2!" && folder is not null && Read(store, folder.Id) == "Папка-1!" && pc is not null && Read(store, pc.Id) is null,
                "imported passwords are stored with Windows protection: the server's own and the folder's; the inheriting server has none of its own");
            var resolved = ProfileInheritance.Resolve(pc!, store.Settings.Folders);
            check(resolved is { Missing.Count: 0 } && resolved.Profile.Username == "buh" && resolved.Profile.Rdp.Domain == "CORP" && resolved.PasswordFolder == folder!.Id, "the RDP server takes user, domain and password from its folder");
            // «Параметры папки…»: changing the folder's user changes what its servers inherit.
            main.FolderWindowOverride = w => { w.UserBox.Text = "buh2"; w.Accept(); return true; };
            Wait(main.EditFolderAsync("Бухгалтерия"));
            check(store.Settings.Folders.Single(f => f.Path == "Бухгалтерия").Username == "buh2" && ProfileInheritance.Resolve(pc!, store.Settings.Folders).Profile.Username == "buh2" && Read(store, folder!.Id) == "Папка-1!",
                "folder settings change the inherited user and keep the saved password");
            // Without the request, a second import of the same file brings no passwords.
            foreach (var id in store.Profiles.Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToArray()) Wait(store.DeleteProfileAsync(id));
            main.MRemoteNgPasswordOverride = (_, _) => (false, (string?)null);
            Wait(main.ImportMRemoteNgAsync());
            var again = store.Profiles.Single(p => p.Name == "buh-ssh");
            check(!again.SaveSecret && Read(store, again.Id) is null, "declining keeps passwords out");
            // .rdp files: one good, one without an address.
            var good = Path.Combine(dir, "sklad.rdp"); File.WriteAllText(good, "full address:s:sklad.corp.test:3390\r\nusername:s:CORP\\kladovshik\r\nredirectclipboard:i:0\r\n", System.Text.Encoding.Unicode);
            var bad = Path.Combine(dir, "empty.rdp"); File.WriteAllText(bad, "screen mode id:i:2\r\n");
            main.RdpFilesOverride = () => new[] { good, bad };
            string[]? warnings = null; main.ImportPreviewOverride = w => { warnings = w.Warnings.ToArray(); return true; };
            Wait(main.ImportRdpFilesAsync());
            var sklad = store.Profiles.SingleOrDefault(p => p.Name == "sklad");
            check(sklad is { IsRdp: true, Host: "sklad.corp.test", Port: 3390, Username: "kladovshik" } && sklad.Rdp is { Domain: "CORP", Clipboard: false } && warnings is not null && warnings.Any(x => x.Contains("empty.rdp", StringComparison.Ordinal)),
                ".rdp files become RDP profiles; a file without an address is named: " + string.Join(" | ", warnings ?? []));
        }
        finally
        {
            main.MRemoteNgPathOverride = null; main.MRemoteNgPasswordOverride = null; main.ImportPreviewOverride = null; main.FolderWindowOverride = null; main.RdpFilesOverride = null;
            foreach (var id in store.Profiles.Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToArray()) Wait(store.DeleteProfileAsync(id));
            store.Settings.Folders.Clear();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static string? Read(AppStore store, Guid id) { var t = store.ReadSecretAsync(id); Wait(t); return t.Result; }
    static void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump(); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
