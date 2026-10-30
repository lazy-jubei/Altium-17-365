using Altium.Auth;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace Altium17Cloud
{
    internal sealed class WorkspaceView : UserControl, IDisposable
    {
        private readonly HttpClient apiHttp = CreateHttp(TimeSpan.FromSeconds(45));
        private readonly HttpClient authHttp = CreateHttp(Timeout.InfiniteTimeSpan);
        private readonly WorkspaceSession session;
        private readonly ObservableCollection<CloudProject> projects = new ObservableCollection<CloudProject>();
        private readonly ICollectionView projectView;
        private readonly TextBox projectSearch = new TextBox { ToolTip = "Search project names and descriptions" };
        private readonly TextBlock projectCount = Label("0 projects found");
        private readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private string[] projectTerms = Array.Empty<string>();
        private readonly TextBox workspaceUrl = new TextBox();
        private readonly PasswordBox accessToken = new PasswordBox();
        private readonly ComboBox workspaces = new ComboBox { DisplayMemberPath = "Name", MinWidth = 180 };
        private readonly ComboBox region = new ComboBox { ItemsSource = WorkspaceApi.Regions, DisplayMemberPath = "Key", SelectedValuePath = "Value", SelectedIndex = 0 };
        private readonly TextBlock connection = Label("Not signed in"), status = Label("Connect to browse your workspace projects.");
        private readonly TextBlock workspaceStatus = Label("Status: Disconnected");
        private readonly TextBlock signInPrompt = Label("Sign in to Altium 365 in your browser, then choose a workspace.");
        private readonly Button cancel = Button("Cancel"), signIn = Button("Sign in"), connect = Button("Connect"), refresh = Button("Refresh"), logout = Button("Log out"), open = Button("Open workspace");
        private readonly TabControl tabs = new TabControl();
        private readonly CheckBox followProject = new CheckBox { Content = "Synchronize with server", ToolTip = "Detect the open project's Altium 365 working copy automatically. Choose Save to Server to upload changes.", IsChecked = true, Margin = new Thickness(0, 4, 0, 6) };
        private readonly TextBlock activeProject = Label("Open a project in Altium to detect its working copy.");
        private readonly Expander manualSetup = new Expander { Header = "Manual setup" };
        private string activeProjectFile = "\0";
        private bool contextPending, settingWorkingCopy;
        private readonly TextBox gitRemote = new TextBox(), gitFolder = new TextBox(), gitExecutable = new TextBox(), gitReference = new TextBox(), commitMessage = new TextBox();
        private readonly PasswordBox gitToken = new PasswordBox();
        private readonly TextBlock gitSummary = Label("Choose a project or enter its repository URL, then clone or select a working copy.");
        private readonly Button useProject = Button("Use selected project"), clone = Button("Clone"), gitRefresh = Button("Refresh status"), pull = Button("Pull changes…"), push = Button("Push changes"), commit = Button("Commit selected"), openProject = Button("Open project");
        private readonly Button fetch = Button("Fetch now");
        private readonly TextBlock fetchNotice = Label("Background fetch checks open projects every two minutes when connected.");
        private readonly BackgroundFetchSchedule fetchSchedule = new BackgroundFetchSchedule();
        private DateTime lastFetchScan = DateTime.MinValue;
        private readonly Dictionary<string, string[]> fetchWarnings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> serverNotices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> fetchErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string fetchScanError;
        private DateTime lastFetchCompleted = DateTime.MinValue;
        private int lastFetchCount;
        private readonly Button createProject = Button("Create project…");
        private readonly Button saveToServer = Button("Save to Server…");
        private readonly ObservableCollection<GitChange> changes = new ObservableCollection<GitChange>();
        private readonly DataGrid changesGrid = new DataGrid();
        private readonly Button discard = Button("Discard changes…");
        private readonly Action<string> openProjectAction;
        private readonly WorkspaceDocuments documents;
        private readonly DispatcherTimer monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private FileSystemWatcher watcher;
        private string monitorFolder;
        private CancellationTokenSource monitorCancellation;
        private Task monitorTask;
        private int notificationQueued;
        private bool statusRequested, monitoring;
        private int unsavedDocuments;
        private bool editorStateAvailable = true;
        private DateTime lastStatusRead = DateTime.MinValue;
        internal string WorkingFolder => gitFolder.Text;
        internal bool IsBusy => operation != null;
        internal bool HasServerChanges
        {
            get { DetectActiveProject(); ReadEditorState(); return !contextPending && !disposed && editorStateAvailable && workingStatus?.Branch == "master" && (unsavedDocuments > 0 || workingStatus.HasPendingSave); }
        }
        private DataGrid projectGrid;
        private CloudProject gitProject;
        private Workspace gitWorkspace;
        private GitStatus workingStatus;
        private readonly List<Control> busyControls = new List<Control>();
        private readonly string settingsPath;
        private readonly SignInStore signInStore;
        private bool signInRestoreStarted;
        private bool reconnectOnStartup = true;
        private CancellationTokenSource operation;
        private bool disposed;
        private bool preparingClone;
        private readonly ProgressBar cloneProgress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 14, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 6) };
        private static HttpClient CreateHttp(TimeSpan timeout) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12 }) { Timeout = timeout };
        private static TextBlock Label(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private static Button Button(string text) => new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 4) };
        internal WorkspaceView(Action<string> openProjectAction = null, WorkspaceDocuments documents = null, string settingsPath = null)
        {
            this.openProjectAction = openProjectAction;
            this.documents = documents;
            this.settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium17365", "settings.json");
            signInStore = new SignInStore(Path.Combine(Path.GetDirectoryName(this.settingsPath), "sign-in.dat"));
            session = new WorkspaceSession(new WorkspaceApi(apiHttp), authHttp);
            projectView = CollectionViewSource.GetDefaultView(projects);
            projectView.Filter = item => item is CloudProject project && projectTerms.All(term =>
                (project.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (project.Description ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Background = Brushes.White; Foreground = Brushes.Black;
            var layout = new DockPanel { Margin = new Thickness(10) };
            connection.FontWeight = FontWeights.SemiBold;
            var header = new DockPanel();
            DockPanel.SetDock(logout, Dock.Right); header.Children.Add(logout); header.Children.Add(connection);
            DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            logout.Click += async (_, __) => await RunAsync(_ => { Disconnect(true); return Task.CompletedTask; }, "Logging out…", needsWorkingCopy: false);
            var footer = new StackPanel(); footer.Children.Add(fetchNotice); footer.Children.Add(status); footer.Children.Add(cloneProgress); footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            layout.Children.Add(tabs); Content = layout;
            cancel.Visibility = Visibility.Collapsed; cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Click += (_, __) => operation?.Cancel();
            tabs.Items.Add(new TabItem { Header = "Connection", Content = BuildConnection() });
            tabs.Items.Add(new TabItem { Header = "Projects", Content = BuildProjects() });
            tabs.Items.Add(new TabItem { Header = "Git", Content = BuildGit() });
            gitToken.PasswordChanged += (_, __) => UpdateState();
            accessToken.PasswordChanged += (_, __) => UpdateState();
            LoadSettings(); manualSetup.IsExpanded = followProject.IsChecked != true;
            if (followProject.IsChecked != true) activeProject.Text = "Manual working copy";
            ClearWorkingStatus(); DetectActiveProject(); UpdateState();
            monitorTimer.Tick += async (_, __) =>
            {
                DetectActiveProject();
                if (!monitoring) { monitorTask = MonitorAsync(); await monitorTask; }
            };
            monitorTimer.Start();
            Loaded += async (_, __) => await RestoreSignInAsync();
        }
        private UIElement BuildConnection()
        {
            var stack = new StackPanel { Margin = new Thickness(10) };
            stack.Children.Add(signInPrompt);
            stack.Children.Add(signIn);
            stack.Children.Add(Label("Workspace")); stack.Children.Add(workspaces); stack.Children.Add(workspaceStatus); stack.Children.Add(connect);
            workspaces.SelectionChanged += (_, __) => UpdateState();
            signIn.Click += async (_, __) => await RunAsync(async ct =>
            {
                var available = await session.SignInAsync((string)region.SelectedValue, OpenBrowser, ct);
                RememberSignIn();
                projects.Clear(); UpdateProjectCount(); gitProject = null; gitWorkspace = null; gitToken.Clear(); accessToken.Clear();
                workspaces.ItemsSource = available;
                var selected = available.FirstOrDefault(w => w.Url.AbsoluteUri == workspaceUrl.Text) ?? (available.Count == 1 ? available[0] : null);
                workspaces.SelectedItem = selected;
                if (selected != null) await ConnectWorkspaceAsync(selected, ct);
                else status.Text = available.Count == 0 ? "No workspaces available for this account." : "Choose a workspace and connect.";
            }, "Complete sign-in in your browser…", needsWorkingCopy: false);
            connect.Click += async (_, __) =>
            {
                if (session.Connected != null)
                    await RunAsync(_ => { Disconnect(false); return Task.CompletedTask; }, "Disconnecting…", needsWorkingCopy: false);
                else await RunAsync(ct => ConnectWorkspaceAsync((Workspace)workspaces.SelectedItem, ct), "Connecting to workspace…", needsWorkingCopy: false);
            };
            var advanced = new StackPanel();
            advanced.Children.Add(Label("Discovery region")); advanced.Children.Add(region);
            advanced.Children.Add(Label("Workspace address")); advanced.Children.Add(workspaceUrl);
            advanced.Children.Add(Label("Workspace API access token")); advanced.Children.Add(accessToken);
            var tokenConnect = Button("Connect with token"); advanced.Children.Add(tokenConnect);
            tokenConnect.Click += async (_, __) => await RunAsync(async ct =>
            {
                var url = WorkspaceApi.WorkspaceUrl(workspaceUrl.Text);
                string token = accessToken.Password; accessToken.Clear();
                var page = await session.ConnectAsync(new Workspace { Name = url.Host.Split('.')[0], Url = url }, token, ct);
                reconnectOnStartup = true;
                UpdateState();
                tabs.SelectedIndex = 1; await ShowProjectsAsync(page, ct);
            }, "Connecting to workspace…", needsWorkingCopy: false);
            stack.Children.Add(new Expander { Header = "Advanced connection", Content = advanced, Margin = new Thickness(0, 18, 0, 0) });
            busyControls.AddRange(new Control[] { signIn, workspaces, region, workspaceUrl, accessToken, tokenConnect });
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        private async Task ConnectWorkspaceAsync(Workspace workspace, CancellationToken cancellation)
        {
            status.Text = "Connecting to workspace…";
            var page = await session.ConnectAsync(workspace, null, cancellation);
            reconnectOnStartup = true;
            workspaceUrl.Text = session.Connected.Url.AbsoluteUri;
            RememberSignIn(); SaveSettings();
            UpdateState();
            tabs.SelectedIndex = 1;
            await ShowProjectsAsync(page, cancellation);
            if (followProject.IsChecked == true && monitorFolder != null) tabs.SelectedIndex = 2;
        }
        private async Task RestoreSignInAsync()
        {
            if (signInRestoreStarted || disposed || session.SignedIn || IsBusy) return;
            signInRestoreStarted = true;
            var saved = signInStore.Read();
            if (saved == null) return;
            reconnectOnStartup = saved.ConnectOnStartup;
            await RunAsync(async ct =>
            {
                region.SelectedValue = saved.Region;
                var available = await session.ResumeAsync(saved.RefreshToken, saved.Region, ct);
                RememberSignIn();
                workspaces.ItemsSource = available;
                var selected = available.FirstOrDefault(w => w.Url.AbsoluteUri == workspaceUrl.Text) ?? (available.Count == 1 ? available[0] : null);
                workspaces.SelectedItem = selected;
                if (saved.ConnectOnStartup && selected != null) await ConnectWorkspaceAsync(selected, ct);
                else status.Text = "Signed in. Choose a workspace and connect.";
            }, "Signing in…", needsWorkingCopy: false);
        }
        private void RememberSignIn()
        {
            if (string.IsNullOrWhiteSpace(session.SignInRefreshToken)) return;
            try { signInStore.Save(new SavedSignIn { RefreshToken = session.SignInRefreshToken, Region = (string)region.SelectedValue, ConnectOnStartup = reconnectOnStartup }); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.Cryptography.CryptographicException)
            { status.Text = "Sign-in could not be remembered on this computer."; }
        }
        private UIElement BuildProjects()
        {
            var layout = new DockPanel { Margin = new Thickness(10) };
            var search = new StackPanel();
            search.Children.Add(Label("Search projects"));
            var searchRow = new DockPanel();
            var clearSearch = Button("Clear");
            DockPanel.SetDock(clearSearch, Dock.Right); searchRow.Children.Add(clearSearch); searchRow.Children.Add(projectSearch);
            search.Children.Add(searchRow); search.Children.Add(projectCount);
            DockPanel.SetDock(search, Dock.Top); layout.Children.Add(search);
            clearSearch.IsEnabled = false;
            clearSearch.Click += (_, __) => { projectSearch.Clear(); ApplyProjectFilter(); projectSearch.Focus(); };
            searchTimer.Tick += (_, __) => ApplyProjectFilter();
            projectSearch.TextChanged += (_, __) =>
            {
                searchTimer.Stop(); searchTimer.Start();
                clearSearch.IsEnabled = projectSearch.Text.Length > 0;
            };
            var buttons = new WrapPanel();
            foreach (var button in new[] { createProject, refresh, useProject, open }) buttons.Children.Add(button);
            DockPanel.SetDock(buttons, Dock.Top); layout.Children.Add(buttons);
            var grid = new DataGrid { ItemsSource = projectView, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
            projectGrid = grid;
            grid.SelectionChanged += (_, __) => UpdateState();
            busyControls.Add(grid);
            grid.Columns.Add(new DataGridTextColumn { Header = "Project", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new System.Windows.Data.Binding("Description"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            layout.Children.Add(grid);
            refresh.Click += async (_, __) => await RunAsync(ct => ShowProjectsAsync(null, ct), "Refreshing projects…");
            createProject.Click += async (_, __) => await RunAsync(async ct =>
            {
                var folders = await session.ProjectFoldersAsync(ct);
                if (folders.Count == 0) throw new InvalidOperationException("No project folders are available in this workspace.");
                CloudProject created;
                using (var dialog = new CreateProjectDialog(folders))
                {
                    var result = documents == null ? dialog.ShowDialog() : dialog.ShowDialog(documents);
                    if (result != System.Windows.Forms.DialogResult.OK) { status.Text = "Project creation cancelled."; return; }
                    status.Text = "Creating project…";
                    created = await session.CreateProjectAsync(dialog.ProjectName, dialog.SelectedFolder, ct);
                }
                // Keep the successful creation visible even if the following list refresh fails.
                projects.Add(created); projectSearch.Clear(); ApplyProjectFilter(); grid.SelectedItem = created;
                try
                {
                    var ready = await session.ProjectFromReferenceAsync(created.ReferenceId, ct);
                    if (ready != null) { projects.Remove(created); projects.Add(ready); grid.SelectedItem = ready; }
                    if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
                    status.Text = !string.IsNullOrEmpty(ready?.RepositoryUrl) ? "Project created. Use selected project to clone its working copy." :
                        "Project created. Refresh the list when Altium 365 finishes preparing its repository.";
                }
                catch (Exception error) when (error is OperationCanceledException || error is HttpRequestException || error is InvalidOperationException)
                { status.Text = "Project created. Refresh the list to load its repository details."; }
                UpdateProjectCount();
            }, "Loading project folders…");
            open.Click += (_, __) => { if (session.Connected != null) TryOpen(session.Connected.Url.AbsoluteUri); };
            var cloneMenu = new MenuItem { Header = "Clone" };
            grid.ContextMenu = new ContextMenu(); grid.ContextMenu.Items.Add(cloneMenu);
            grid.PreviewMouseRightButtonDown += (_, e) =>
            {
                var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
                grid.SelectedItem = row?.Item;
            };
            grid.ContextMenuOpening += (_, e) =>
            {
                cloneMenu.IsEnabled = !IsBusy && grid.SelectedItem is CloudProject selected && projectView.Contains(selected) && !string.IsNullOrEmpty(selected.RepositoryUrl);
                if (!cloneMenu.IsEnabled) e.Handled = true;
            };
            cloneMenu.Click += (_, __) => PrepareClone();
            useProject.Click += (_, __) => PrepareClone();
            return layout;
        }
        private void PrepareClone()
        {
            if (IsBusy || !(projectGrid.SelectedItem is CloudProject project) || !projectView.Contains(project) || string.IsNullOrEmpty(project.RepositoryUrl)) return;
            monitorCancellation?.Cancel(); contextPending = false; preparingClone = true;
            settingWorkingCopy = true;
            try { followProject.IsChecked = true; } finally { settingWorkingCopy = false; }
            string name = project.Name ?? "Project";
            var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();
            name = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") name = "Project";
            if (System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) name = "_" + name;
            if (name.Length > 120) name = name.Substring(0, char.IsHighSurrogate(name[119]) ? 119 : 120).TrimEnd(' ', '.');
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Altium", "Projects");
            string folder = Path.Combine(root, name);
            for (int suffix = 2; Directory.Exists(folder) || File.Exists(folder); suffix++) folder = Path.Combine(root, name + " (" + suffix + ")");
            SetWorkingCopy(project.RepositoryUrl, folder); BindKnownProject();
            activeProject.Text = project.Name; activeProject.ToolTip = null;
            manualSetup.IsExpanded = true; tabs.SelectedIndex = 2;
            status.Text = "Choose the local folder, then Clone. Open the cloned project to synchronize with server.";
            UpdateState();
        }
        private UIElement BuildGit()
        {
            var layout = new DockPanel { Margin = new Thickness(10) };
            var settings = new StackPanel();
            var configuration = new StackPanel();
            settings.Children.Add(followProject); settings.Children.Add(activeProject);
            followProject.Checked += (_, __) =>
            {
                if (settingWorkingCopy) return;
                manualSetup.IsExpanded = false; activeProjectFile = "\0"; DetectActiveProject(); SaveSettings();
            };
            followProject.Unchecked += (_, __) =>
            {
                if (settingWorkingCopy) return;
                preparingClone = false; contextPending = false; monitorCancellation?.Cancel(); manualSetup.IsExpanded = true;
                activeProject.Text = "Manual working copy"; UpdateState(); SaveSettings();
            };
            configuration.Children.Add(Label("For cloning or a different working copy, choose a workspace project or enter its repository below."));
            var direct = new StackPanel();
            direct.Children.Add(Label("Altium 365 repository URL")); direct.Children.Add(gitRemote);
            direct.Children.Add(Label("Workspace token (direct Git only)")); direct.Children.Add(gitToken);
            configuration.Children.Add(new Expander { Header = "Advanced: direct repository", Content = direct });
            configuration.Children.Add(Label("Local working-copy folder")); configuration.Children.Add(gitFolder);
            var chooseFolder = Button("Choose folder…"); configuration.Children.Add(chooseFolder);
            chooseFolder.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose an existing working copy or an empty folder to clone into.", ShowNewFolderButton = true })
                    if ((documents == null ? dialog.ShowDialog() : dialog.ShowDialog(documents)) == System.Windows.Forms.DialogResult.OK) gitFolder.Text = dialog.SelectedPath;
            };
            var advanced = new StackPanel();
            advanced.Children.Add(Label("Git executable (optional; defaults to bundled Git)")); advanced.Children.Add(gitExecutable);
            var chooseGit = Button("Choose git.exe…"); advanced.Children.Add(chooseGit);
            chooseGit.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Git executable|git.exe", CheckFileExists = true })
                    if ((documents == null ? dialog.ShowDialog() : dialog.ShowDialog(documents)) == System.Windows.Forms.DialogResult.OK) gitExecutable.Text = dialog.FileName;
            };
            advanced.Children.Add(Label("Existing checkout to reuse when cloning (optional)")); advanced.Children.Add(gitReference);
            advanced.Children.Add(Label("Reuses downloaded history and creates an independent copy. Local edits in the existing checkout are preserved."));
            configuration.Children.Add(new Expander { Header = "Git setup", Content = advanced });
            manualSetup.Content = new ScrollViewer { Content = configuration, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            settings.Children.Add(manualSetup);
            var buttons = new WrapPanel();
            saveToServer.FontWeight = FontWeights.SemiBold;
            saveToServer.Background = new SolidColorBrush(Color.FromRgb(24, 91, 153)); saveToServer.Foreground = Brushes.White;
            settings.Children.Add(saveToServer);
            clone.HorizontalAlignment = HorizontalAlignment.Stretch; clone.FontWeight = FontWeights.SemiBold;
            clone.Padding = new Thickness(10, 12, 10, 12); settings.Children.Add(clone);
            foreach (var button in new[] { gitRefresh, fetch, pull, push, openProject, discard }) buttons.Children.Add(button);
            settings.Children.Add(buttons); settings.Children.Add(gitSummary);
            DockPanel.SetDock(settings, Dock.Top); layout.Children.Add(settings);
            var commitArea = new StackPanel();
            commitArea.Children.Add(Label("Commit message (optional; uses your configured Git author name and email)")); commitArea.Children.Add(commitMessage); commitArea.Children.Add(commit);
            DockPanel.SetDock(commitArea, Dock.Bottom); layout.Children.Add(commitArea);
            changesGrid.ItemsSource = changes; changesGrid.AutoGenerateColumns = false;
            changesGrid.CanUserAddRows = changesGrid.CanUserDeleteRows = false;
            changesGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
            changesGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Commit", Binding = new System.Windows.Data.Binding("Selected") });
            changesGrid.Columns.Add(new DataGridTextColumn { Header = "Status", IsReadOnly = true, Binding = new System.Windows.Data.Binding("Status"), Width = DataGridLength.Auto });
            changesGrid.Columns.Add(new DataGridTextColumn { Header = "File", IsReadOnly = true, Binding = new System.Windows.Data.Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            layout.Children.Add(changesGrid);
            gitRemote.TextChanged += (_, __) =>
            {
                if (settingWorkingCopy) return;
                preparingClone = false; followProject.IsChecked = false; gitProject = null; gitWorkspace = null; gitToken.Clear(); ClearWorkingStatus();
            };
            gitFolder.TextChanged += (_, __) => { if (!settingWorkingCopy) { if (!preparingClone) followProject.IsChecked = false; ClearWorkingStatus(); } };
            clone.Click += async (_, __) => await RunAsync(async ct =>
            {
                var git = Git();
                cloneProgress.Visibility = Visibility.Visible; cloneProgress.IsIndeterminate = true;
                bool reporting = true;
                var progress = new Progress<GitCloneProgress>(update =>
                {
                    if (!reporting || disposed) return;
                    cloneProgress.IsIndeterminate = false; cloneProgress.Value = update.Percent;
                    status.Text = update.Phase + ": " + update.Percent + "%";
                });
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gitFolder.Text)));
                    await git.CloneAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), gitReference.Text, ct, progress);
                }
                finally { reporting = false; cloneProgress.Visibility = Visibility.Collapsed; }
                await RefreshGitAsync(ct);
                if (workingStatus.IsUnborn && changes.Count == 0 && gitProject != null)
                {
                    NewProjectFiles.Write(gitFolder.Text, gitProject.Name);
                    await RefreshGitAsync(ct);
                }
                SaveSettings(); status.Text = "Project cloned. Open it to begin editing.";
            }, "Cloning project…", TimeSpan.FromHours(1));
            gitRefresh.Click += async (_, __) => await RunAsync(async ct => { await RefreshGitAsync(ct); SaveSettings(); status.Text = "Working-copy status refreshed."; }, "Reading working-copy status…");
            fetch.Click += async (_, __) => await RunAsync(async ct =>
            {
                var result = await Git().FetchAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct);
                RecordFetch(gitFolder.Text, result);
                await RefreshGitAsync(ct); UpdateGitSummary();
                status.Text = "Server changes fetched. Local documents were not replaced.";
            }, "Fetching server changes…");
            pull.Click += async (_, __) => await RunAsync(async ct =>
            {
                if (await PullChangesAsync(ct)) { await RefreshGitAsync(ct); status.Text = "Project changes pulled."; }
            }, "Reviewing server changes…");
            push.Click += async (_, __) => await RunAsync(async ct =>
            {
                if (!await PullChangesAsync(ct)) return;
                await Git().PushAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct);
                await RefreshGitAsync(ct); status.Text = "Commits pushed to the Workspace's master branch.";
            }, "Preparing push…");
            commit.Click += async (_, __) => await RunAsync(async ct =>
            {
                changesGrid.CommitEdit(DataGridEditingUnit.Cell, true); changesGrid.CommitEdit(DataGridEditingUnit.Row, true);
                var selected = changes.Where(c => c.Selected).Select(c => c.Path).ToArray();
                await Git().CommitAsync(gitRemote.Text, gitFolder.Text, selected, commitMessage.Text, ct);
                await RefreshGitAsync(ct); commitMessage.Clear(); status.Text = "Selected files committed locally. Push to send the commits to Altium 365.";
            }, "Committing selected files…");
            saveToServer.Click += async (_, __) => await SaveToServerAsync();
            discard.Click += async (_, __) => await RunAsync(async ct =>
            {
                ReadEditorState();
                if (!editorStateAvailable || unsavedDocuments > 0)
                    throw new InvalidOperationException("Save or close unsaved editor tabs before discarding saved files.");
                var git = Git();
                using (var plan = await git.PrepareDiscardAsync(gitRemote.Text, gitFolder.Text, ct))
                using (var dialog = new DiscardChangesDialog(plan))
                {
                    if (plan.Changes.Count == 0) { await RefreshGitAsync(ct); status.Text = "No saved changes to discard."; return; }
                    System.Windows.Forms.DialogResult review;
                    operation.CancelAfter(Timeout.InfiniteTimeSpan);
                    try { review = documents == null ? dialog.ShowDialog() : dialog.ShowDialog(documents); }
                    finally { operation.CancelAfter(TimeSpan.FromMinutes(5)); }
                    if (review != System.Windows.Forms.DialogResult.OK) { status.Text = "Discard cancelled. Files kept."; return; }
                    var paths = dialog.SelectedPaths;
                    var affected = plan.Changes.Where(c => paths.Contains(c.Path)).SelectMany(c =>
                        c.OriginalPath == null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath });
                    var reopen = documents?.CloseForUpdate(gitFolder.Text, affected, "discard");
                    bool reopenFailed = false;
                    try { await git.ApplyDiscardAsync(plan, paths, ct); }
                    finally { try { reopen?.Invoke(); } catch { reopenFailed = true; } }
                    await RefreshGitAsync(ct);
                    status.Text = "Selected changes discarded. Recovery copy saved locally." + (reopenFailed ? " Reopen the affected documents in Altium." : "");
                }
            }, "Reviewing changes to discard…");
            openProject.Click += (_, __) =>
            {
                try
                {
                    string folder = gitFolder.Text;
                    if (!Directory.Exists(folder) && !string.IsNullOrWhiteSpace(activeProjectFile) && Path.IsPathRooted(activeProjectFile)) folder = Path.GetDirectoryName(activeProjectFile);
                    using (var dialog = new System.Windows.Forms.OpenFileDialog { Title = "Open project", InitialDirectory = folder, Filter = "Altium PCB project|*.PrjPcb", CheckFileExists = true })
                        if ((documents == null ? dialog.ShowDialog() : dialog.ShowDialog(documents)) == System.Windows.Forms.DialogResult.OK) { openProjectAction(dialog.FileName); FollowCurrentProject(false); }
                }
                catch { status.Text = "Unable to open the project. Open its PrjPcb file from Altium's File menu."; }
            };
            busyControls.AddRange(new Control[] { gitRemote, gitFolder, gitToken, gitExecutable, gitReference, commitMessage, chooseFolder, chooseGit, changesGrid });
            return layout;
        }
        private WorkspaceGit Git() => new WorkspaceGit(new ProcessGitRunner(gitExecutable.Text));
        private void Disconnect(bool logOut)
        {
            reconnectOnStartup = logOut;
            monitorCancellation?.Cancel(); fetchSchedule.Clear(); serverNotices.Clear(); fetchWarnings.Clear(); fetchErrors.Clear(); fetchScanError = null; lastFetchScan = lastFetchCompleted = DateTime.MinValue;
            if (logOut) { signInStore.Clear(); session.Disconnect(); workspaces.ItemsSource = null; }
            else session.DisconnectWorkspace();
            accessToken.Clear(); gitToken.Clear(); gitProject = null; gitWorkspace = null;
            projects.Clear(); projectSearch.Clear(); ApplyProjectFilter(); tabs.SelectedIndex = 0;
            status.Text = logOut ? "Logged out." : "Disconnected."; UpdateState();
            fetchNotice.Text = "Background fetch resumes when connected.";
        }
        private async Task<string> GitTokenAsync(CancellationToken cancellation)
        {
            if (gitProject != null && gitRemote.Text == gitProject.RepositoryUrl)
            {
                if (!SameWorkspace(session.Connected, gitWorkspace))
                    throw new InvalidOperationException("Connect to this project's workspace or select a project from the connected workspace.");
                return await session.AccessTokenAsync(cancellation);
            }
            if (followProject.IsChecked == true) throw new InvalidOperationException("Sign in and connect to this project's Altium 365 workspace before synchronizing.");
            return gitToken.Password;
        }
        private void BindKnownProject()
        {
            gitProject = projects.FirstOrDefault(p => p.RepositoryUrl == gitRemote.Text);
            gitWorkspace = gitProject == null ? null : session.Connected;
        }
        private void SetWorkingCopy(string remote, string folder)
        {
            bool changed = gitRemote.Text != (remote ?? "") || !string.Equals(gitFolder.Text, folder ?? "", StringComparison.OrdinalIgnoreCase);
            if (!changed) return;
            settingWorkingCopy = true;
            try { gitRemote.Text = remote ?? ""; gitFolder.Text = folder ?? ""; }
            finally { settingWorkingCopy = false; }
            gitToken.Clear(); gitProject = null; gitWorkspace = null; commitMessage.Clear(); ClearWorkingStatus(false);
        }
        private void DetectActiveProject()
        {
            if (disposed || operation != null || preparingClone || followProject.IsChecked != true) return;
            string path;
            try { path = documents?.ActiveProjectFile(); }
            catch { path = null; }
            if (string.Equals(path, activeProjectFile, StringComparison.OrdinalIgnoreCase)) return;
            activeProjectFile = path; contextPending = true; monitorCancellation?.Cancel();
            activeProject.Text = path == null ? "No active saved project." : Path.GetFileNameWithoutExtension(path) + " — detecting working copy…";
            UpdateState();
        }
        private async Task FollowActiveProjectAsync(CancellationToken ct)
        {
            if (preparingClone || !contextPending || followProject.IsChecked != true) return;
            string path = activeProjectFile;
            try
            {
                string folder = await Task.Run(() => ProjectWorkingCopy.FindRoot(path), ct);
                string remote = folder == null ? null : await Git().ReadRemoteAsync(folder, ct);
                ct.ThrowIfCancellationRequested();
                if (disposed || followProject.IsChecked != true || !string.Equals(path, activeProjectFile, StringComparison.OrdinalIgnoreCase)) return;
                SetWorkingCopy(remote, remote == null ? null : folder); BindKnownProject();
                activeProject.Text = path == null ? "No active saved project. Open a project in Altium." :
                    Path.GetFileNameWithoutExtension(path) + (remote == null ? " — no Altium 365 working copy detected." : "");
                activeProject.ToolTip = folder ?? path;
                if (remote == null) gitSummary.Text = "Choose a workspace project to clone, or use Manual setup for an existing checkout.";
                contextPending = false; SaveSettings();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (!disposed && path == activeProjectFile && followProject.IsChecked == true)
                {
                    SetWorkingCopy(null, null); contextPending = false;
                    activeProject.Text = "Cannot detect this project's working copy. " + FriendlyError(error);
                }
            }
            finally { if (!disposed) UpdateState(); }
        }
        internal void FollowCurrentProject(bool showConnection = true)
        {
            preparingClone = false; followProject.IsChecked = true; activeProjectFile = "\0"; DetectActiveProject();
            if (showConnection) tabs.SelectedIndex = session.Connected == null ? 0 : 2;
        }
        private void ClearWorkingStatus(bool cancelPending = true)
        {
            workingStatus = null; changes.Clear(); unsavedDocuments = 0; gitSummary.Text = "Clone the project or refresh the selected working copy.";
            ConfigureWatcher(cancelPending); UpdateState();
        }
        private async Task RefreshGitAsync(CancellationToken cancellation)
        {
            string remote = gitRemote.Text, folder = gitFolder.Text;
            var result = await Git().StatusAsync(remote, folder, cancellation);
            if (disposed || remote != gitRemote.Text || folder != gitFolder.Text) return;
            changesGrid.CommitEdit(DataGridEditingUnit.Cell, true); changesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selections = changes.ToDictionary(c => c.Path, c => c.Selected, StringComparer.Ordinal);
            workingStatus = result;
            string key = WorkingCopyPath.Root(folder);
            if (result.IncomingCommits == 0) { fetchWarnings.Remove(key); serverNotices.Remove(key); RenderFetchNotice(); }
            else if (fetchWarnings.TryGetValue(key, out var warnings)) result.PotentialConflicts.AddRange(warnings);
            changes.Clear();
            foreach (var change in result.Changes) { change.Selected = !selections.TryGetValue(change.Path, out bool selected) || selected; changes.Add(change); }
            lastStatusRead = DateTime.UtcNow; ReadEditorState(); UpdateGitSummary();
            if (monitorFolder == null) ConfigureWatcher(false);
        }
        internal void NotifyDocumentChanged()
        {
            if (disposed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref notificationQueued, 0);
                if (!disposed) { statusRequested = true; if (operation == null) { ReadEditorState(); UpdateState(); } }
            }));
        }
        internal void RequestServerSave() => Dispatcher.BeginInvoke(new Action(async () => await SaveToServerAsync()));
        private void ReadEditorState()
        {
            if (disposed || documents == null || workingStatus == null) { unsavedDocuments = 0; return; }
            try { unsavedDocuments = documents.ModifiedFiles(gitFolder.Text).Length; editorStateAvailable = true; }
            catch { editorStateAvailable = false; }
        }
        private void UpdateGitSummary()
        {
            if (workingStatus == null) return;
            gitSummary.Text = "Branch: " + workingStatus.Branch + " · " + changes.Count + " changed files · " + workingStatus.OutgoingCommits + " commits waiting to upload · " + workingStatus.IncomingCommits + " server commits to pull" +
                (workingStatus.HasConflicts ? " · unresolved merge conflicts" : "") +
                (workingStatus.PotentialConflicts.Count > 0 ? " · " + workingStatus.PotentialConflicts.Count + " possible file conflicts — review Pull changes" : "") +
                (unsavedDocuments > 0 ? " · " + unsavedDocuments + " unsaved documents" : "") + "." +
                (!editorStateAvailable ? " Altium's document state is unavailable; Save to Server is disabled." : "");
        }
        private void ConfigureWatcher(bool cancelPending = true)
        {
            if (cancelPending) monitorCancellation?.Cancel(); watcher?.Dispose(); watcher = null; monitorFolder = null;
            statusRequested = false; lastStatusRead = DateTime.MinValue;
            try
            {
                WorkspaceGit.ValidateRemote(gitRemote.Text);
                if (!Directory.Exists(gitFolder.Text) || (!Directory.Exists(Path.Combine(gitFolder.Text, ".git")) && !File.Exists(Path.Combine(gitFolder.Text, ".git")))) return;
                string root = WorkingCopyPath.Root(gitFolder.Text); monitorFolder = root; statusRequested = true;
                watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += (_, e) => QueueFileChange(root, e.FullPath); watcher.Created += (_, e) => QueueFileChange(root, e.FullPath);
                watcher.Deleted += (_, e) => QueueFileChange(root, e.FullPath); watcher.Renamed += (_, e) => { QueueFileChange(root, e.OldFullPath); QueueFileChange(root, e.FullPath); };
                watcher.Error += (_, __) => NotifyDocumentChanged(); watcher.EnableRaisingEvents = true; statusRequested = true;
            }
            catch { /* The periodic status check also covers unavailable filesystem notifications. */ }
        }
        private void QueueFileChange(string root, string path)
        {
            string relative;
            try { relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/'); }
            catch { return; }
            if (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) && relative != ".git/HEAD" && relative != ".git/config" && relative != ".git/packed-refs" && !relative.StartsWith(".git/refs/", StringComparison.OrdinalIgnoreCase)) return;
            NotifyDocumentChanged();
        }
        private async Task MonitorAsync()
        {
            if (disposed || operation != null || monitoring) return;
            ReadEditorState(); UpdateGitSummary(); UpdateState();
            bool fetchDue = (session.Connected != null || gitToken.Password.Length > 0) && DateTime.UtcNow - lastFetchScan >= BackgroundFetchSchedule.Interval;
            if (!contextPending && !fetchDue && (monitorFolder == null || (!statusRequested && DateTime.UtcNow - lastStatusRead < TimeSpan.FromSeconds(30)))) return;
            statusRequested = false; monitoring = true; UpdateState();
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    monitorCancellation = timeout;
                    await FollowActiveProjectAsync(timeout.Token);
                    if (contextPending) return;
                    if (fetchDue) await FetchOpenProjectsAsync(timeout.Token);
                    if (monitorFolder != null) await RefreshGitAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) { if (fetchDue) lastFetchScan = DateTime.MinValue; }
            catch (Exception e)
            {
                if (!disposed)
                {
                    if (fetchDue) { fetchScanError = "Background fetch unavailable: " + FriendlyError(e) + " Retrying in two minutes."; RenderFetchNotice(); }
                    workingStatus = null; changes.Clear(); gitSummary.Text = "Unable to refresh the working copy. Choose Refresh status before saving to the server."; lastStatusRead = DateTime.UtcNow;
                }
            }
            finally { monitorCancellation = null; monitoring = false; if (!disposed) UpdateState(); }
        }
        private async Task FetchOpenProjectsAsync(CancellationToken ct)
        {
            lastFetchScan = DateTime.UtcNow; fetchScanError = null;
            fetchNotice.Text = "Checking open projects for server changes…";
            string[] open;
            try { open = documents == null ? Array.Empty<string>() : documents.ProjectFiles().Concat(documents.OpenFiles()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch { open = Array.Empty<string>(); fetchScanError = "Open project detection is unavailable. The selected working copy is still checked."; }
            string selectedFolder = gitFolder.Text, selectedRemote = gitRemote.Text;
            var roots = await Task.Run(() =>
            {
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in open)
                {
                    string root = ProjectWorkingCopy.FindRoot(file);
                    if (root != null) folders.Add(root);
                }
                if (monitorFolder != null) folders.Add(WorkingCopyPath.Root(selectedFolder));
                return folders.ToArray();
            }, ct);
            foreach (string closed in serverNotices.Keys.Concat(fetchErrors.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !roots.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray())
            { serverNotices.Remove(closed); fetchErrors.Remove(closed); }
            int checkedProjects = 0;
            // Use the same timestamp for the batch and each project's cooldown.
            DateTime scanAt = DateTime.UtcNow; lastFetchScan = scanAt;
            foreach (string folder in roots)
            {
                if (!fetchSchedule.TryStart(folder, scanAt)) continue;
                try
                {
                    var git = Git();
                    string remote = await git.ReadRemoteAsync(folder, ct);
                    if (remote == null) continue;
                    string token;
                    if (session.Connected != null && projects.Any(p => p.RepositoryUrl == remote)) token = await session.AccessTokenAsync(ct);
                    else if (remote == selectedRemote && folder.Equals(WorkingCopyPath.Root(selectedFolder), StringComparison.OrdinalIgnoreCase) && gitToken.Password.Length > 0) token = gitToken.Password;
                    else continue;
                    var result = await git.FetchAsync(remote, folder, token, ct);
                    checkedProjects++;
                    RecordFetch(folder, result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { fetchErrors[folder] = Path.GetFileName(folder) + ": fetch unavailable. " + FriendlyError(e); }
                finally { fetchSchedule.Complete(folder); }
            }
            if (!disposed)
            {
                if (checkedProjects > 0 || fetchErrors.Count > 0) { lastFetchCompleted = DateTime.UtcNow; lastFetchCount = checkedProjects; }
                RenderFetchNotice();
            }
        }
        private void RecordFetch(string folder, GitStatus result)
        {
            folder = WorkingCopyPath.Root(folder);
            fetchErrors.Remove(folder); fetchWarnings[folder] = result.PotentialConflicts.ToArray();
            if (result.IncomingCommits == 0) serverNotices.Remove(folder);
            else serverNotices[folder] = Path.GetFileName(folder) + ": " + result.IncomingCommits + " server commits" +
                (result.PotentialConflicts.Count > 0 ? " (possible file conflicts)" : "");
            RenderFetchNotice();
        }
        private void RenderFetchNotice()
        {
            if (fetchScanError != null) { fetchNotice.Text = fetchScanError; fetchNotice.Foreground = Brushes.DarkOrange; return; }
            if (lastFetchCompleted == DateTime.MinValue) { fetchNotice.Text = "Background fetch checks open projects every two minutes when connected."; fetchNotice.Foreground = Brushes.DimGray; return; }
            string checkedAt = lastFetchCompleted.ToLocalTime().ToString("HH:mm:ss");
            var notices = serverNotices.Values.Concat(fetchErrors.Values).ToArray();
            fetchNotice.Text = notices.Length == 0 ? "Background fetch checked " + lastFetchCount + (lastFetchCount == 1 ? " project at " : " projects at ") + checkedAt + ". Checks every two minutes." :
                "Checked " + checkedAt + " · " + string.Join("\n", notices) + (fetchErrors.Count > 0 ? "\nBackground fetch retries automatically." : "\nReview Pull changes to update files.");
            fetchNotice.Foreground = notices.Length == 0 ? Brushes.DimGray : Brushes.DarkOrange;
        }
        private async Task<bool> PullChangesAsync(CancellationToken ct)
        {
            using (var plan = await Git().PreparePullAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct))
            {
                if (plan.IncomingCommits == 0) return true;
                ReadEditorState();
                if (!editorStateAvailable || unsavedDocuments > 0) throw new InvalidOperationException("Save open project documents before reviewing a pull. Saved local changes can be kept in the conflict review.");
                using (var dialog = new PullChangesDialog(plan))
                {
                    System.Windows.Forms.DialogResult review;
                    operation.CancelAfter(Timeout.InfiniteTimeSpan);
                    try { review = dialog.ShowDialog(documents); }
                    finally { operation.CancelAfter(TimeSpan.FromMinutes(5)); }
                    if (review != System.Windows.Forms.DialogResult.OK) { status.Text = "Pull cancelled. No local files were replaced."; return false; }
                    ct.ThrowIfCancellationRequested();
                    var reopen = documents?.CloseForUpdate(gitFolder.Text, plan.UpdatedFiles, "pull");
                    status.Text = "Applying reviewed project changes…";
                    await Git().ApplyPullAsync(plan, dialog.Choices, ct);
                    try { reopen?.Invoke(); }
                    catch { throw new InvalidOperationException("Changes were pulled. Reopen the project to load its updated documents."); }
                }
                await RefreshGitAsync(ct); SaveSettings(); return true;
            }
        }
        private async Task SaveToServerAsync() => await RunAsync(async ct =>
        {
            tabs.SelectedIndex = 2;
            var before = await Git().StatusAsync(gitRemote.Text, gitFolder.Text, ct);
            if (before.Branch != "master") throw new InvalidOperationException("Switch to master before saving to Altium 365.");
            documents?.SaveModified(gitFolder.Text);
            if (!await PullChangesAsync(ct)) return;
            await RefreshGitAsync(ct);
            if (!editorStateAvailable || unsavedDocuments > 0) throw new InvalidOperationException("Save the project's documents in Altium before uploading. Nothing was uploaded.");
            if (!workingStatus.HasPendingSave) { status.Text = "Project is up to date. Nothing to save to the server."; return; }
            using (var dialog = new ServerSaveDialog(workingStatus, gitFolder.Text, commitMessage.Text))
            {
                System.Windows.Forms.DialogResult review;
                operation.CancelAfter(Timeout.InfiniteTimeSpan);
                try { review = dialog.ShowDialog(documents); }
                finally { operation.CancelAfter(TimeSpan.FromMinutes(5)); }
                commitMessage.Text = dialog.Comment;
                if (review != System.Windows.Forms.DialogResult.OK) { status.Text = "Save to Server cancelled. Documents remain saved locally."; return; }
                ct.ThrowIfCancellationRequested();
                ReadEditorState();
                if (!editorStateAvailable || unsavedDocuments > 0) throw new InvalidOperationException("The project changed while reviewing. Retry Save to Server to review the latest saved files.");
                status.Text = "Saving to Altium 365…";
                var selected = new HashSet<string>(dialog.SelectedPaths, StringComparer.Ordinal);
                foreach (var change in changes) change.Selected = selected.Contains(change.Path);
                try { await Git().SaveToServerAsync(gitRemote.Text, gitFolder.Text, selected, dialog.Comment, await GitTokenAsync(ct), ct); }
                catch
                {
                    try { using (var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25))) await RefreshGitAsync(refreshTimeout.Token); } catch { }
                    throw;
                }
                commitMessage.Clear(); status.Text = "Saved to Altium 365."; SaveSettings();
                try { await RefreshGitAsync(ct); }
                catch { status.Text = "Saved to Altium 365. Refresh status to read the latest working-copy state."; }
            }
        }, "Preparing Save to Server…");
        private static bool SameWorkspace(Workspace a, Workspace b) => a != null && b != null && a.Url == b.Url &&
            (string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(b.Id) || string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        private void ApplyProjectFilter()
        {
            searchTimer.Stop();
            projectTerms = projectSearch.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            projectView.Refresh();
            if (projectGrid?.SelectedItem is CloudProject selected && !projectView.Contains(selected)) projectGrid.SelectedItem = null;
            UpdateProjectCount(); UpdateState();
        }
        private void UpdateProjectCount()
        {
            int shown = projectView.Cast<object>().Count();
            projectCount.Text = projectTerms.Length == 0 ? projects.Count + (projects.Count == 1 ? " project found" : " projects found") :
                shown + " of " + projects.Count + " projects found";
        }
        private async Task ShowProjectsAsync(ProjectPage first, CancellationToken cancellation)
        {
            bool loading = true;
            var progress = new Progress<int>(count => { if (loading && !disposed) status.Text = "Finding projects… " + count; });
            List<CloudProject> all;
            try { all = await session.AllProjectsAsync(first, progress, cancellation); }
            finally { loading = false; }
            if (disposed) return;
            projects.Clear();
            foreach (var project in all.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) projects.Add(project);
            var selected = all.FirstOrDefault(p => p.RepositoryUrl == gitRemote.Text);
            if (selected != null)
            {
                gitProject = selected; gitWorkspace = session.Connected;
                projectGrid.SelectedItem = projectView.Contains(selected) ? selected : null;
            }
            UpdateProjectCount();
            status.Text = projects.Count + (projects.Count == 1 ? " project found." : " projects found.");
        }
        private async Task RunAsync(Func<CancellationToken, Task> action, string message, TimeSpan? timeout = null, bool needsWorkingCopy = true)
        {
            if (disposed || operation != null) return;
            if (needsWorkingCopy) DetectActiveProject();
            var pending = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            operation = pending; status.Text = message; UpdateState();
            try
            {
                // Freeze the selected project before awaiting a cancelled background fetch.
                if (monitoring) { monitorCancellation?.Cancel(); if (monitorTask != null) await monitorTask; }
                pending.Token.ThrowIfCancellationRequested();
                if (disposed) return;
                if (needsWorkingCopy)
                {
                    await FollowActiveProjectAsync(pending.Token);
                    if (contextPending) throw new InvalidOperationException("Wait for the active project's working copy to be detected.");
                }
                pending.Token.ThrowIfCancellationRequested();
                await action(pending.Token);
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "Operation cancelled or timed out."; }
            catch (Exception error)
            {
                if (!disposed) status.Text = FriendlyError(error);
            }
            finally { operation = null; pending.Dispose(); if (!disposed) { SaveSettings(); RememberSignIn(); UpdateState(); } }
        }
        internal static string FriendlyError(Exception error)
        {
            if (error is WorkspaceAuthenticationException) return "Altium sign-in failed. Complete sign-in in your browser and try again.";
            if (error is HttpRequestException) return "Cannot reach Altium 365. Check your connection and workspace address.";
            if (error is IOException || error is UnauthorizedAccessException) return "Cannot read or write the working copy. Check folder permissions and close any documents using those files.";
            // API errors above contain only fixed messages; authentication response bodies never enter the UI or logs.
            if (error is ArgumentException || error is InvalidOperationException) return error.Message;
            return "The connection could not be completed. Log out and try again.";
        }
        private void UpdateState()
        {
            bool busy = IsBusy || contextPending;
            bool connected = session.Connected != null;
            signIn.Visibility = signInPrompt.Visibility = session.SignedIn || connected ? Visibility.Collapsed : Visibility.Visible;
            foreach (var control in busyControls) control.IsEnabled = !busy;
            followProject.IsEnabled = !IsBusy;
            cancel.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
            connect.Content = connected ? "Disconnect" : "Connect";
            connect.IsEnabled = !IsBusy && (connected || session.SignedIn && workspaces.SelectedItem is Workspace);
            workspaces.IsEnabled = !IsBusy && !connected;
            workspaceStatus.Text = connected ? "Status: Connected" : "Status: Disconnected";
            workspaceStatus.Foreground = connected ? Brushes.DarkGreen : Brushes.DimGray;
            logout.IsEnabled = !busy && (session.SignedIn || session.Connected != null || gitToken.Password.Length > 0 || accessToken.Password.Length > 0);
            createProject.IsEnabled = refresh.IsEnabled = open.IsEnabled = !busy && session.Connected != null;
            useProject.IsEnabled = !busy && session.Connected != null && projectGrid?.SelectedItem is CloudProject project && projectView.Contains(project) && !string.IsNullOrEmpty(project.RepositoryUrl);
            clone.IsEnabled = !busy && (preparingClone || followProject.IsChecked != true);
            gitRefresh.IsEnabled = !busy && (followProject.IsChecked != true || monitorFolder != null);
            fetch.IsEnabled = !busy && workingStatus != null;
            pull.IsEnabled = push.IsEnabled = !busy && workingStatus?.Branch == "master";
            commit.IsEnabled = !busy && workingStatus?.Branch == "master" && !workingStatus.HasConflicts && changes.Count > 0;
            discard.IsEnabled = !busy && workingStatus?.Branch == "master" && !workingStatus.HasConflicts && changes.Count > 0;
            openProject.IsEnabled = !busy && openProjectAction != null;
            saveToServer.Visibility = workingStatus?.Branch == "master" && (unsavedDocuments > 0 || workingStatus.HasPendingSave) ? Visibility.Visible : Visibility.Collapsed;
            saveToServer.IsEnabled = !busy && editorStateAvailable;
            connection.Text = session.Connected == null ? (session.SignedIn ? "Signed in" : "Not signed in") : "Connected · " + session.Connected.Name;
            connection.Foreground = session.Connected == null ? Brushes.DimGray : Brushes.DarkGreen;
        }
        private static void OpenBrowser(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        private void TryOpen(string url) { try { OpenBrowser(url); } catch { status.Text = "Unable to open the browser."; } }
        private void LoadSettings()
        {
            settingWorkingCopy = true;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(settingsPath));
                if (saved == null) return;
                if (saved.TryGetValue("workspaceUrl", out var url) && !string.IsNullOrWhiteSpace(url))
                    try { workspaceUrl.Text = WorkspaceApi.WorkspaceUrl(url).AbsoluteUri; } catch (ArgumentException) { }
                if (saved.TryGetValue("gitRemote", out var remote) && !string.IsNullOrWhiteSpace(remote))
                    try { gitRemote.Text = WorkspaceGit.ValidateRemote(remote); } catch (ArgumentException) { }
                if (saved.TryGetValue("gitFolder", out var folder)) gitFolder.Text = folder;
                if (saved.TryGetValue("gitExecutable", out var executable)) gitExecutable.Text = executable;
                if (saved.TryGetValue("gitReference", out var reference)) gitReference.Text = reference;
                if (saved.TryGetValue("region", out var gateway) && WorkspaceApi.Regions.Any(r => r.Value == gateway)) region.SelectedValue = gateway;
                if (saved.TryGetValue("followActiveProject", out var follow) && bool.TryParse(follow, out bool enabled)) followProject.IsChecked = enabled;
            }
            catch { }
            finally { settingWorkingCopy = false; }
        }
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new Dictionary<string, string> { ["workspaceUrl"] = workspaceUrl.Text, ["region"] = (string)region.SelectedValue, ["gitRemote"] = gitRemote.Text, ["gitFolder"] = gitFolder.Text, ["gitExecutable"] = gitExecutable.Text, ["gitReference"] = gitReference.Text, ["followActiveProject"] = (followProject.IsChecked == true).ToString() }));
            }
            catch { /* Preferences are optional. */ }
        }
        public void Dispose()
        {
            if (disposed) return;
            RememberSignIn();
            disposed = true; operation?.Cancel(); accessToken.Clear(); gitToken.Clear(); session.Disconnect();
            searchTimer.Stop(); monitorTimer.Stop(); monitorCancellation?.Cancel(); watcher?.Dispose(); watcher = null;
            apiHttp.Dispose(); authHttp.Dispose();
        }
    }
}
