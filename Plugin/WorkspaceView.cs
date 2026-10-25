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
        private readonly TextBlock projectCount = Label("0 projects");
        private readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private string[] projectTerms = Array.Empty<string>();
        private readonly TextBox workspaceUrl = new TextBox();
        private readonly PasswordBox accessToken = new PasswordBox();
        private readonly ComboBox workspaces = new ComboBox { DisplayMemberPath = "Name", MinWidth = 180 };
        private readonly ComboBox region = new ComboBox { ItemsSource = WorkspaceApi.Regions, DisplayMemberPath = "Key", SelectedValuePath = "Value", SelectedIndex = 0 };
        private readonly TextBlock connection = Label("Not signed in"), status = Label("Connect to browse your workspace projects.");
        private readonly Button cancel = Button("Cancel"), signIn = Button("Sign in"), connect = Button("Connect"), refresh = Button("Refresh"), logout = Button("Log out"), open = Button("Open workspace");
        private readonly TabControl tabs = new TabControl();
        private readonly TextBox gitRemote = new TextBox(), gitFolder = new TextBox(), gitExecutable = new TextBox(), gitReference = new TextBox(), commitMessage = new TextBox();
        private readonly PasswordBox gitToken = new PasswordBox();
        private readonly TextBlock gitSummary = Label("Choose a project or enter its repository URL, then clone or select a working copy.");
        private readonly Button useProject = Button("Use selected project"), clone = Button("Clone"), gitRefresh = Button("Refresh status"), pull = Button("Pull"), push = Button("Push"), commit = Button("Commit selected"), openProject = Button("Open project");
        private readonly Button createProject = Button("Create project…");
        private readonly Button saveToServer = Button("Save to Server…");
        private readonly ObservableCollection<GitChange> changes = new ObservableCollection<GitChange>();
        private readonly DataGrid changesGrid = new DataGrid();
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
            get { ReadEditorState(); return !disposed && editorStateAvailable && workingStatus?.Branch == "master" && (unsavedDocuments > 0 || workingStatus.HasPendingSave); }
        }
        private DataGrid projectGrid;
        private CloudProject gitProject;
        private Workspace gitWorkspace;
        private GitStatus workingStatus;
        private readonly List<Control> busyControls = new List<Control>();
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium17365", "settings.json");
        private CancellationTokenSource operation;
        private bool disposed;
        private static HttpClient CreateHttp(TimeSpan timeout) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12 }) { Timeout = timeout };
        private static TextBlock Label(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private static Button Button(string text) => new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 4) };
        internal WorkspaceView(Action<string> openProjectAction = null, WorkspaceDocuments documents = null)
        {
            this.openProjectAction = openProjectAction;
            this.documents = documents;
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
            logout.Click += (_, __) => LogOut();
            var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            layout.Children.Add(tabs); Content = layout;
            cancel.Visibility = Visibility.Collapsed; cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Click += (_, __) => operation?.Cancel();
            tabs.Items.Add(new TabItem { Header = "Connection", Content = BuildConnection() });
            tabs.Items.Add(new TabItem { Header = "Projects", Content = BuildProjects() });
            tabs.Items.Add(new TabItem { Header = "Git", Content = BuildGit() });
            gitToken.PasswordChanged += (_, __) => UpdateState();
            accessToken.PasswordChanged += (_, __) => UpdateState();
            LoadSettings(); UpdateState();
            monitorTimer.Tick += async (_, __) => { if (!monitoring) { monitorTask = MonitorAsync(); await monitorTask; } }; monitorTimer.Start();
        }
        private UIElement BuildConnection()
        {
            var stack = new StackPanel { Margin = new Thickness(10) };
            stack.Children.Add(Label("Sign in to Altium 365 in your browser, then choose a workspace."));
            stack.Children.Add(signIn);
            stack.Children.Add(Label("Workspace")); stack.Children.Add(workspaces); stack.Children.Add(connect);
            stack.Children.Add(Label("Your session stays in memory until you log out or close Altium."));
            workspaces.SelectionChanged += (_, __) => UpdateState();
            signIn.Click += async (_, __) => await RunAsync(async ct =>
            {
                var available = await session.SignInAsync((string)region.SelectedValue, OpenBrowser, ct);
                projects.Clear(); UpdateProjectCount(); gitProject = null; gitWorkspace = null; gitToken.Clear(); accessToken.Clear();
                workspaces.ItemsSource = available;
                var selected = available.FirstOrDefault(w => w.Url.AbsoluteUri == workspaceUrl.Text) ?? (available.Count == 1 ? available[0] : null);
                workspaces.SelectedItem = selected;
                if (selected != null) await ConnectWorkspaceAsync(selected, ct);
                else status.Text = available.Count == 0 ? "No workspaces available for this account." : "Choose a workspace and connect.";
            }, "Complete sign-in in your browser…");
            connect.Click += async (_, __) => await RunAsync(ct => ConnectWorkspaceAsync((Workspace)workspaces.SelectedItem, ct), "Connecting to workspace…");
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
                tabs.SelectedIndex = 1; await ShowProjectsAsync(page, ct);
            }, "Connecting to workspace…");
            stack.Children.Add(new Expander { Header = "Advanced connection", Content = advanced, Margin = new Thickness(0, 18, 0, 0) });
            busyControls.AddRange(new Control[] { signIn, workspaces, region, workspaceUrl, accessToken, tokenConnect });
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        private async Task ConnectWorkspaceAsync(Workspace workspace, CancellationToken cancellation)
        {
            status.Text = "Connecting to workspace…";
            var page = await session.ConnectAsync(workspace, null, cancellation);
            workspaceUrl.Text = session.Connected.Url.AbsoluteUri;
            tabs.SelectedIndex = 1;
            await ShowProjectsAsync(page, cancellation);
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
            grid.Columns.Add(new DataGridTextColumn { Header = "VCS", Binding = new System.Windows.Data.Binding("VersionControl"), Width = DataGridLength.Auto });
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
            useProject.Click += (_, __) =>
            {
                if (!(grid.SelectedItem is CloudProject project) || !projectView.Contains(project) || string.IsNullOrEmpty(project.RepositoryUrl)) return;
                gitRemote.Text = project.RepositoryUrl; gitProject = project; gitWorkspace = session.Connected; gitToken.Clear();
                tabs.SelectedIndex = 2; status.Text = "Project selected. Choose its local working-copy folder, or a new folder to clone into.";
                UpdateState();
            };
            return layout;
        }
        private UIElement BuildGit()
        {
            var layout = new DockPanel { Margin = new Thickness(10) };
            var settings = new StackPanel();
            var configuration = new StackPanel();
            configuration.Children.Add(Label("Choose a project from the Projects tab, then clone or select its working copy."));
            var direct = new StackPanel();
            direct.Children.Add(Label("Altium 365 repository URL")); direct.Children.Add(gitRemote);
            direct.Children.Add(Label("Workspace token (direct Git only)")); direct.Children.Add(gitToken);
            configuration.Children.Add(new Expander { Header = "Advanced: direct repository", Content = direct });
            configuration.Children.Add(Label("Local working-copy folder")); configuration.Children.Add(gitFolder);
            var chooseFolder = Button("Choose folder…"); configuration.Children.Add(chooseFolder);
            chooseFolder.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose an existing working copy or an empty folder to clone into.", ShowNewFolderButton = true })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) gitFolder.Text = dialog.SelectedPath;
            };
            var advanced = new StackPanel();
            advanced.Children.Add(Label("Git executable (optional; use Git for Windows under Wine)")); advanced.Children.Add(gitExecutable);
            var chooseGit = Button("Choose git.exe…"); advanced.Children.Add(chooseGit);
            chooseGit.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Git executable|git.exe", CheckFileExists = true })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) gitExecutable.Text = dialog.FileName;
            };
            advanced.Children.Add(Label("Existing checkout to reuse when cloning (optional)")); advanced.Children.Add(gitReference);
            advanced.Children.Add(Label("Reuses downloaded history and creates an independent copy. Local edits in the existing checkout are preserved."));
            configuration.Children.Add(new Expander { Header = "Git setup", Content = advanced });
            settings.Children.Add(new Expander { Header = "Working copy", IsExpanded = true, Content = new ScrollViewer { Content = configuration, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            var buttons = new WrapPanel();
            saveToServer.FontWeight = FontWeights.SemiBold;
            saveToServer.Background = new SolidColorBrush(Color.FromRgb(24, 91, 153)); saveToServer.Foreground = Brushes.White;
            settings.Children.Add(saveToServer);
            foreach (var button in new[] { clone, gitRefresh, pull, push, openProject }) buttons.Children.Add(button);
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
            gitRemote.TextChanged += (_, __) => { gitProject = null; gitWorkspace = null; ClearWorkingStatus(); };
            gitFolder.TextChanged += (_, __) => ClearWorkingStatus();
            clone.Click += async (_, __) => await RunAsync(async ct =>
            {
                var git = Git();
                await git.CloneAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), gitReference.Text, ct);
                await RefreshGitAsync(ct);
                if (workingStatus.IsUnborn && changes.Count == 0 && gitProject != null)
                {
                    NewProjectFiles.Write(gitFolder.Text, gitProject.Name);
                    await RefreshGitAsync(ct);
                }
                SaveSettings(); status.Text = "Project cloned. Open it to begin editing.";
            }, "Cloning project…", TimeSpan.FromHours(1));
            gitRefresh.Click += async (_, __) => await RunAsync(async ct => { await RefreshGitAsync(ct); SaveSettings(); status.Text = "Working-copy status refreshed."; }, "Reading working-copy status…");
            pull.Click += async (_, __) => await RunAsync(async ct =>
            {
                if (MessageBox.Show("Save and close this working copy's project documents in Altium before pulling. Continue after closing them?", "Pull from Altium 365", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                { status.Text = "Pull cancelled."; return; }
                await Git().PullAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct);
                await RefreshGitAsync(ct); status.Text = "Working copy updated. Reopen any project documents already open in Altium to read the updated files.";
            }, "Pulling project…");
            push.Click += async (_, __) => await RunAsync(async ct =>
            {
                await Git().PushAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct);
                await RefreshGitAsync(ct); status.Text = "Commits pushed to the Workspace's master branch.";
            }, "Pushing project…");
            commit.Click += async (_, __) => await RunAsync(async ct =>
            {
                changesGrid.CommitEdit(DataGridEditingUnit.Cell, true); changesGrid.CommitEdit(DataGridEditingUnit.Row, true);
                var selected = changes.Where(c => c.Selected).Select(c => c.Path).ToArray();
                await Git().CommitAsync(gitRemote.Text, gitFolder.Text, selected, commitMessage.Text, ct);
                await RefreshGitAsync(ct); commitMessage.Clear(); status.Text = "Selected files committed locally. Push to send the commits to Altium 365.";
            }, "Committing selected files…");
            saveToServer.Click += async (_, __) => await SaveToServerAsync();
            openProject.Click += (_, __) =>
            {
                try
                {
                    using (var dialog = new System.Windows.Forms.OpenFileDialog { InitialDirectory = gitFolder.Text, Filter = "Altium PCB project|*.PrjPcb", CheckFileExists = true })
                        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) openProjectAction(dialog.FileName);
                }
                catch { status.Text = "Unable to open the project. Open its PrjPcb file from Altium's File menu."; }
            };
            busyControls.AddRange(new Control[] { gitRemote, gitFolder, gitToken, gitExecutable, gitReference, commitMessage, chooseFolder, chooseGit, changesGrid });
            return layout;
        }
        private WorkspaceGit Git() => new WorkspaceGit(new ProcessGitRunner(gitExecutable.Text));
        private void LogOut()
        {
            session.Disconnect(); accessToken.Clear(); gitToken.Clear(); gitProject = null; gitWorkspace = null; workspaces.ItemsSource = null;
            projects.Clear(); projectSearch.Clear(); ApplyProjectFilter(); tabs.SelectedIndex = 0;
            status.Text = "Logged out. Session and Git credentials cleared."; UpdateState();
        }
        private async Task<string> GitTokenAsync(CancellationToken cancellation)
        {
            if (gitProject != null && gitRemote.Text == gitProject.RepositoryUrl)
            {
                if (!SameWorkspace(session.Connected, gitWorkspace))
                    throw new InvalidOperationException("Connect to this project's workspace or select a project from the connected workspace.");
                return await session.AccessTokenAsync(cancellation);
            }
            return gitToken.Password;
        }
        private void ClearWorkingStatus()
        {
            workingStatus = null; changes.Clear(); unsavedDocuments = 0; gitSummary.Text = "Clone the project or refresh the selected working copy.";
            ConfigureWatcher(); UpdateState();
        }
        private async Task RefreshGitAsync(CancellationToken cancellation)
        {
            string remote = gitRemote.Text, folder = gitFolder.Text;
            var result = await Git().StatusAsync(remote, folder, cancellation);
            if (disposed || remote != gitRemote.Text || folder != gitFolder.Text) return;
            changesGrid.CommitEdit(DataGridEditingUnit.Cell, true); changesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selections = changes.ToDictionary(c => c.Path, c => c.Selected, StringComparer.Ordinal);
            workingStatus = result;
            changes.Clear();
            foreach (var change in result.Changes) { change.Selected = !selections.TryGetValue(change.Path, out bool selected) || selected; changes.Add(change); }
            lastStatusRead = DateTime.UtcNow; ReadEditorState(); UpdateGitSummary();
            if (monitorFolder == null) ConfigureWatcher();
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
            gitSummary.Text = "Branch: " + workingStatus.Branch + " · " + changes.Count + " changed files · " + workingStatus.OutgoingCommits + " commits waiting to upload" +
                (unsavedDocuments > 0 ? " · " + unsavedDocuments + " unsaved documents" : "") + "." +
                (!editorStateAvailable ? " Altium's document state is unavailable; Save to Server is disabled." : "");
        }
        private void ConfigureWatcher()
        {
            monitorCancellation?.Cancel(); watcher?.Dispose(); watcher = null; monitorFolder = null;
            statusRequested = false; lastStatusRead = DateTime.MinValue;
            try
            {
                WorkspaceGit.ValidateRemote(gitRemote.Text);
                if (!Directory.Exists(gitFolder.Text) || (!Directory.Exists(Path.Combine(gitFolder.Text, ".git")) && !File.Exists(Path.Combine(gitFolder.Text, ".git")))) return;
                string root = Path.GetFullPath(gitFolder.Text); monitorFolder = root; statusRequested = true;
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
            if (disposed || operation != null || monitoring || monitorFolder == null) return;
            ReadEditorState(); UpdateGitSummary(); UpdateState();
            if (!statusRequested && DateTime.UtcNow - lastStatusRead < TimeSpan.FromSeconds(30)) return;
            statusRequested = false; monitoring = true; UpdateState();
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                { monitorCancellation = timeout; await RefreshGitAsync(timeout.Token); }
            }
            catch (OperationCanceledException) { }
            catch { if (!disposed) { workingStatus = null; changes.Clear(); gitSummary.Text = "Unable to refresh the working copy. Choose Refresh status before saving to the server."; lastStatusRead = DateTime.UtcNow; } }
            finally { monitorCancellation = null; monitoring = false; if (!disposed) UpdateState(); }
        }
        private async Task SaveToServerAsync() => await RunAsync(async ct =>
        {
            tabs.SelectedIndex = 2;
            var before = await Git().StatusAsync(gitRemote.Text, gitFolder.Text, ct);
            if (before.Branch != "master") throw new InvalidOperationException("Switch to master before saving to Altium 365.");
            documents?.SaveModified(gitFolder.Text);
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
            projectCount.Text = projectTerms.Length == 0 ? projects.Count + (projects.Count == 1 ? " project" : " projects") :
                shown + " of " + projects.Count + " projects";
        }
        private async Task ShowProjectsAsync(ProjectPage first, CancellationToken cancellation)
        {
            bool loading = true;
            var progress = new Progress<int>(count => { if (loading && !disposed) status.Text = "Loading projects… " + count; });
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
            status.Text = projects.Count + (projects.Count == 1 ? " project loaded." : " projects loaded.");
        }
        private async Task RunAsync(Func<CancellationToken, Task> action, string message, TimeSpan? timeout = null)
        {
            if (disposed || operation != null) return;
            if (monitoring) { monitorCancellation?.Cancel(); if (monitorTask != null) await monitorTask; }
            if (disposed || operation != null) return;
            var pending = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            operation = pending; status.Text = message; UpdateState();
            try { await action(pending.Token); }
            catch (OperationCanceledException) { if (!disposed) status.Text = "Operation cancelled or timed out."; }
            catch (Exception error)
            {
                if (!disposed) status.Text = FriendlyError(error);
            }
            finally { operation = null; pending.Dispose(); if (!disposed) { SaveSettings(); UpdateState(); } }
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
            bool busy = IsBusy;
            foreach (var control in busyControls) control.IsEnabled = !busy;
            cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            connect.IsEnabled = !busy && session.SignedIn && workspaces.SelectedItem is Workspace;
            logout.IsEnabled = !busy && (session.SignedIn || session.Connected != null || gitToken.Password.Length > 0 || accessToken.Password.Length > 0);
            createProject.IsEnabled = refresh.IsEnabled = open.IsEnabled = !busy && session.Connected != null;
            useProject.IsEnabled = !busy && session.Connected != null && projectGrid?.SelectedItem is CloudProject project && projectView.Contains(project) && !string.IsNullOrEmpty(project.RepositoryUrl);
            clone.IsEnabled = gitRefresh.IsEnabled = !busy;
            pull.IsEnabled = push.IsEnabled = !busy && workingStatus?.Branch == "master";
            commit.IsEnabled = !busy && workingStatus?.Branch == "master" && changes.Count > 0;
            openProject.IsEnabled = !busy && workingStatus != null && openProjectAction != null;
            saveToServer.Visibility = workingStatus?.Branch == "master" && (unsavedDocuments > 0 || workingStatus.HasPendingSave) ? Visibility.Visible : Visibility.Collapsed;
            saveToServer.IsEnabled = !busy && editorStateAvailable;
            connection.Text = session.Connected == null ? (session.SignedIn ? "Signed in" : "Not signed in") : "Connected · " + session.Connected.Name;
            connection.Foreground = session.Connected == null ? Brushes.DimGray : Brushes.DarkGreen;
        }
        private static void OpenBrowser(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        private void TryOpen(string url) { try { OpenBrowser(url); } catch { status.Text = "Unable to open the browser."; } }
        private void LoadSettings()
        {
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
            }
            catch { }
        }
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new Dictionary<string, string> { ["workspaceUrl"] = workspaceUrl.Text, ["region"] = (string)region.SelectedValue, ["gitRemote"] = gitRemote.Text, ["gitFolder"] = gitFolder.Text, ["gitExecutable"] = gitExecutable.Text, ["gitReference"] = gitReference.Text }));
            }
            catch { /* Preferences are optional; credentials are never persisted. */ }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; operation?.Cancel(); accessToken.Clear(); gitToken.Clear(); session.Disconnect();
            searchTimer.Stop(); monitorTimer.Stop(); monitorCancellation?.Cancel(); watcher?.Dispose(); watcher = null;
            apiHttp.Dispose(); authHttp.Dispose();
        }
    }
}
