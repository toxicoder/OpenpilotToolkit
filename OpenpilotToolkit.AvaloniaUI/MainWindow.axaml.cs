using Avalonia.Controls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octokit;
using OpenpilotSdk.Hardware;
using OpenpilotSdk.OpenPilot;
using OpenpilotSdk.Git;
using Renci.SshNet;
using Renci.SshNet.Common;
using OpenpilotDevice = OpenpilotSdk.Hardware.OpenpilotDevice;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Renci.SshNet.Sftp;
using Avalonia.LogicalTree;
using OpenpilotSdk.OpenPilot.Fork;

namespace OpenpilotToolkit.AvaloniaUI
{
    public partial class MainWindow : Window
    {
        private ConcurrentDictionary<string, DateTime?> _watchedFiles = new ConcurrentDictionary<string, DateTime?>();
        private readonly string _tempExplorerFiles;
        private readonly ConcurrentDictionary<string, Task> _activeTaskList = new ConcurrentDictionary<string, Task>();
        private const string AdbConnectedMessage = "Device in fastboot mode connected";
        private const string AdbDisconnectedMessage = "Device in fastboot mode disconnected";
        private int _connectedFastbootDevices = 0;
        readonly BindingList<OpenpilotDevice> _devices = new BindingList<OpenpilotDevice>();
        private Stack<string> _workingDirectory = new Stack<string>();
        private ShellStream _shellStream = null;
        private OpenpilotDevice _lastExplorerDevice = null;
        private DispatcherTimer _scanTimer;
        // private ChromiumWebBrowser _sshTerminal;
        // private Player _flyPlayer;

        public MainWindow()
        {
            InitializeComponent();
            _tempExplorerFiles = Path.Combine(AppContext.BaseDirectory, "tmp", "explorer");
            this.Loaded += MainWindow_Loaded;
            this.FindControl<Button>("btnRefreshVideos").Click += async (sender, e) => await LoadRoutesAsync();
            this.FindControl<Button>("btnExport").Click += btnExport_Click;
            this.FindControl<Button>("btnExportGpx").Click += btnExportGpx_Click;
            this.FindControl<Button>("btnDeleteRoutes").Click += BtnDeleteRoutesClick;
            this.FindControl<ListBox>("lbRoutes").SelectionChanged += LbRoutesSelectedIndexChanged;

            this.FindControl<Button>("btnUpdate").Click += btnUpdate_Click;
            this.FindControl<Button>("btnReboot").Click += btnReboot_Click;
            this.FindControl<Button>("btnShutdown").Click += btnShutdown_Click;
            this.FindControl<Button>("btnOpenSettings").Click += btnOpenSettings_Click;
            this.FindControl<Button>("btnCloseSettings").Click += btnCloseSettings_Click;
            this.FindControl<Button>("btnFlashPanda").Click += btnFlashPanda_Click;
            this.FindControl<Button>("btnInstallEmu").Click += btnInstallEmu_Click;
            var tcSettings = this.FindControl<TabControl>("tcSettings");
            if (tcSettings != null)
            {
                tcSettings.SelectionChanged += tcSettings_Selected;
            }

            var dgvExplorer = this.FindControl<DataGrid>("dgvExplorer");
            if (dgvExplorer != null)
            {
                dgvExplorer.DoubleTapped += async (sender, e) => await ExplorerCellAction();
            }

            _scanTimer = new DispatcherTimer();
            _scanTimer.Interval = TimeSpan.FromSeconds(5);
            _scanTimer.Tick += async (sender, e) => await ScanDevices();
            _scanTimer.Start();
        }

        private async void MainWindow_Loaded(object sender, EventArgs e)
        {
            await ScanDevices().ConfigureAwait(false);
        }

