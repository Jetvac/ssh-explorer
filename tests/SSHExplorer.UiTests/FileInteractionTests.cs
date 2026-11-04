using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

/// <summary>Real browser controls and handlers with a controlled operations boundary.
/// This proves UI state/identity, not remote SFTP semantics or physical keyboard input.</summary>
internal static class FileInteractionTests
{
    public static int StateOnly()
    {
        try
        {
            int checks=0;void Assert(bool value,string label){Check(value,label);checks++;}
            var row=new FileItemViewModel(new RemoteEntry{Name="report.final.txt",FullPath="/fixture/report.final.txt",IsRegularFile=true});
            var edit=new FileNameEdit(row);row.Edit=edit;edit.Text=" kept spaces .txt";
            string? source=null,target=null;var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
            var pending=edit.CommitAsync((a,b)=>{source=a;target=b;calls++;return gate.Task;});
            _=edit.CommitAsync((_,_)=>{calls++;return Task.CompletedTask;});
            Assert(edit.IsBusy&&calls==1&&!edit.Cancel(),"pending inline rename admits one operation and cannot silently cancel its remote result");
            gate.SetResult();pending.GetAwaiter().GetResult();
            Assert(source=="/fixture/report.final.txt"&&target=="/fixture/ kept spaces .txt"&&!edit.IsActive,"rename captures exact source and preserves significant name whitespace");
            var failed=new FileNameEdit(row){Text="collision.txt"};failed.CommitAsync((_,_)=>throw new IOException("fixture conflict")).GetAwaiter().GetResult();
            Assert(failed.IsActive&&!failed.IsBusy&&failed.Text=="collision.txt"&&failed.Error.Length>0,"rename failure retains typed input and an actionable error");
            Assert(FileNameEdit.SelectionLength("report.final.txt",false)==12&&FileNameEdit.SelectionLength(".bashrc",false)==7&&FileNameEdit.SelectionLength("folder.with.dot",true)==15,"file extension, dotfile and directory selection ranges are distinct");
            FileItemViewModel HiddenExtension(string value)=>new(new RemoteEntry{Name=value,FullPath="/fixture/"+value,IsRegularFile=true}){ShowExtensions=false};
            Assert(HiddenExtension(".config.json").DisplayName==".config.json"&&HiddenExtension("part\\name.txt").DisplayName=="part\\name"&&HiddenExtension("name.").DisplayName=="name."&&FileNameEdit.SelectionLength(".config.json",false)==12&&FileNameEdit.SelectionLength("name.",false)==5,"POSIX hidden-extension display preserves dotfiles, trailing dots and backslash name segments");
            var indices=FileSelectionGeometry.Intersecting(new Rect(0,1,99,798),10000,1,100,20).ToArray();
            Assert(indices.Length==40&&indices.First()==0&&indices.Last()==39,"lasso selects model indices across non-realized rows using content coordinates");
            Assert(!FileSelectionGeometry.Intersecting(new Rect(0,0,100,100),0,1,100,20).Any(),"empty lasso model cannot create synthetic selected items");
            Console.WriteLine($"RESULT: {checks} file edit/selection state checks passed; no Application, Window, HWND or SSH created");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    public static int Run(string[] args)
    {
        CiVisualEvidence.RequireCi();
        try
        {
            var app=TestApplication.Create();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Theme.Apply("Light");CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            int checks=0;void Assert(bool value,string label){Check(value,label);checks++;}
            var evidence=new CiVisualEvidence(Value(args,"--render-dir")??"artifacts/ui-beta5-interactions");
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","file-interaction-store",Guid.NewGuid().ToString("N")));
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);store.Settings.ShowHidden=true;
            var profile=new ServerProfile{Name="Файлы · управляемый стенд",Host="127.0.0.1",Username="fixture"};store.Profiles.Add(profile);
            var session=new ServerSession(profile,store);var main=new MainWindow{Width=1320,Height=850,ShowInTaskbar=false,ShowActivated=false,Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual};
            var operations=new Operations();var files=new FileBrowserView(session,main,operations);var model=(FileBrowserViewModel)files.DataContext;
            // Tree expansion is not part of this operations fixture. It must not call a live transport.
            ((TreeView)Field(files,"tree")).Items.Clear();Complete(files.InitializeAsync("/srv/fixture",CancellationToken.None));
            var monitor=new MonitorView(new MonitorViewModel());var view=new SessionView(session,main,files,monitor);((SessionViewModel)Field(view,"viewModel")).StatusText="Данные стенда · без подключения";view.SetActive(false);
            typeof(MainWindow).GetMethod("AddSessionTab",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,[profile,session,view]);main.Show();view.SetActive(false);view.ShowSection("files");Layout(main);
            var table=(DataGrid)Field(files,"table");var tiles=(ListBox)Field(files,"tiles");var address=(TextBox)Field(files,"address");
            var original=model.Entries.Single(row=>row.Name=="document-005.txt");table.SelectedItem=original;table.ScrollIntoView(original);Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"files-interaction-initial",new{theme="Light",fixtureOnly=true,phase="before interaction assertions",operationBoundary="controlled list/create/rename; no live SFTP"});
            GroupPressCase(files,table,evidence,Assert,"details",false);table.SelectedItem=original;table.ScrollIntoView(original);Layout(main);
            var originalPresenter=Descendants<FileNamePresenter>(table).Single(presenter=>ReferenceEquals(presenter.DataContext,original));
            originalPresenter.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});Pump();
            Assert(Field(files,"lassoOwner") is null&&Field(files,"dragStart") is Point,"an actual item-origin mouse-down remains a file-drag candidate and does not begin blank-space lasso");
            // Raise the real routed key event. The browser and FileNamePresenter handlers participate.
            SendKey(table,Key.F2);Layout(main);var editor=Editor(files,original);
            Assert(original.Edit is{IsActive:true}&&editor.IsVisible&&editor.Text==original.Name&&editor.SelectionStart==0&&editor.SelectionLength=="document-005".Length,"Details F2 creates the actual name TextBox and selects only the file base name");
            evidence.Capture((FrameworkElement)main.Content,"files-inline-rename",new{theme="Light",fixtureOnly=true,view="Details",sourceName=original.Name,programmaticRoutedKey="F2"});
            editor.Text="renamed Unicode Ω.txt";operations.RenameGate=NewGate();SendKey(editor,Key.Enter);Pump();SendKey(editor,Key.Enter);Pump();
            Assert(operations.Renames.Count==1&&original.Edit is{IsBusy:true}&&editor.IsReadOnly,"duplicate Enter while the actual editor is pending sends one rename and shows a read-only pending field");
            table.SelectedItem=model.Entries.Single(row=>row.Name=="document-006.txt");model.Descending=true;Invoke(files,"ApplyItems");Complete(files.RefreshAsync());Layout(main);
            Assert(model.Entries.Contains(original)&&ReferenceEquals(original,model.Entries.Single(row=>row.Entry.FullPath=="/srv/fixture/document-005.txt"))&&original.Edit?.Text=="renamed Unicode Ω.txt","sort, selection change and same-path refresh retain the captured editing row and typed input");
            operations.RenameGate.SetResult();WaitFor(()=>original.Edit is null&&!model.IsMutating);Layout(main);
            Assert(operations.Renames[0].Source=="/srv/fixture/document-005.txt"&&operations.Exists("/srv/fixture/renamed Unicode Ω.txt")&&operations.Exists("/srv/fixture/document-006.txt"),"completion renames the original source rather than the newly selected item");
            var renamed=model.Entries.Single(row=>row.Name=="renamed Unicode Ω.txt");files.BeginNameEdit(renamed);Layout(main);editor=Editor(files,renamed);editor.Text="document-006.txt";SendKey(editor,Key.Enter);WaitFor(()=>renamed.Edit?.Error.Length>0);Layout(main);
            Assert(editor.IsVisible&&editor.Text=="document-006.txt"&&renamed.Edit is{IsActive:true,IsBusy:false}&&AutomationProperties.GetHelpText(editor).Length>0,"collision keeps the actual editor, exact requested name and accessible inline error");
            evidence.Capture((FrameworkElement)main.Content,"files-inline-error",new{theme="Light",fixtureOnly=true,operationBoundary="controlled list/create/rename",physicalInput=false});
            operations.FailNextList=true;editor.Text="fixed-after-conflict.txt";SendKey(editor,Key.Enter);WaitFor(()=>renamed.Edit is null&&!model.IsMutating);Layout(main);
            Assert(operations.Exists("/srv/fixture/fixed-after-conflict.txt")&&operations.Exists("/srv/fixture/document-006.txt"),"correcting the failed editor and pressing Enter retries without overwriting the conflicting object");
            Assert(model.Entries.Contains(renamed)&&renamed.Entry.FullPath=="/srv/fixture/fixed-after-conflict.txt"&&model.SelectedPaths.Contains(renamed.Entry.FullPath),"confirmed rename preserves the updated row identity and selection even when its following directory listing fails");
            renamed=model.Entries.Single(row=>row.Name=="fixed-after-conflict.txt");files.BeginNameEdit(renamed);Layout(main);editor=Editor(files,renamed);editor.Text="discarded.txt";
            SendKey(editor,Key.Escape);Layout(main);Assert(renamed.Edit is null&&operations.Exists(renamed.Entry.FullPath)&&!operations.Exists("/srv/fixture/discarded.txt"),"Escape from rename ends editing without changing the original object");
            // Error inputs remain editable and focus changes do not discard an unsuccessful address.
            Invoke(files,"EditAddress");address.Text="/missing/typed-path";Complete(files.SubmitAddressAsync());Layout(main);
            Assert(address.Visibility==Visibility.Visible&&address.Text=="/missing/typed-path"&&!address.IsReadOnly&&model.CurrentPath=="/srv/fixture","failed address stays visible and editable while the last successful folder remains current");
            table.Focus();Pump();Assert(address.Text=="/missing/typed-path"&&address.Visibility==Visibility.Visible,"moving focus after a failed address does not discard the typed path");
            evidence.Capture((FrameworkElement)main.Content,"files-address-error",new{theme="Light",fixtureOnly=true,typedPath="/missing/typed-path"});
            address.Text="/correct";Complete(files.SubmitAddressAsync());Layout(main);
            Assert(model.CurrentPath=="/correct"&&address.Visibility==Visibility.Collapsed,"correcting the same address field and submitting navigates successfully");
            Invoke(files,"EditAddress");address.Text="/discard";SendKey(address,Key.Escape);Layout(main);
            Assert(address.Text=="/correct"&&address.Visibility==Visibility.Collapsed&&model.CurrentPath=="/correct","address Escape restores the last successful folder");
            // A slow older response deliberately ignores its cancellation, like a late vendor completion.
            operations.SlowListGate=NewGate();var older=files.NavigateAsync("/slow");Pump();Complete(files.NavigateAsync("/srv/fixture"));operations.SlowListGate.SetResult();Complete(older);Layout(main);
            Assert(model.CurrentPath=="/srv/fixture"&&model.Entries.Any(row=>row.Name=="fixed-after-conflict.txt"),"late navigation response cannot replace a newer committed folder");
            int preFailure=model.Entries.Count;operations.FailNextCreate=true;Complete(InvokeTask(files,"Create",false));Layout(main);
            Assert(model.Entries.Count==preFailure&&!model.IsMutating&&model.Message.Length>0&&!model.Entries.Any(row=>row.Edit?.IsActive==true),"create failure returns an inline error without inserting an unconfirmed object");
            operations.CreateGate=NewGate();int before=model.Entries.Count;var creating=InvokeTask(files,"Create",true);Pump();var duplicateCreate=InvokeTask(files,"Create",true);Complete(duplicateCreate);
            Assert(operations.CreateCalls==2&&model.IsMutating&&model.Entries.Count==before&&!model.Entries.Any(row=>row.Edit?.IsActive==true),"create waits for the exclusive server result without a phantom item and suppresses duplicate invocation");
            operations.CreateGate.SetResult();Complete(creating);Layout(main);var created=model.Entries.Single(row=>row.Edit?.IsActive==true);editor=Editor(files,created);
            Assert(operations.Exists(created.Entry.FullPath)&&created.Entry.IsDirectory&&editor.Text==created.Name,"successful create selects the confirmed directory and opens its actual inline editor");
            evidence.Capture((FrameworkElement)main.Content,"files-created-folder",new{theme="Light",fixtureOnly=true,serverConfirmed=true,remoteOperations="controlled boundary"});
            SendKey(editor,Key.Escape);Layout(main);Assert(operations.Exists(created.Entry.FullPath)&&created.Edit is null,"Escape after successful creation keeps the created remote object");
            // Reuse the same name presenter in the virtualized tile template.
            operations.AddMany(10000);Complete(files.RefreshAsync());Invoke(files,"SetMode","Medium",false);Layout(main);
            var dotfile=model.Entries.Single(row=>row.Name==".bashrc");int dotfileIndex=model.VisibleEntries.ToList().IndexOf(dotfile);bool initiallyRealized=tiles.ItemContainerGenerator.ContainerFromItem(dotfile)is not null;
            tiles.SelectedItem=dotfile;tiles.ScrollIntoView(dotfile);Layout(main);SendKey(tiles,Key.F2);Layout(main);
            var tilePanel=Descendants<VirtualizingWrapPanel>(tiles).Single();var matchedPresenters=Descendants<FileNamePresenter>(tiles).Where(presenter=>ReferenceEquals(presenter.DataContext,dotfile)).ToArray();
            var dotContainer=tiles.ItemContainerGenerator.ContainerFromItem(dotfile)as ListBoxItem;bool belongsToCurrentView=dotContainer is not null&&tiles.IsAncestorOf(dotContainer);
            var tileDiagnostic=new{theme="Light",fixtureOnly=true,visibleContainsItem=model.VisibleEntries.Contains(dotfile),visibleCount=model.VisibleEntries.Count,dotfileIndex,initiallyRealized,selectedPaths=model.SelectedPaths.ToArray(),editActive=dotfile.Edit?.IsActive,canRename=model.CanRename,realizedContainers=Descendants<ListBoxItem>(tiles).Count(),realizedPresenters=Descendants<FileNamePresenter>(tiles).Count(),matchedPresenters=matchedPresenters.Length,matchedVisibleEditors=matchedPresenters.SelectMany(Descendants<TextBox>).Count(input=>input.IsVisible),panelOffset=tilePanel.VerticalOffset,panelViewport=tilePanel.ViewportHeight,panelExtent=tilePanel.ExtentHeight,generatedContainerExists=dotContainer is not null,belongsToCurrentView,generatedContainerParent=dotContainer is null?null:VisualTreeHelper.GetParent(dotContainer)?.GetType().Name,generatedContainerHasPresentationSource=dotContainer is not null&&PresentationSource.FromVisual(dotContainer)is not null,currentPanelChildCount=VisualTreeHelper.GetChildrenCount(tilePanel),currentPanelHasPresentationSource=PresentationSource.FromVisual(tilePanel)is not null};
            Console.WriteLine("DIAGNOSTIC tile editor "+System.Text.Json.JsonSerializer.Serialize(tileDiagnostic));evidence.Capture((FrameworkElement)main.Content,"files-tiles-before-editor",tileDiagnostic);
            var clipped=!belongsToCurrentView?Rect.Empty:Rect.Intersect(new Rect(tiles.RenderSize),dotContainer!.TransformToAncestor(tiles).TransformBounds(new Rect(dotContainer.RenderSize)));
            Assert(dotfileIndex>1000&&belongsToCurrentView&&!clipped.IsEmpty&&clipped.Width>0&&clipped.Height>0&&Descendants<ListBoxItem>(tiles).Count()<250,"ScrollIntoView realizes and displays the offscreen selected tile in a ten-thousand-item list while keeping containers bounded");
            editor=Editor(files,dotfile);
            Assert(editor.IsVisible&&editor.SelectionLength==dotfile.Name.Length,"tile F2 uses a real editor and selects the complete dotfile name");
            evidence.Capture((FrameworkElement)main.Content,"files-inline-tiles",new{theme="Light",fixtureOnly=true,view="Medium",sourceName=dotfile.Name});
            editor.Text=".profile-new";SendKey(editor,Key.Escape);Layout(main);Assert(operations.Exists("/srv/fixture/.bashrc")&&!operations.Exists("/srv/fixture/.profile-new"),"tile Escape preserves source and does not submit the edited name");
            GroupPressCase(files,tiles,evidence,Assert,"tiles",true);tiles.SelectedItem=dotfile;tiles.ScrollIntoView(dotfile);Layout(main);
            int listCalls=operations.ListCalls;var context=(ContextMenu)Result(files,"BuildContextMenu",true)!;Ui.OpenContextMenu(tiles,context);Pump();
            var strip=context.Items.OfType<FileContextCommandStrip>().Single();var stripButtons=Descendants<Button>(strip).ToArray();
            Assert(stripButtons.Length==5&&stripButtons.Count(button=>AutomationProperties.GetName(button).Contains("Переименовать",StringComparison.Ordinal))==1&&operations.ListCalls==listCalls,"selected tile context presents the real five-action command group without a directory request");
            evidence.Capture(context,"files-context-selected",new{theme="Light",fixtureOnly=true,selectedView="Medium",selectedName=dotfile.Name});
            stripButtons.Single(button=>AutomationProperties.GetName(button).StartsWith("Переименовать",StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(main);editor=Editor(files,dotfile);
            Assert(dotfile.Edit?.SourcePath==dotfile.Entry.FullPath,"actual context-group rename acts on the current tile selection");SendKey(editor,Key.Escape);context.IsOpen=false;
            var emptyData=new DataObject();var textData=new DataObject();textData.SetText("synthetic text");var localData=new DataObject();localData.SetData(DataFormats.FileDrop,new[]{typeof(FileInteractionTests).Assembly.Location},false);
            Assert(!files.CanPasteData(emptyData)&&!files.CanPasteData(textData)&&files.CanPasteData(localData),"paste availability distinguishes local file data from empty and plain-text DataObjects without writing the OS clipboard");
            files.SetAvailability(false);Assert(!files.CanPasteData(localData),"unavailable destination disables paste even when local file data is present");files.SetAvailability(true);
            var emptyMenu=(ContextMenu)Result(files,"BuildContextMenu",false)!;
            Assert(emptyMenu.Items.OfType<MenuItem>().Single(item=>item.InputGestureText=="Ctrl+Z").IsEnabled==false,"unsupported undo remains disabled rather than suggesting unrelated reversal");
            Complete(InvokeTask(files,"TogglePin",files.CurrentPath));var pinnedMenu=(ContextMenu)Result(files,"BuildContextMenu",true)!;
            Assert(pinnedMenu.Items.OfType<MenuItem>().Any(item=>Equals(item.Header,"Открепить папку"))&&!pinnedMenu.Items.OfType<MenuItem>().Any(item=>Equals(item.Header,"Закрепить / открепить папку")),"current folder pin action is rebuilt as explicit Unpin after pinning");Complete(InvokeTask(files,"TogglePin",files.CurrentPath));
            var viewMenu=(ContextMenu)Result(files,"BuildViewMenu")!;var modes=viewMenu.Items.OfType<MenuItem>().Take(8).ToArray();
            Assert(modes.Length==8&&modes.Select(item=>(item.Icon as TextBlock)?.Text).Distinct(StringComparer.Ordinal).Count()==8&&modes.Count(item=>item.IsChecked)==1,"all eight file view modes expose distinct glyphs and one current mode");
            var commandBar=Descendants<ResponsiveCommandBar>(files.CommandBar).Single();commandBar.Width=190;Layout(main);var overflow=commandBar.CreateOverflowMenu();
            var hiddenRename=overflow.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Переименовать"));
            Assert(commandBar.OverflowCommandKeys.Contains("Rename")&&hiddenRename.IsEnabled&&commandBar.VisibleCommandKeys.Contains("Create"),"constrained single-row toolbar keeps high-priority creation and exposes enabled rename in overflow");
            var overflowButton=Descendants<Button>(commandBar).Single(button=>button.Name=="CommandOverflowButton");Ui.OpenFlyout(overflowButton,overflow);Pump();evidence.Capture(overflow,"files-overflow-menu",new{theme="Light",fixtureOnly=true,barWidthDip=commandBar.ActualWidth,visibleKeys=commandBar.VisibleCommandKeys,overflowKeys=commandBar.OverflowCommandKeys});
            hiddenRename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();overflow.IsOpen=false;Layout(main);
            Assert(dotfile.Edit?.IsActive==true&&Editor(files,dotfile).IsVisible,"overflow rename invokes the same inline action rather than a second dialog");files.CancelNameEdit();commandBar.Width=double.NaN;Layout(main);
            // A large model exercises the production lasso geometry without realizing all containers.
            Complete(files.RefreshAsync());Layout(main);var tileScroller=Descendants<ScrollViewer>(tiles).First();tileScroller.ScrollToVerticalOffset(0);Layout(main);
            using(var pointer=new CiSoftwarePointer(main))
            {
            var start=new Point(1,1);var startHit=tiles.InputHitTest(start)as DependencyObject;if(startHit is null){start=new Point(Math.Min(90,tiles.ActualWidth/2),1);startHit=tiles.InputHitTest(start)as DependencyObject;}if(startHit is null||FileBrowserView.IsFileObjectHit(startHit))throw new InvalidOperationException("CI tile lasso start is not a real blank-space input target.");
            int realizedBefore=Descendants<ListBoxItem>(tiles).Count();pointer.MoveTo(tiles,start);pointer.Down();pointer.MoveTo(tiles,new Point(Math.Min(tiles.ActualWidth-3,220),Math.Min(tiles.ActualHeight-3,250)));Layout(main);
            var lassoDiagnostic=new{theme="Light",fixtureOnly=true,captureIncludesActualAdorner=true,measurementsAtRequestBeforeSettle=true,inputLevel="software SendInput mouse",physicalMouseTested=false,actualLeftButton=Mouse.LeftButton.ToString(),ownerMatches=ReferenceEquals(Field(files,"lassoOwner"),tiles),lassoOwnerType=Field(files,"lassoOwner")?.GetType().Name,lastEndReason=files.LastLassoEndReason,hasAdorner=Field(files,"lassoAdorner")is not null,hasLayer=Field(files,"lassoLayer")is not null,hasTimer=Field(files,"lassoTimer")is not null,discoveredAdornerLayer=AdornerLayer.GetAdornerLayer(tiles)is not null,mouseCaptured=tiles.IsMouseCaptured,mouseCaptureWithin=tiles.IsMouseCaptureWithin,capturedType=Mouse.Captured?.GetType().Name,selectedCount=tiles.SelectedItems.Count,realizedBefore,realizedAfter=Descendants<ListBoxItem>(tiles).Count(),panelOffset=tilePanel.VerticalOffset,width=tiles.ActualWidth,height=tiles.ActualHeight,panelWidth=tilePanel.ViewportWidth,panelHeight=tilePanel.ViewportHeight};
            Console.WriteLine("DIAGNOSTIC tile lasso "+System.Text.Json.JsonSerializer.Serialize(lassoDiagnostic));evidence.Capture(LassoCaptureVisual(files),"files-lasso-before-assert",lassoDiagnostic);
            Assert(Field(files,"lassoOwner")==tiles&&tiles.SelectedItems.Count>0&&realizedBefore is >0 and <250&&Descendants<ListBoxItem>(tiles).Count() is >0 and <250,"tile lasso selects model items while keeping a ten-thousand-item listing virtualized");
            evidence.Capture(LassoCaptureVisual(files),"files-lasso-tiles",new{theme="Light",fixtureOnly=true,captureIncludesActualAdorner=true,measurementsAtRequestBeforeSettle=true,totalItems=model.VisibleEntries.Count,realizedContainers=Descendants<ListBoxItem>(tiles).Count(),selectedItems=tiles.SelectedItems.Count,inputLevel="software SendInput mouse",physicalMouseTested=false});
            tiles.ReleaseMouseCapture();Pump();Assert(Field(files,"lassoOwner") is null&&Field(files,"lassoAdorner") is null,"lost mouse capture removes tile lasso state and its adorner");
            }
            Invoke(files,"SetMode","Details",false);Layout(main);var scroller=Descendants<ScrollViewer>(table).First();scroller.ScrollToVerticalOffset(0);Layout(main);
            Point origin;
            using(var pointer=new CiSoftwarePointer(main))
            {
            scroller.ScrollToVerticalOffset(0);Layout(main);origin=BlankNamePoint(table);
            if(FileBrowserView.IsFileObjectHit(table.InputHitTest(origin)as DependencyObject))throw new InvalidOperationException("CI Details lasso start is not blank Name-cell space.");
            pointer.MoveTo(table,origin);pointer.Down();pointer.MoveTo(table,new Point(table.ActualWidth-8,table.ActualHeight-2));WaitFor(()=>scroller.VerticalOffset>0);Layout(main);
            Assert(table.SelectedItems.Count>0&&Descendants<DataGridRow>(table).Count() is >0 and <250,"Details lasso edge scrolling selects rows across the virtualized model without realizing every row");
            evidence.Capture(LassoCaptureVisual(files),"files-lasso-details",new{theme="Light",fixtureOnly=true,captureIncludesActualAdorner=true,measurementsAtRequestBeforeSettle=true,inputLevel="software SendInput mouse",physicalMouseTested=false,selectedItems=table.SelectedItems.Count,scrollOffset=scroller.VerticalOffset,realizedRows=Descendants<DataGridRow>(table).Count()});
            SendKey(table,Key.Escape);Pump();Assert(Field(files,"lassoOwner") is null&&Field(files,"lassoTimer") is null,"Escape clears Details lasso capture and edge-scroll timer");
            }
            using(var pointer=new CiSoftwarePointer(main))
            {
            scroller.ScrollToVerticalOffset(0);Layout(main);origin=BlankNamePoint(table);pointer.MoveTo(table,origin);pointer.Down();pointer.MoveTo(table,new Point(Math.Max(2,origin.X-100),origin.Y+40));
            if(!ReferenceEquals(Field(files,"lassoOwner"),table))throw new InvalidOperationException("CI section-leave lasso did not arm before hiding the file view.");
            view.ShowSection("processes");Layout(main);
            Assert(Field(files,"lassoOwner") is null&&Field(files,"lassoAdorner") is null,"leaving the file section clears selection capture and adorner");
            }
            view.ShowSection("files");Layout(main);
            bool closed=false;main.Closed+=(_,_)=>closed=true;main.Close();WaitFor(()=>closed&&!app.Windows.Cast<Window>().Contains(main));app.Shutdown();
            Console.WriteLine($"RESULT: {checks} actual file interaction checks passed; CI-only HWNDs; controlled operations; no SSH, clipboard writes or process signals");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static void GroupPressCase(FileBrowserView files,Control owner,CiVisualEvidence evidence,Action<bool,string> assert,string mode,bool checkModifiers)
    {
        var host=Window.GetWindow(files)??throw new InvalidOperationException("CI group input requires its owned window.");using var pointer=new CiSoftwarePointer(host);
        var model=(FileBrowserViewModel)files.DataContext;
        IEnumerable<FrameworkElement> containers=owner is DataGrid grid?Descendants<DataGridRow>(grid):Descendants<ListBoxItem>(owner);
        var live=containers.Where(item=>item.IsVisible&&item.ActualWidth>0&&item.ActualHeight>0&&item.DataContext is FileItemViewModel).Where(item=>{var bounds=item.TransformToAncestor(owner).TransformBounds(new Rect(item.RenderSize));if(bounds.Top<0||bounds.Bottom>owner.ActualHeight||bounds.Right<=0||bounds.Left>=owner.ActualWidth)return false;var image=Descendants<Image>(item).FirstOrDefault(icon=>icon.IsVisible&&icon.ActualWidth>0&&icon.ActualHeight>0);if(image is null)return false;var point=image.TranslatePoint(new Point(image.ActualWidth/2,image.ActualHeight/2),owner);var hit=owner.InputHitTest(point)as DependencyObject;return hit is not null&&FileBrowserView.IsFileObjectHit(hit)&&item.IsAncestorOf(hit);}).Take(2).ToArray();
        if(live.Length!=2)throw new InvalidOperationException("Group press requires two real visible "+mode+" containers.");
        var rows=live.Select(item=>item.DataContext as FileItemViewModel??throw new InvalidOperationException("Visible group row lost its file identity.")).ToArray();
        void SelectPair(){if(owner is DataGrid table){table.UnselectAll();foreach(var row in rows)table.SelectedItems.Add(row);}else{var tiles=(ListBox)owner;tiles.UnselectAll();foreach(var row in rows)tiles.SelectedItems.Add(row);}}
        SelectPair();var expected=rows.Select(row=>row.Entry.FullPath).ToHashSet(StringComparer.Ordinal);var target=Descendants<Image>(live[0]).First(image=>image.IsVisible&&image.ActualWidth>0&&image.ActualHeight>0);
        pointer.MoveTo(target,new Point(target.ActualWidth/2,target.ActualHeight/2));var actualHit=owner.InputHitTest(Mouse.GetPosition(owner))as DependencyObject;if(actualHit is null||!FileBrowserView.IsFileObjectHit(actualHit)||!live[0].IsAncestorOf(actualHit))throw new InvalidOperationException("CI software pointer did not hit the chosen visible file row.");pointer.Down();bool pressArmed=ReferenceEquals(Field(files,"pressedGroupOwner"),owner)&&owner.IsMouseCaptureWithin;
        int downCount=model.SelectedPaths.Count;bool frozenMatches=files.CaptureDragEntries().Select(entry=>entry.FullPath).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
        pointer.Up();Pump();
        bool singleAfterUp=model.SelectedPaths.SequenceEqual(new[]{rows[0].Entry.FullPath});
        Console.WriteLine("DIAGNOSTIC software group "+System.Text.Json.JsonSerializer.Serialize(new{mode,pressArmed,downCount,frozenMatches,singleAfterUp,selectedPaths=model.SelectedPaths}));
        if(!(pressArmed&&downCount==2&&frozenMatches&&singleAfterUp))evidence.Capture((FrameworkElement)host.Content,"files-group-press-failure-"+mode,new{theme="Light",fixtureOnly=true,pressArmed,downCount,frozenMatches,singleAfterUp,selectedPaths=model.SelectedPaths,inputLevel="software SendInput mouse",physicalInput=false,oleStarted=false});
        assert(pressArmed&&downCount==2&&frozenMatches&&singleAfterUp,mode+" software OS selected-item press preserves the full drag group through native mouse-down and plain mouse-up selects the clicked item");
        if(checkModifiers)
        {
            SelectPair();bool ctrl=files.BeginSelectedGroupPress(owner,rows[0],ModifierKeys.Control),shift=files.BeginSelectedGroupPress(owner,rows[0],ModifierKeys.Shift);
            assert(!ctrl&&!shift&&!owner.IsMouseCaptureWithin&&model.SelectedPaths.ToHashSet(StringComparer.Ordinal).SetEquals(expected)&&files.CaptureDragEntries().Select(entry=>entry.FullPath).ToHashSet(StringComparer.Ordinal).SetEquals(expected),"Ctrl and Shift group presses remain on the native selection path without arming the plain-press drag snapshot");
        }
    }
    static Point BlankNamePoint(DataGrid table)
    {
        var row=Descendants<DataGridRow>(table).First(item=>item.IsVisible&&item.GetIndex()==0);var cell=Descendants<DataGridCell>(row).Single(item=>item.Column.SortMemberPath=="Name");return cell.TranslatePoint(new Point(cell.ActualWidth-8,cell.ActualHeight/2),table);
    }
    static FrameworkElement LassoCaptureVisual(FileBrowserView files)
    {
        var adorner=Field(files,"lassoAdorner")as Adorner??throw new InvalidOperationException("Lasso capture requires the active production adorner.");
        var layer=Field(files,"lassoLayer")as AdornerLayer??throw new InvalidOperationException("Lasso capture requires its production adorner layer.");
        if(!adorner.IsVisible||layer.GetAdorners(adorner.AdornedElement)?.Contains(adorner)!=true)throw new InvalidOperationException("The active production lasso is not attached to its visible layer.");
        // Window's AdornerLayer is a sibling of its content. Rendering only
        // MainWindow.Content omits the real frame even while capture is active.
        for(DependencyObject? visual=files;visual is not null;visual=VisualTreeHelper.GetParent(visual))
            if(visual is AdornerDecorator decorator&&decorator.IsAncestorOf(layer)&&decorator.IsAncestorOf(adorner))return decorator;
        throw new InvalidOperationException("No actual AdornerDecorator subtree contains the active production lasso.");
    }
    sealed class Operations:IFileBrowserOperations
    {
        readonly Dictionary<string,RemoteEntry> entries=CompositionFixtures.Files().ToDictionary(row=>row.FullPath,StringComparer.Ordinal);
        public bool Available=>true;
        public TaskCompletionSource? RenameGate,CreateGate,SlowListGate;
        public readonly List<(string Source,string Destination)> Renames=[];
        public int CreateCalls,ListCalls;public bool FailNextCreate,FailNextList;
        public bool Exists(string path)=>entries.ContainsKey(path);
        public Task<string> ResolvePathAsync(string path,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(path);}
        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path,CancellationToken ct)
        {
            ListCalls++;
            if(FailNextList){FailNextList=false;throw new IOException("Controlled directory listing failed after rename.");}
            if(path=="/slow"){await SlowListGate!.Task;return [];}
            ct.ThrowIfCancellationRequested();if(path=="/correct")return [];
            if(path!="/srv/fixture")throw new DirectoryNotFoundException("Controlled fixture has no such folder.");
            return entries.Values.ToArray();
        }
        public async Task RenameAsync(string source,string destination,CancellationToken ct)
        {
            Renames.Add((source,destination));if(RenameGate is{Task.IsCompleted:false} gate)await gate.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();if(entries.ContainsKey(destination))throw new IOException("The target name already exists.");
            var old=entries[source];entries.Remove(source);entries.Add(destination,Copy(old,PosixPath.Name(destination),destination));
        }
        public async Task<string> CreateUniqueAsync(string parent,bool directory,CancellationToken ct)
        {
            CreateCalls++;if(FailNextCreate){FailNextCreate=false;throw new IOException("Controlled create denied.");}if(CreateGate is{} gate)await gate.Task.WaitAsync(ct);ct.ThrowIfCancellationRequested();
            string name=directory?"Новая папка":"Новый файл.txt";int number=1;while(entries.ContainsKey(PosixPath.Combine(parent,name)))name=(directory?"Новая папка":"Новый файл")+" ("+(number++)+ ")"+(directory?"":".txt");
            var path=PosixPath.Combine(parent,name);entries.Add(path,new RemoteEntry{Name=name,FullPath=path,IsDirectory=directory,IsRegularFile=!directory});return path;
        }
        public void AddMany(int count){for(int i=0;i<count;i++){string name=$"virtualized-{i:00000}.txt",path="/srv/fixture/"+name;entries.TryAdd(path,new RemoteEntry{Name=name,FullPath=path,IsRegularFile=true,Length=i});}}
        static RemoteEntry Copy(RemoteEntry old,string name,string path)=>new(){Name=name,FullPath=path,IsDirectory=old.IsDirectory,IsSymbolicLink=old.IsSymbolicLink,IsRegularFile=old.IsRegularFile,Length=old.Length,LastWriteTimeUtc=old.LastWriteTimeUtc};
    }
    static TextBox Editor(FileBrowserView files,FileItemViewModel row)=>Descendants<FileNamePresenter>(files).Where(presenter=>ReferenceEquals(presenter.DataContext,row)).SelectMany(Descendants<TextBox>).Single(editor=>editor.IsVisible);
    static void SendKey(UIElement target,Key key){var source=PresentationSource.FromVisual(target)??throw new InvalidOperationException("Key event requires the real CI presentation source.");target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();}
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static void Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static object? Result(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static Task InvokeTask(object target,string name,params object?[] args)=>(Task)target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args)!;
    static TaskCompletionSource NewGate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    static void Complete(Task task){WaitFor(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static void Layout(Window window){window.UpdateLayout();Pump();window.UpdateLayout();}
    static void WaitFor(Func<bool> done){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Controlled file interaction did not settle.");Pump();}}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    static string? Value(string[] args,string key){int i=Array.IndexOf(args,key);return i<0?null:args[i+1];}
    static void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);Console.WriteLine("PASS "+label);}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
