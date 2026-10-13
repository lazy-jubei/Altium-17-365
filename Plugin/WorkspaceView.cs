using Altium.Auth;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;

namespace Altium17Cloud
{
    internal sealed class WorkspaceView : UserControl, IDisposable
    {
        private readonly HttpClient apiHttp = CreateHttp(TimeSpan.FromSeconds(45));
        private readonly HttpClient authHttp = CreateHttp(Timeout.InfiniteTimeSpan);
        private readonly WorkspaceSession session;
        private readonly ObservableCollection<CloudProject> projects = new ObservableCollection<CloudProject>();
        private readonly TextBox workspaceUrl = new TextBox(), clientId = new TextBox();
        private readonly PasswordBox accessToken = new PasswordBox();
        private readonly ComboBox regions = new ComboBox { DisplayMemberPath = "Key", SelectedValuePath = "Value" };
        private readonly ComboBox workspaces = new ComboBox { DisplayMemberPath = "Name" };
        private readonly TextBlock connection = Label("Disconnected"), status = Label("Connect to browse your workspace projects.");
        private readonly Button cancel = Button("Cancel"), connect = Button("Connect"), refresh = Button("Refresh"), more = Button("Load more"), disconnect = Button("Disconnect"), open = Button("Open workspace");
        private readonly TabControl tabs = new TabControl();
        private readonly TextBox gitRemote = new TextBox(), gitFolder = new TextBox(), gitExecutable = new TextBox(), commitMessage = new TextBox();
        private readonly PasswordBox gitToken = new PasswordBox();
        private readonly TextBlock gitSummary = Label("Choose a project or enter its repository URL, then clone or select a working copy.");
        private readonly Button useProject = Button("Use selected project"), clone = Button("Clone"), gitRefresh = Button("Refresh status"), pull = Button("Pull"), push = Button("Push"), commit = Button("Commit selected"), openProject = Button("Open project");
        private readonly ObservableCollection<GitChange> changes = new ObservableCollection<GitChange>();
        private readonly DataGrid changesGrid = new DataGrid();
        private readonly Action<string> openProjectAction;
        private DataGrid projectGrid;
        private CloudProject gitProject;
        private Workspace gitWorkspace;
        private GitStatus workingStatus;
        private readonly List<Control> busyControls = new List<Control>();
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium17365", "settings.json");
        private CancellationTokenSource operation;
        private string cursor;
        private bool hasNext, disposed;
        private static HttpClient CreateHttp(TimeSpan timeout) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12 }) { Timeout = timeout };
        private static TextBlock Label(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private static Button Button(string text) => new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 4) };
        internal WorkspaceView(Action<string> openProjectAction = null)
        {
            this.openProjectAction = openProjectAction;
            session = new WorkspaceSession(new WorkspaceApi(apiHttp), authHttp);
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Background = Brushes.White; Foreground = Brushes.Black;
            var layout = new DockPanel { Margin = new Thickness(10) };
            connection.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(connection, Dock.Top); layout.Children.Add(connection);
            var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            layout.Children.Add(tabs); Content = layout;
            cancel.Visibility = Visibility.Collapsed; cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Click += (_, __) => operation?.Cancel();
            tabs.Items.Add(new TabItem { Header = "Connection", Content = BuildConnection() });
            tabs.Items.Add(new TabItem { Header = "Projects", Content = BuildProjects() });
            tabs.Items.Add(new TabItem { Header = "Git", Content = BuildGit() });
            LoadSettings(); UpdateState();
        }
        private UIElement BuildConnection()
        {
            var stack = new StackPanel { Margin = new Thickness(10) };
            stack.Children.Add(Label("Use a workspace API access token, or sign in through your browser with a registered application ID."));
            stack.Children.Add(Label("Workspace address")); stack.Children.Add(workspaceUrl);
            stack.Children.Add(Label("Workspace API access token")); stack.Children.Add(accessToken);
            stack.Children.Add(Label("The token stays in memory and is cleared when you disconnect or close Altium."));
            var tokenConnect = Button("Connect with token"); stack.Children.Add(tokenConnect);
            tokenConnect.Click += async (_, __) => await RunAsync(async ct =>
            {
                var url = WorkspaceApi.WorkspaceUrl(workspaceUrl.Text);
                string token = accessToken.Password;
                accessToken.Clear();
                var workspace = new Workspace { Name = url.Host.Split('.')[0], Url = url };
                var page = await session.ConnectAsync(workspace, token, ct);
                ShowPage(page, false); SaveSettings(); tabs.SelectedIndex = 1;
            }, "Connecting to workspace…");
            var browser = new StackPanel();
            browser.Children.Add(Label("Altium-registered public client ID")); browser.Children.Add(clientId);
            browser.Children.Add(Label("Region")); browser.Children.Add(regions);
            regions.ItemsSource = WorkspaceApi.Regions; regions.SelectedIndex = 0;
            var signin = Button("Sign in in browser"); browser.Children.Add(signin);
            browser.Children.Add(Label("Workspace")); browser.Children.Add(workspaces); browser.Children.Add(connect);
            signin.Click += async (_, __) => await RunAsync(async ct =>
            {
                var list = await session.SignInAsync(clientId.Text, (string)regions.SelectedValue, OpenBrowser, ct);
                workspaces.ItemsSource = list; workspaces.SelectedIndex = list.Count == 0 ? -1 : 0;
                status.Text = list.Count == 0 ? "Signed in. No workspaces found in this region." : "Signed in. Choose a workspace, then Connect.";
                SaveSettings();
            }, "Waiting for browser sign-in…");
            connect.Click += async (_, __) => await RunAsync(async ct =>
            {
                var selected = workspaces.SelectedItem as Workspace;
                var page = await session.ConnectAsync(selected, null, ct);
                workspaceUrl.Text = selected.Url.AbsoluteUri;
                ShowPage(page, false); SaveSettings(); tabs.SelectedIndex = 1;
            }, "Connecting to workspace…");
            workspaces.SelectionChanged += (_, __) => UpdateState();
            stack.Children.Add(new Expander { Header = "Browser sign-in", Content = browser, Margin = new Thickness(0, 18, 0, 0) });
            busyControls.AddRange(new Control[] { workspaceUrl, accessToken, tokenConnect, clientId, regions, signin, workspaces });
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        private UIElement BuildProjects()
        {
            var layout = new DockPanel { Margin = new Thickness(10) };
            var buttons = new WrapPanel();
            foreach (var button in new[] { refresh, more, useProject, open, disconnect }) buttons.Children.Add(button);
            DockPanel.SetDock(buttons, Dock.Top); layout.Children.Add(buttons);
            var grid = new DataGrid { ItemsSource = projects, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
            projectGrid = grid;
            grid.SelectionChanged += (_, __) => UpdateState();
            busyControls.Add(grid);
            grid.Columns.Add(new DataGridTextColumn { Header = "Project", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new System.Windows.Data.Binding("Description"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "VCS", Binding = new System.Windows.Data.Binding("VersionControl"), Width = DataGridLength.Auto });
            layout.Children.Add(grid);
            refresh.Click += async (_, __) => await RunAsync(async ct => ShowPage(await session.ProjectsAsync(null, ct), false), "Refreshing projects…");
            more.Click += async (_, __) => await RunAsync(async ct => ShowPage(await session.ProjectsAsync(cursor, ct), true), "Loading more projects…");
            open.Click += (_, __) => { if (session.Connected != null) TryOpen(session.Connected.Url.AbsoluteUri); };
            disconnect.Click += (_, __) => Disconnect();
            useProject.Click += (_, __) =>
            {
                if (!(grid.SelectedItem is CloudProject project) || string.IsNullOrEmpty(project.RepositoryUrl)) return;
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
            configuration.Children.Add(Label("Use a project from the Projects tab, or enter its Git URL and Workspace token directly. Direct Git does not query the workspace API."));
            configuration.Children.Add(Label("Altium 365 repository URL")); configuration.Children.Add(gitRemote);
            configuration.Children.Add(Label("Workspace access token (direct Git only; kept in memory)")); configuration.Children.Add(gitToken);
            var clearToken = Button("Disconnect / clear token"); configuration.Children.Add(clearToken);
            clearToken.Click += (_, __) => Disconnect();
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
            configuration.Children.Add(new Expander { Header = "Git installation", Content = advanced });
            settings.Children.Add(new Expander { Header = "Repository and credentials", IsExpanded = true, Content = new ScrollViewer { Content = configuration, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            var buttons = new WrapPanel();
            foreach (var button in new[] { clone, gitRefresh, pull, push, openProject }) buttons.Children.Add(button);
            settings.Children.Add(buttons); settings.Children.Add(gitSummary);
            DockPanel.SetDock(settings, Dock.Top); layout.Children.Add(settings);
            var commitArea = new StackPanel();
            commitArea.Children.Add(Label("Commit message (uses your configured Git author name and email)")); commitArea.Children.Add(commitMessage); commitArea.Children.Add(commit);
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
                await git.CloneAsync(gitRemote.Text, gitFolder.Text, await GitTokenAsync(ct), ct);
                await RefreshGitAsync(ct); SaveSettings(); status.Text = "Project cloned. Open it to begin editing.";
            }, "Cloning project…");
            gitRefresh.Click += async (_, __) => await RunAsync(async ct => { await RefreshGitAsync(ct); SaveSettings(); }, "Reading working-copy status…");
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
            openProject.Click += (_, __) =>
            {
                try
                {
                    using (var dialog = new System.Windows.Forms.OpenFileDialog { InitialDirectory = gitFolder.Text, Filter = "Altium PCB project|*.PrjPcb", CheckFileExists = true })
                        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) openProjectAction(dialog.FileName);
                }
                catch { status.Text = "Unable to open the project. Open its PrjPcb file from Altium's File menu."; }
            };
            busyControls.AddRange(new Control[] { gitRemote, gitFolder, gitToken, gitExecutable, commitMessage, chooseFolder, chooseGit, clearToken, changesGrid });
            return layout;
        }
        private WorkspaceGit Git() => new WorkspaceGit(new ProcessGitRunner(gitExecutable.Text));
        private void Disconnect()
        {
            session.Disconnect(); accessToken.Clear(); gitToken.Clear(); gitProject = null; gitWorkspace = null;
            projects.Clear(); hasNext = false; cursor = null; status.Text = "Disconnected. Git credentials cleared."; UpdateState();
        }
        private async Task<string> GitTokenAsync(CancellationToken cancellation)
        {
            if (gitProject != null && ReferenceEquals(session.Connected, gitWorkspace) && gitWorkspace != null && gitRemote.Text == gitProject.RepositoryUrl)
                return await session.AccessTokenAsync(cancellation);
            return gitToken.Password;
        }
        private void ClearWorkingStatus()
        {
            workingStatus = null; changes.Clear(); gitSummary.Text = "Clone the project or refresh the selected working copy."; UpdateState();
        }
        private async Task RefreshGitAsync(CancellationToken cancellation)
        {
            workingStatus = await Git().StatusAsync(gitRemote.Text, gitFolder.Text, cancellation);
            changes.Clear(); foreach (var change in workingStatus.Changes) changes.Add(change);
            gitSummary.Text = "Branch: " + workingStatus.Branch + " · " + changes.Count + " changed files. Save documents in Altium before committing.";
        }
        private void ShowPage(ProjectPage page, bool append)
        {
            if (!append) projects.Clear();
            var known = new HashSet<string>(projects.Select(p => p.Id), StringComparer.Ordinal);
            foreach (var project in page.Projects) if (known.Add(project.Id)) projects.Add(project);
            cursor = page.Cursor; hasNext = page.HasNext;
            status.Text = projects.Count + (projects.Count == 1 ? " project loaded." : " projects loaded.") + (hasNext ? " More available." : "");
        }
        private async Task RunAsync(Func<CancellationToken, Task> action, string message)
        {
            if (disposed || operation != null) return;
            var pending = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            operation = pending; status.Text = message; UpdateState();
            try { await action(pending.Token); }
            catch (OperationCanceledException) { if (!disposed) status.Text = "Operation cancelled or timed out. Refresh Git status before retrying a Git operation."; }
            catch (Exception error)
            {
                if (!disposed) status.Text = FriendlyError(error);
            }
            finally { operation = null; pending.Dispose(); if (!disposed) UpdateState(); }
        }
        internal static string FriendlyError(Exception error)
        {
            if (error is WorkspaceAuthenticationException) return "Altium sign-in failed. Check your registered client ID and complete browser sign-in.";
            if (error is HttpRequestException) return "Cannot reach Altium 365. Check your connection and workspace address.";
            if (error is IOException || error is UnauthorizedAccessException) return "Cannot read or write the working copy. Check folder permissions and close any documents using those files.";
            // API errors above contain only fixed messages; authentication response bodies never enter the UI or logs.
            if (error is ArgumentException || error is InvalidOperationException) return error.Message;
            return "The connection could not be completed. Disconnect and try again.";
        }
        private void UpdateState()
        {
            bool busy = operation != null;
            foreach (var control in busyControls) control.IsEnabled = !busy;
            cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            connect.IsEnabled = !busy && session.SignedIn && workspaces.SelectedItem is Workspace;
            refresh.IsEnabled = open.IsEnabled = disconnect.IsEnabled = !busy && session.Connected != null;
            more.IsEnabled = !busy && session.Connected != null && hasNext;
            useProject.IsEnabled = !busy && session.Connected != null && projectGrid?.SelectedItem is CloudProject project && !string.IsNullOrEmpty(project.RepositoryUrl);
            clone.IsEnabled = gitRefresh.IsEnabled = !busy;
            pull.IsEnabled = push.IsEnabled = !busy && workingStatus?.Branch == "master";
            commit.IsEnabled = !busy && workingStatus?.Branch == "master" && changes.Count > 0;
            openProject.IsEnabled = !busy && workingStatus != null && openProjectAction != null;
            connection.Text = session.Connected == null ? "Disconnected" : "Connected · " + session.Connected.Name;
            connection.Foreground = session.Connected == null ? Brushes.DimGray : Brushes.DarkGreen;
        }
        private static void OpenBrowser(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        private void TryOpen(string url) { try { OpenBrowser(url); } catch { status.Text = "Unable to open the browser."; } }
        private void LoadSettings()
        {
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(settingsPath));
                if (saved.TryGetValue("workspaceUrl", out var url) && !string.IsNullOrWhiteSpace(url)) workspaceUrl.Text = WorkspaceApi.WorkspaceUrl(url).AbsoluteUri;
                if (saved.TryGetValue("clientId", out var id)) clientId.Text = id;
                if (saved.TryGetValue("gitRemote", out var remote) && !string.IsNullOrWhiteSpace(remote)) gitRemote.Text = WorkspaceGit.ValidateRemote(remote);
                if (saved.TryGetValue("gitFolder", out var folder)) gitFolder.Text = folder;
                if (saved.TryGetValue("gitExecutable", out var executable)) gitExecutable.Text = executable;
            }
            catch { }
        }
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new Dictionary<string, string> { ["workspaceUrl"] = workspaceUrl.Text, ["clientId"] = clientId.Text, ["gitRemote"] = gitRemote.Text, ["gitFolder"] = gitFolder.Text, ["gitExecutable"] = gitExecutable.Text }));
            }
            catch { /* Preferences are optional; credentials are never persisted. */ }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; operation?.Cancel(); accessToken.Clear(); gitToken.Clear(); session.Disconnect();
            apiHttp.Dispose(); authHttp.Dispose();
        }
    }
}