        private async void tcSettings_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem selectedTab)
            {
                if (selectedTab.Header.ToString() == "Log")
                {
                    var directoryInfo = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));
                    var logFile = directoryInfo.GetFiles("*.txt", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(file => file.LastWriteTime).FirstOrDefault();
                    if (logFile != null)
                    {
                        using (var fileStream = new FileStream(logFile.FullName, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var streamReader = new StreamReader(fileStream))
                        {
                            this.FindControl<TextBox>("txtLog").Text = await streamReader.ReadToEndAsync();
                        }
                    }
                }
                else if (selectedTab.Header.ToString() == "Explorer")
                {
                    if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
                    {
                        if (_lastExplorerDevice == null || openpilotDevice != _lastExplorerDevice)
                        {
                            _workingDirectory.Clear();
                            this.FindControl<TextBox>("txtWorkingDirectory").Text = openpilotDevice.WorkingDirectory;
                            IEnumerable<ISftpFile> files = null;
                            var directories = openpilotDevice.WorkingDirectory.Split("/");
                            foreach (var directory in directories)
                            {
                                _workingDirectory.Push(directory);
                            }

                            await Task.Run(async () =>
                            {
                                var currentWorkingDirectory = string.Join("/", _workingDirectory.Reverse());
                                files = await openpilotDevice.EnumerateFilesAsync(currentWorkingDirectory);
                            });
                            this.FindControl<DataGrid>("dgvExplorer").ItemsSource = files.OrderBy(file => file.Name).ToArray();
                        }
                        _lastExplorerDevice = openpilotDevice;
                    }
                    else
                    {
                        _workingDirectory.Clear();
                    }
                }
                else if (selectedTab.Header.ToString() == "Fingerprint")
                {
                    this.FindControl<TextBox>("txtFingerprint").Text = "";
                    if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
                    {
                        if (openpilotDevice.IsAuthenticated)
                        {
                            IEnumerable<Firmware> firmwares = null;
                            this.FindControl<TextBox>("txtFingerprint").Text = "Loading...";
                            await Task.Run(async () =>
                            {
                                firmwares = await openpilotDevice.GetFirmwareVersions();
                            });
                            var sb = new StringBuilder();
                            foreach (var firmware in firmwares)
                            {
                                sb.AppendLine($"ecu = {firmware.Ecu}");
                                sb.AppendLine($"fwVersion = b'{firmware.Version}'");
                                sb.AppendLine($"address = {firmware.Address}");
                                sb.AppendLine($"subAddress = {firmware.SubAddress}");
                                sb.AppendLine("");
                            }
                            this.FindControl<TextBox>("txtFingerprint").Text = sb.ToString();
                        }
                    }
                }
            }
        }

        private async Task ExplorerCellAction()
        {
            var dgvExplorer = this.FindControl<DataGrid>("dgvExplorer");
            if (dgvExplorer.SelectedItem == null)
            {
                return;
            }

            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                var selectedItem = (ISftpFile)dgvExplorer.SelectedItem;
                if (!((selectedItem.IsDirectory && !selectedItem.IsRegularFile) || selectedItem.IsSymbolicLink))
                {
                    var normalizedPath = selectedItem.FullName.Replace('/', Path.DirectorySeparatorChar);
                    normalizedPath = normalizedPath.StartsWith(Path.DirectorySeparatorChar)
                        ? normalizedPath.Substring(1, normalizedPath.Length - 1) : normalizedPath;
                    var outputFilePath = Path.Combine(_tempExplorerFiles, normalizedPath);
                    if (!_watchedFiles.TryAdd(outputFilePath, null))
                    {
                        DateTime? modifiedDate = null;
                        _watchedFiles.TryGetValue(outputFilePath, out modifiedDate);
                        if (modifiedDate == null)
                        {
                            return;
                        }
                        _watchedFiles[outputFilePath] = null;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath));
                    await using (var outputFile = File.Create(outputFilePath))
                    {
                        await using (var stream = await openpilotDevice.OpenReadAsync(selectedItem.FullName))
                        {
                            // var ucDownloadProgress = new ucProgress("↓ " + selectedItem.Name);
                            // this.FindControl<StackPanel>("tlpExplorerTasks").Children.Add(ucDownloadProgress);

                            var buffer = new byte[81920];
                            int bytesRead = 0;
                            var sourceLength = stream.Length;
                            int totalBytesRead = 0;
                            int previousProgress = 0;
                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await outputFile.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                totalBytesRead += bytesRead;
                                var progress = (int)(((double)totalBytesRead / (double)sourceLength) * 100);
                                if (progress != previousProgress)
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() => { /* ucDownloadProgress.Progress = progress; */ });
                                }
                                previousProgress = progress;
                            }
                        }
                    }
                    _watchedFiles[outputFilePath] = File.GetLastWriteTimeUtc(outputFilePath);
                    using (var process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo(outputFilePath)
                        {
                            UseShellExecute = true,
                        };
                        process.Start();
                    }
                    return;
                }
                else
                {
                    var path = selectedItem.Name;
                    var workingDirectory = new Stack<string>(_workingDirectory.Reverse());
                    if (path == "..")
                    {
                        if (workingDirectory.Count > 1)
                        {
                            workingDirectory.Pop();
                        }
                        else
                        {
                            return;
                        }
                    }
                    else if (path != ".")
                    {
                        workingDirectory.Push(path);
                    }
                    var newPath = string.Join("/", workingDirectory.Reverse());
                    newPath = newPath.Length < 1 ? "/" : newPath;
                    IEnumerable<ISftpFile> files = null;
                    var currentWorkingDirectory = string.Join("/", newPath);
                    files = (await openpilotDevice.EnumerateFilesAsync(currentWorkingDirectory)).OrderBy(file => file.Name).ToArray();
                    _workingDirectory = workingDirectory;
                    dgvExplorer.ItemsSource = files;
                    this.FindControl<TextBox>("txtWorkingDirectory").Text = newPath;
                }
            }
        }

        private async Task ScanDevices()
        {
            _devices.Clear();
            this.FindControl<ListBox>("lbRoutes").Items.Clear();

            // wifiConnected.SetEnabled(false);
            this.FindControl<Button>("btnScan").IsEnabled = false;

            try
            {
                var discoveredDevices = new HashSet<OpenpilotDevice>();
                await Task.Run(async () =>
                {
                    var connectionTasks = new List<Task>();
                    try
                    {
                        await foreach (var device in OpenpilotDevice.DiscoverAsync().ConfigureAwait(false))
                        {
                            if (discoveredDevices.Add(device))
                            {
                                connectionTasks.Add(Task.Run(async () =>
                                {
                                    if (device is not UnknownDevice)
                                    {
                                        if (!device.IsAuthenticated)
                                        {
                                            try
                                            {
                                                await device.ConnectSftpAsync().ConfigureAwait(false);
                                            }
                                            catch (SshAuthenticationException ex)
                                            {
                                                Debug.WriteLine($"Authentication failed for {device}: {ex.Message}");
                                            }
                                        }
                                    }

                                    if (device.IsAuthenticated)
                                    {
                                        await Dispatcher.UIThread.InvokeAsync(() =>
                                        {
                                            _devices.Add(device);
                                            if (_devices.Count == 1)
                                            {
                                                // wifiConnected.SetEnabled(true);
                                                this.FindControl<ComboBox>("cmbDevices").SelectedIndex = 0;
                                            }
                                        });
                                    }
                                }));
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Debug.WriteLine("Discovery Timed Out.");
                    }

                    await Task.WhenAll(connectionTasks).ConfigureAwait(false);
                });

                if (discoveredDevices.Count < 1)
                {
                    await MessageBox.Show(this, "No devices were found, please check that SSH is enabled on your device and the device is connected to the network.", "No Devices Found", MessageBox.MessageBoxButtons.Ok);
                }
                else if (_devices.Count < 1 && discoveredDevices.Count > 0)
                {
                    if (await MessageBox.Show(this, $"{discoveredDevices.Count} device(s) found but authentication failed, do you want to start the SSH wizard?", "Authentication Failed", MessageBox.MessageBoxButtons.OkCancel) == MessageBox.MessageBoxResult.Ok)
                    {
                        // ucSshWizard.Reset();
                        // tcSettings.SelectedTab = tpSSH;
                    }
                }
            }
            finally
            {
                this.FindControl<Button>("btnScan").IsEnabled = true;
            }
        }

        private async void btnExport_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                var exportFolder = this.FindControl<TextBox>("txtExportFolder").Text;
                var exportTasks = new List<Tuple<string, Task>>();
                var cameras = new List<Camera>(3);
                if (this.FindControl<CheckBox>("cbFrontCamera").IsChecked == true)
                {
                    if (openpilotDevice.Cameras.TryGetValue(CameraType.Front, out var camera))
                    {
                        cameras.Add(camera);
                    }
                }
                if (this.FindControl<CheckBox>("cbWideCamera").IsChecked == true)
                {
                    if (openpilotDevice.Cameras.TryGetValue(CameraType.Wide, out var camera))
                    {
                        cameras.Add(camera);
                    }
                }
                if (this.FindControl<CheckBox>("cbDriverCamera").IsChecked == true)
                {
                    if (openpilotDevice.Cameras.TryGetValue(CameraType.Driver, out var camera))
                    {
                        cameras.Add(camera);
                    }
                }

                if (cameras.Count < 1)
                {
                    await MessageBox.Show(this, "You must select at least 1 camera to export.", "No Cameras Selected", MessageBox.MessageBoxButtons.Ok);
                    return;
                }

                foreach (var selectedItem in this.FindControl<ListBox>("lbRoutes").SelectedItems)
                {
                    if (selectedItem is Route route)
                    {
                        if (_activeTaskList.ContainsKey(route.Date.ToString()))
                        {
                            continue;
                        }

                        // var ucRoute = new ucTaskProgress(route.ToString(), cameras.Count * 100);
                        // this.FindControl<StackPanel>("tlpTasks").Children.Add(ucRoute);

                        var progressDictionary = cameras.ToDictionary(cam => cam, cam => 0);
                        var progressLock = new SemaphoreSlim(1, 1);
                        var progress = new Progress<OpenpilotSdk.OpenPilot.Camera.Progress>(async (cameraProgress) =>
                        {
                            progressDictionary[cameraProgress.Camera] = cameraProgress.Percent;
                            try
                            {
                                await progressLock.WaitAsync();
                                var currentProgress = progressDictionary.Sum(p => p.Value);
                                // ucRoute.Progress = currentProgress;
                            }
                            finally
                            {
                                progressLock.Release();
                            }
                        });

                        var task = Task.Run(async () =>
                        {
                            await Parallel.ForEachAsync(cameras, async (camera, token) =>
                            {
                                await openpilotDevice.ExportRouteAsync(exportFolder, route, camera, this.FindControl<CheckBox>("cbCombineSegments").IsChecked ?? false, progress);
                            });
                        });
                        exportTasks.Add(new Tuple<string, Task>(route.Date.ToString(), task));
                        _activeTaskList.TryAdd(route.Date.ToString(), task);
                    }
                }

                if (exportTasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(exportTasks.Select(item => item.Item2));
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Error exporting route: {exception.Message}");
                        await MessageBox.Show(this, exception.Message, "Error", MessageBox.MessageBoxButtons.Ok);
                    }

                    foreach (var exportTask in exportTasks)
                    {
                        _activeTaskList.TryRemove(exportTask.Item1, out _);
                    }
                }
            }
        }

        private async Task LoadRoutesAsync()
        {
            var item = this.FindControl<ComboBox>("cmbDevices").SelectedItem;
            if (item is OpenpilotDevice openpilotDevice)
            {
                try
                {
                    this.FindControl<ListBox>("lbRoutes").Items.Clear();
                    await Task.Run(async () =>
                    {
                        await foreach (var route in openpilotDevice.GetRoutesAsync().ConfigureAwait(false))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                this.FindControl<ListBox>("lbRoutes").Items.Add(route);
                                if (this.FindControl<ListBox>("lbRoutes").Items.Count == 1)
                                {
                                    this.FindControl<ListBox>("lbRoutes").SelectedIndex = 0;
                                }
                            });
                        }
                    });
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Error retrieving routes: {exception.Message}");
                    await MessageBox.Show(this, exception.Message, "Error", MessageBox.MessageBoxButtons.Ok);
                    return;
                }
            }
        }

        private async void btnExportGpx_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                var exportTasks = new List<Task>();
                foreach (var selectedItem in this.FindControl<ListBox>("lbRoutes").SelectedItems)
                {
                    if (selectedItem is Route route)
                    {
                        var exportFolder = this.FindControl<TextBox>("txtExportFolder").Text;
                        // var ucRoute = new ucTaskProgress(route.ToString(), route.Segments.Count);
                        // this.FindControl<StackPanel>("tlpTasks").Children.Add(ucRoute);

                        var segmentsProcessed = 0;
                        var progress = new Progress<int>((segmentIndex) =>
                        {
                            Interlocked.Increment(ref segmentsProcessed);
                            // ucRoute.Progress = segmentsProcessed;
                        });

                        exportTasks.Add(Task.Run(async () =>
                        {
                            var fileName = Path.Combine(exportFolder, route + ".gpx");
                            var gpxFile = await openpilotDevice.GenerateGpxFileAsync(route, progress);
                            var gpxString = gpxFile.BuildString(new NetTopologySuite.IO.GpxWriterSettings());
                            await File.WriteAllTextAsync(fileName, gpxString);
                        }));
                    }
                }

                if (exportTasks.Count > 0)
                {
                    await Task.WhenAll(exportTasks);
                }
            }
        }

        private async void BtnDeleteRoutesClick(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                var selectedItems = this.FindControl<ListBox>("lbRoutes").SelectedItems;
                if (selectedItems.Count > 0)
                {
                    if (await MessageBox.Show(this, $"Are you sure you want to delete {selectedItems.Count} routes(s): {Environment.NewLine + string.Join(Environment.NewLine, selectedItems.Cast<object>().Where(item => item is Route).Select(row => row.ToString()))}", "Delete Routes", MessageBox.MessageBoxButtons.OkCancel) != MessageBox.MessageBoxResult.Ok)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }

                await Task.Run(async () =>
                {
                    var deleteTasks = new Dictionary<Route, Task>();
                    foreach (var selectedItem in selectedItems)
                    {
                        if (selectedItem is Route route)
                        {
                            if (_activeTaskList.ContainsKey(route.Date.ToString()))
                            {
                                continue;
                            }
                            deleteTasks.Add(route, openpilotDevice.DeleteRouteAsync(route));
                        }
                    }

                    foreach (var deleteTask in deleteTasks)
                    {
                        await deleteTask.Value;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            this.FindControl<ListBox>("lbRoutes").Items.Remove(deleteTask.Key);
                        });
                    }
                });
            }
        }

        private readonly SemaphoreSlim _routeSemaphoreSlim = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _routeVideo;
        private async void LbRoutesSelectedIndexChanged(object sender, EventArgs e)
        {
            this.FindControl<ComboBox>("cmbDevices").IsEnabled = false;
            this.FindControl<DataGrid>("dgvRouteInfo").ItemsSource = null;

            try
            {
                if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
                {
                    if (this.FindControl<ListBox>("lbRoutes").SelectedItems.Count < 2 && this.FindControl<ListBox>("lbRoutes").SelectedItem is Route route)
                    {
                        var routeInfo = new Dictionary<string, string>
                        {
                            { "Segments", route.Segments.Count.ToString() },
                            { "Start Date", route.Date.ToShortDateString() },
                            { "Start Time", route.Date.ToLongTimeString() }
                        };

                        this.FindControl<DataGrid>("dgvRouteInfo").ItemsSource = routeInfo.ToArray();
                        try
                        {
                            if (route.Segments.Any())
                            {
                                await Task.Run(async () =>
                                {
                                    var cts = new CancellationTokenSource();
                                    await _routeSemaphoreSlim.WaitAsync().ConfigureAwait(false);
                                    try
                                    {
                                        if (_routeVideo != null)
                                        {
                                            // await _routeVideo.CancelAsync().ConfigureAwait(false);
                                        }
                                        _routeVideo = cts;
                                    }
                                    finally
                                    {
                                        _routeSemaphoreSlim.Release();
                                    }
                                    // await flyleafVideoPlayer.PlayRouteAsync(openpilotDevice, route, cts.Token).ConfigureAwait(false);
                                });
                            }
                        }
                        catch (Exception exception)
                        {
                            Debug.WriteLine($"Error playing video: {exception.Message}");
                            await MessageBox.Show(this, exception.Message, "Error", MessageBox.MessageBoxButtons.Ok);
                            this.FindControl<ComboBox>("cmbDevices").IsEnabled = true;
                            return;
                        }
                    }
                }
            }
            finally
            {
                this.FindControl<ComboBox>("cmbDevices").IsEnabled = true;
            }
        }

        private async void btnUpdate_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                EnableRemoteControls(false);

                try
                {
                    ForkResult result = null;
                    var progress = new Progress<InstallProgress>();
                    // using (new ToolkitProgressDialog("Reinstalling fork, please wait", this, progress))
                    // {
                        await Task.Run(async () =>
                        {
                            result = await openpilotDevice.ReinstallOpenpilotAsync(progress);
                        });
                    // }

                    await MessageBox.Show(this, result.Success ? "Reinstall Successful" : $"There was an error during installation: {result.Message}", "Reinstall", MessageBox.MessageBoxButtons.Ok);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Error in fork installer: {exception.Message}");
                    await MessageBox.Show(this, exception.Message, "Error", MessageBox.MessageBoxButtons.Ok);
                    return;
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnReboot_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    bool result = false;
                    // using (new ToolkitProgressDialog("Rebooting Device...", this))
                    // {
                        await Task.Run(async () =>
                        {
                            result = await openpilotDevice.RebootAsync();
                        });
                    // }
                    if (result)
                    {
                        await MessageBox.Show(this, "Rebooted Device.", "Reboot", MessageBox.MessageBoxButtons.Ok);
                    }
                    else
                    {
                        await MessageBox.Show(this, "Failed to Reboot Device.", "Reboot", MessageBox.MessageBoxButtons.Ok);
                    }
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnShutdown_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    bool result = false;
                    // using (new ToolkitProgressDialog("Shutting Down Device...", this))
                    // {
                        await Task.Run(async () =>
                        {
                            result = await openpilotDevice.ShutdownAsync();
                        });
                    // }
                    if (result)
                    {
                        await MessageBox.Show(this, "Shutdown the Device.", "Shutdown", MessageBox.MessageBoxButtons.Ok);
                    }
                    else
                    {
                        await MessageBox.Show(this, "Failed to Shutdown Device.", "Shutdown", MessageBox.MessageBoxButtons.Ok);
                    }
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnOpenSettings_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is Comma2 openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    await Task.Run(async () =>
                    {
                        var result = await openpilotDevice.OpenSettingsAsync();
                    });
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnCloseSettings_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is Comma2 openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    await Task.Run(async () =>
                    {
                        var result = await openpilotDevice.CloseSettingsAsync();
                    });
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnFlashPanda_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    bool result = false;
                    // using (new ToolkitProgressDialog("Flashing Panda", this))
                    // {
                        await Task.Run(async () =>
                        {
                            result = await openpilotDevice.FlashPandaAsync();
                        });
                    // }
                    if (result)
                    {
                        await MessageBox.Show(this, "Flashed Panda.", "Flash Panda", MessageBox.MessageBoxButtons.Ok);
                    }
                    else
                    {
                        await MessageBox.Show(this, "Failed to flash Panda", "Flash Panda", MessageBox.MessageBoxButtons.Ok);
                    }
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private async void btnInstallEmu_Click(object sender, EventArgs e)
        {
            if (this.FindControl<ComboBox>("cmbDevices").SelectedItem is OpenpilotDevice openpilotDevice)
            {
                EnableRemoteControls(false);
                try
                {
                    bool result = false;
                    // using (new ToolkitProgressDialog("Installing Emu...", this))
                    // {
                        await Task.Run(async () =>
                        {
                            result = await openpilotDevice.InstallEmuAsync();
                        });
                    // }
                    if (result)
                    {
                        await MessageBox.Show(this, "Emu Installed.", "Install Emu", MessageBox.MessageBoxButtons.Ok);
                    }
                    else
                    {
                        await MessageBox.Show(this, "Emu Installation Failed.", "Install Emu", MessageBox.MessageBoxButtons.Ok);
                    }
                }
                finally
                {
                    EnableRemoteControls(true);
                }
            }
        }

        private void EnableRemoteControls(bool enable)
        {
            var remoteTab = this.FindControl<TabItem>("tpRemote");
            var remoteButtons = remoteTab.GetLogicalDescendants().OfType<Button>();
            foreach (var button in remoteButtons)
            {
                button.IsEnabled = enable;
            }
        }
    }
}
