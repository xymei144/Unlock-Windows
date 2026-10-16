using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UnlockServer.Models;
using UnlockServer.Services;
using UnlockServer.Views;

namespace UnlockServer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ConfigService _configService;
        private UnlockManager _unlockManager;
        private AppSettings _settings;
        private string _statusKey = "T.Status.Ready";
        private object[] _statusArgs;
        private string _statusText = LangService.T("T.Status.Ready");
        private string _currentRssi = LangService.T("T.Status.Idle");
        private bool _isDeviceInRange;
        private bool _isConnected;
        private bool _isMonitoring;
        private int _selectedTab = App.PendingTab;
        private string _localStatusKey = "T.Local.NotInstalled";
        private string _localUnlockStatus = LangService.T("T.Local.NotInstalled");
        private readonly DispatcherTimer _autoSaveTimer;
        private bool _suspendAutoSave;

        public ObservableCollection<BoundDevice> BoundDevices { get; } = new ObservableCollection<BoundDevice>();

        public string AppVersion { get; } = FormatAppVersion();

        public string WindowTitle => LangService.T("T.App.Title") + " " + AppVersion;

        private static string FormatAppVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "" : "v" + version.ToString(3);
        }

        public AppSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string CurrentRssi
        {
            get => _currentRssi;
            set => SetProperty(ref _currentRssi, value);
        }

        public bool IsDeviceInRange
        {
            get => _isDeviceInRange;
            set => SetProperty(ref _isDeviceInRange, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public bool IsMonitoring
        {
            get => _isMonitoring;
            set
            {
                if (SetProperty(ref _isMonitoring, value))
                    OnPropertyChanged(nameof(MonitorButtonText));
            }
        }

        public int SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsTabDevices));
                    OnPropertyChanged(nameof(IsTabUnlock));
                    OnPropertyChanged(nameof(IsTabRules));
                }
            }
        }

        public bool IsTabDevices => SelectedTab == 0;
        public bool IsTabUnlock => SelectedTab == 1;
        public bool IsTabRules => SelectedTab == 2;

        public string MonitorButtonText =>
            LangService.T(IsMonitoring ? "T.Btn.StopMonitor" : "T.Btn.StartMonitor");

        public bool HasNoDevices => BoundDevices.Count == 0;

        public bool HasMultipleDevices => BoundDevices.Count > 1;

        public string InstallButtonText =>
            LangService.T(LocalUnlock.IsProviderRegistered() ? "T.Btn.UpdatePassword" : "T.Btn.EnableLocal");

        public string SetupHint
        {
            get
            {
                if (BoundDevices.Count == 0)
                    return LangService.T("T.Hint.AddDevice");
                if (!LocalUnlock.CanUnlock())
                    return LangService.T("T.Hint.SetPassword");
                if (!Settings.AutoLock && !Settings.AutoUnlock)
                    return LangService.T("T.Hint.OpenRules");
                return "";
            }
        }

        public bool HasSetupHint => !string.IsNullOrEmpty(SetupHint);

        public string LocalUnlockStatus
        {
            get => _localUnlockStatus;
            set => SetProperty(ref _localUnlockStatus, value);
        }

        public bool IsLocalUnlockReady => LocalUnlock.CanUnlock();

        public bool CanUninstallLocalUnlock =>
            LocalUnlock.IsProviderRegistered() || LocalUnlock.HasSavedCredential();

        public bool CanTestUnlock => LocalUnlock.IsProviderRegistered();

        public ICommand SearchDeviceCommand { get; }
        public ICommand LockScreenCommand { get; }
        public ICommand ToggleMonitoringCommand { get; }
        public ICommand ShowWindowCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand TestServerCommand { get; }
        public ICommand SelectTabCommand { get; }
        public ICommand RemoveDeviceCommand { get; }
        public ICommand RenameDeviceCommand { get; }
        public ICommand InstallLocalUnlockCommand { get; }
        public ICommand UninstallLocalUnlockCommand { get; }
        public ICommand TestUnlockCommand { get; }

        public MainViewModel()
        {
            _configService = ConfigService.Instance;
            _settings = _configService.LoadSettings();

            if (string.IsNullOrEmpty(_settings.Username))
                _settings.Username = Environment.UserName;

            foreach (var d in _settings.Devices ?? Enumerable.Empty<BoundDevice>())
            {
                AttachDevice(d);
                BoundDevices.Add(d);
            }

            BoundDevices.CollectionChanged += OnBoundDevicesChanged;

            _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _autoSaveTimer.Tick += (s, e) =>
            {
                _autoSaveTimer.Stop();
                PersistSettings(false);
            };
            Settings.PropertyChanged += SettingsOnPropertyChanged;

            SearchDeviceCommand = new RelayCommand(SearchDevice);
            LockScreenCommand = new RelayCommand(LockScreen);
            ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
            ShowWindowCommand = new RelayCommand(ShowWindow);
            ExitCommand = new RelayCommand(ExitApplication);
            TestServerCommand = new RelayCommand(TestServer);
            SelectTabCommand = new RelayCommand(p =>
            {
                if (p != null && int.TryParse(p.ToString(), out var tab))
                    SelectedTab = tab;
            });
            RemoveDeviceCommand = new RelayCommand(RemoveDevice);
            RenameDeviceCommand = new RelayCommand(RenameDevice);
            InstallLocalUnlockCommand = new RelayCommand(InstallLocalUnlock);
            UninstallLocalUnlockCommand = new RelayCommand(UninstallLocalUnlock);
            TestUnlockCommand = new RelayCommand(TestUnlock, _ => CanTestUnlock);

            LangService.LanguageChanged += OnLanguageChanged;

            RefreshLocalUnlockStatus();
            InitializeUnlockManager();
        }

        /// <summary>语言切换后，把已经生成好的字符串全部重算一遍</summary>
        private void OnLanguageChanged()
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                StatusText = LangService.F(_statusKey, _statusArgs);
                ApplyLocalStatusKey();
                foreach (var d in BoundDevices) d.RefreshLocalized();
                RefreshSetupHint();
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(InstallButtonText));
                OnPropertyChanged(nameof(MonitorButtonText));
                OnPropertyChanged(nameof(CurrentRssi));
            });
        }

        private void SetStatus(string key, params object[] args)
        {
            _statusKey = key;
            _statusArgs = args;
            StatusText = LangService.F(key, args);
        }

        private void ApplyLocalStatusKey()
        {
            LocalUnlockStatus = LangService.T(_localStatusKey);
        }

        private void SettingsOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suspendAutoSave) return;
            if (e.PropertyName == nameof(AppSettings.Devices)) return;
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        private void PersistSettings(bool showError)
        {
            try
            {
                _suspendAutoSave = true;
                Settings.Devices = BoundDevices.ToList();
                if (!_configService.SaveSettings(Settings))
                {
                    if (showError) MessageDialog.ShowError(LangService.T("T.Msg.SaveFailed"));
                    return;
                }

                WanClient.reloadConfig();
                if (Settings.UseLocalUnlock &&
                    !string.IsNullOrEmpty(Settings.Username) &&
                    !string.IsNullOrEmpty(Settings.Password))
                {
                    LocalUnlock.SaveCredential(Settings.Username, Settings.Password);
                }

                ApplySettingsToManager();
                RefreshLocalUnlockStatus();
                RefreshSetupHint();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"保存设置失败: {ex.Message}");
                if (showError) MessageDialog.ShowError(LangService.T("T.Msg.SaveError"));
            }
            finally
            {
                _suspendAutoSave = false;
            }
        }

        private void TestServer(object parameter)
        {
            try
            {
                _configService.SaveSettings(Settings);
                WanClient.reloadConfig();
                if (WanClient.TestServer(out var message))
                    MessageDialog.ShowSuccess(message);
                else
                    MessageDialog.ShowWarning(message);
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(LangService.T("T.Msg.TestConnectionFailed") + ex.Message);
            }
        }

        private void InstallLocalUnlock(object parameter)
        {
            try
            {
                if (string.IsNullOrEmpty(Settings.Username) || string.IsNullOrEmpty(Settings.Password))
                {
                    MessageDialog.ShowWarning(LangService.T("T.Msg.NeedCredentials"));
                    return;
                }

                PersistSettings(false);
                LocalUnlock.SaveCredential(Settings.Username, Settings.Password);

                if (LocalUnlock.IsProviderRegistered())
                {
                    RefreshLocalUnlockStatus();
                    MessageDialog.ShowSuccess(LangService.T("T.Msg.CredUpdated"));
                    return;
                }

                if (!MessageDialog.ShowConfirm(LangService.T("T.Msg.InstallConfirm")))
                    return;

                if (LocalUnlock.RelaunchElevatedToInstall())
                {
                    RefreshLocalUnlockStatus();
                    MessageDialog.ShowSuccess(LangService.T("T.Msg.InstallDone"));
                }
                else
                {
                    RefreshLocalUnlockStatus();
                    MessageDialog.ShowWarning(LangService.T("T.Msg.InstallIncomplete"));
                }
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(LangService.T("T.Msg.InstallFailed") + ex.Message);
            }
        }

        private void UninstallLocalUnlock(object parameter)
        {
            try
            {
                if (!CanUninstallLocalUnlock)
                {
                    MessageDialog.ShowWarning(LangService.T("T.Msg.NotInstalled"));
                    return;
                }

                if (!MessageDialog.ShowConfirm(LangService.T("T.Msg.UninstallConfirm")))
                    return;

                var ok = LocalUnlock.RelaunchElevatedToUninstall();
                Settings.UseLocalUnlock = false;
                PersistSettings(false);
                RefreshLocalUnlockStatus();

                if (ok || !LocalUnlock.IsProviderRegistered())
                    MessageDialog.ShowSuccess(LangService.T("T.Msg.UninstallDone"));
                else
                    MessageDialog.ShowWarning(LangService.T("T.Msg.UninstallIncomplete"));
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(LangService.T("T.Msg.UninstallFailed") + ex.Message);
            }
        }

        private void TestUnlock(object parameter)
        {
            try
            {
                if (!MessageDialog.ShowConfirm(LangService.T("T.Msg.TestConfirm")))
                    return;

                if (!string.IsNullOrEmpty(Settings.Username) && !string.IsNullOrEmpty(Settings.Password))
                {
                    PersistSettings(false);
                    LocalUnlock.SaveCredential(Settings.Username, Settings.Password);
                    RefreshLocalUnlockStatus();
                }

                if (_unlockManager == null)
                    InitializeUnlockManager();

                if (!_unlockManager.BeginUnlockTest(5, out var error))
                {
                    MessageDialog.ShowWarning(error);
                    return;
                }

                SetStatus("T.Status.UnlockTesting");
                _unlockManager.UnlockTestFinished = OnUnlockTestFinished;
            }
            catch (Exception ex)
            {
                MessageDialog.ShowError(LangService.T("T.Msg.TestFailed") + ex.Message);
            }
        }

        private void OnUnlockTestFinished(bool ok)
        {
            if (IsMonitoring)
                SetStatus("T.Status.Monitoring");
            else
                SetStatus(ok ? "T.Status.Ready" : "T.Status.Locked");
        }

        private void SearchDevice(object parameter)
        {
            OnRequestSearchDevice?.Invoke(this, 0);
        }

        private void OnBoundDevicesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (BoundDevice d in e.NewItems)
                    AttachDevice(d);
            }
            if (e.OldItems != null)
            {
                foreach (BoundDevice d in e.OldItems)
                    DetachDevice(d);
            }
            OnPropertyChanged(nameof(HasNoDevices));
            RefreshSetupHint();
        }

        private void AttachDevice(BoundDevice device)
        {
            if (device == null) return;
            device.PropertyChanged -= BoundDeviceOnPropertyChanged;
            device.PropertyChanged += BoundDeviceOnPropertyChanged;
        }

        private void DetachDevice(BoundDevice device)
        {
            if (device == null) return;
            device.PropertyChanged -= BoundDeviceOnPropertyChanged;
        }

        private void BoundDeviceOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BoundDevice.Enabled)) return;
            Settings.Devices = BoundDevices.ToList();
            _configService.SaveSettings(Settings);
            SyncBoundDevicesToManager(false);
        }

        private void RemoveDevice(object parameter)
        {
            var device = parameter as BoundDevice;
            if (device == null) return;
            BoundDevices.Remove(device);
            Settings.Devices = BoundDevices.ToList();
            _configService.SaveSettings(Settings);
            SyncBoundDevicesToManager(false);
        }

        /// <summary>给设备起个自己看得懂的名字（苹果设备的广播名是乱码，改的是显示名，不影响识别）</summary>
        private void RenameDevice(object parameter)
        {
            var device = parameter as BoundDevice;
            if (device == null) return;

            var name = MessageDialog.ShowInput(
                LangService.T("T.Msg.RenamePrompt"), LangService.T("T.Msg.RenameTitle"), device.Name);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0 || name == device.Name) return;

            device.Name = name;
            Settings.Devices = BoundDevices.ToList();
            _configService.SaveSettings(Settings);
        }

        public void AddBoundDevice(string address, int bluetoothType)
        {
            var mac = BluetoothDiscover.NormalizeAddress(address);
            if (string.IsNullOrEmpty(mac)) return;

            var name = address ?? "";
            var start = name.LastIndexOf('[');
            if (start > 0) name = name.Substring(0, start);

            var existing = BoundDevices.FirstOrDefault(d =>
                mac.Equals(d.NormalizedAddress, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = name;
                existing.BluetoothType = bluetoothType;
                existing.Enabled = true;
            }
            else
            {
                BoundDevices.Add(new BoundDevice
                {
                    Name = name,
                    Address = mac,
                    BluetoothType = bluetoothType,
                    Enabled = true
                });
            }

            Settings.Devices = BoundDevices.ToList();
            _configService.SaveSettings(Settings);
            SyncBoundDevicesToManager(true);
            if (!IsMonitoring)
                StartMonitoring();
            RefreshSetupHint();

            if (!LocalUnlock.CanUnlock())
                ToastService.Show(LangService.T("T.Msg.AddedNeedPassword"));
            else if (!Settings.AutoLock && !Settings.AutoUnlock)
                ToastService.Show(LangService.T("T.Msg.AddedNeedRules"));
            else
                ToastService.Show(LangService.T("T.Msg.AddedMonitoring"));
        }

        private void LockScreen(object parameter)
        {
            try
            {
                if (_unlockManager?.sessionSwitchClass != null)
                {
                    _unlockManager.sessionSwitchClass.MarkSoftLockRequested();
                    _unlockManager.sessionSwitchClass.isLockBySoft = true;
                }
                _unlockManager?.MarkSoftwareLock();
                WanClient.LockPc();
                SetStatus("T.Status.Locked");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"锁屏失败: {ex.Message}");
            }
        }

        private void ToggleMonitoring(object parameter)
        {
            if (IsMonitoring)
            {
                StopMonitoring();
                return;
            }

            if (BoundDevices.Count == 0)
            {
                SelectedTab = 0;
                MessageDialog.ShowWarning(LangService.T("T.Msg.NeedDevice"));
                return;
            }

            StartMonitoring();
        }

        private void ShowWindow(object parameter) => OnRequestShowWindow?.Invoke(this, EventArgs.Empty);

        private void ExitApplication(object parameter)
        {
            if (MessageDialog.ShowConfirm(LangService.T("T.Msg.ExitConfirm")))
            {
                StopMonitoring();
                OnRequestExit?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Initialize()
        {
            if (BoundDevices.Count > 0)
                StartMonitoring();
            else
            {
                SetStatus("T.Status.Setup");
                CurrentRssi = LangService.T("T.Status.NoDevice");
                IsDeviceInRange = false;
            }
            RefreshSetupHint();
        }

        public void Cleanup() => StopMonitoring();

        /// <summary>主题/语言这类外观设置需要立刻落盘（可能马上要重建窗口）</summary>
        public void SaveAppearance()
        {
            try
            {
                _configService.SaveSettings(Settings);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine("保存外观设置失败: " + ex.Message);
            }
        }

        private void InitializeUnlockManager()
        {
            try
            {
                _unlockManager = new UnlockManager
                {
                    rssiyuzhi = Settings.RssiThreshold,
                    hysteresisDb = Settings.HysteresisDb,
                    presenceTimeout = Settings.PresenceTimeout,
                    isautolock = Settings.AutoLock,
                    isautounlock = Settings.AutoUnlock,
                    manuallock = Settings.ManualLock,
                    manualunlock = Settings.ManualUnlock,
                    lockWhenSeen = Settings.LockWhenSeen,
                    lockDelay = Settings.LockDelay,
                    unlockDelay = Settings.UnlockDelay,
                    actionWarnSeconds = Settings.ActionWarnSeconds,
                    lockOnlyWhenIdle = Settings.LockOnlyWhenIdle,
                    idleLockSeconds = Settings.IdleLockSeconds,
                    requireAllDevices = Settings.RequireAllDevices,
                    useLocalUnlock = Settings.UseLocalUnlock
                };
                _unlockManager.SetBoundDevices(BoundDevices);
                _unlockManager.UpdategRssi = UpdateRssiDisplay;
                _unlockManager.UpdateDevicePresence = UpdateDevicePresence;
                _unlockManager.UnlockTestFinished = OnUnlockTestFinished;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"初始化解锁管理器失败: {ex.Message}");
            }
        }

        private void StartMonitoring()
        {
            try
            {
                if (_unlockManager == null)
                    InitializeUnlockManager();
                SyncBoundDevicesToManager(false);
                _unlockManager?.Start();
                IsMonitoring = true;
                SetStatus("T.Status.Monitoring");
                IsConnected = true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"启动监控失败: {ex.Message}");
                MessageDialog.ShowError(LangService.T("T.Msg.StartMonitorFailed"));
            }
        }

        private void StopMonitoring()
        {
            try
            {
                _unlockManager?.Stop();
                IsMonitoring = false;
                SetStatus("T.Status.Stopped");
                IsConnected = false;
                CurrentRssi = LangService.T("T.Status.Idle");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"停止监控失败: {ex.Message}");
            }
        }

        private void SyncBoundDevicesToManager(bool restartIfNeeded)
        {
            if (_unlockManager == null) return;
            var oldType = _unlockManager.bletype;
            _unlockManager.SetBoundDevices(BoundDevices);
            if (!IsMonitoring) return;
            if (restartIfNeeded || _unlockManager.bletype != oldType)
            {
                _unlockManager.Stop();
                _unlockManager.Start();
            }
        }

        private void UpdateRssiDisplay(string displayText, bool inRange)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                CurrentRssi = displayText;
                IsDeviceInRange = inRange;
            });
        }

        private void UpdateDevicePresence(string address, short rssi, bool inRange, string status)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                var device = BoundDevices.FirstOrDefault(d =>
                    address.Equals(d.NormalizedAddress, StringComparison.OrdinalIgnoreCase));
                if (device == null) return;
                device.Rssi = rssi;
                device.IsInRange = inRange;
                device.StatusText = status;
            });
        }

        private void ApplySettingsToManager()
        {
            if (_unlockManager == null) return;
            _unlockManager.ApplyRuntimeSettings(
                Settings.RssiThreshold,
                Settings.HysteresisDb,
                Settings.PresenceTimeout,
                Settings.LockDelay,
                Settings.UnlockDelay,
                Settings.ActionWarnSeconds,
                Settings.AutoLock,
                Settings.AutoUnlock,
                Settings.ManualLock,
                Settings.ManualUnlock,
                Settings.RequireAllDevices,
                Settings.UseLocalUnlock,
                Settings.LockOnlyWhenIdle,
                Settings.IdleLockSeconds,
                Settings.LockWhenSeen);
            SyncBoundDevicesToManager(false);
        }

        private void RefreshLocalUnlockStatus()
        {
            _localStatusKey = LocalUnlock.StatusKey();
            ApplyLocalStatusKey();
            OnPropertyChanged(nameof(IsLocalUnlockReady));
            OnPropertyChanged(nameof(CanUninstallLocalUnlock));
            OnPropertyChanged(nameof(CanTestUnlock));
            OnPropertyChanged(nameof(InstallButtonText));
            (TestUnlockCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RefreshSetupHint();
        }

        private void RefreshSetupHint()
        {
            OnPropertyChanged(nameof(SetupHint));
            OnPropertyChanged(nameof(HasSetupHint));
            OnPropertyChanged(nameof(HasMultipleDevices));
        }

        public event EventHandler<int> OnRequestSearchDevice;
        public event EventHandler OnRequestShowWindow;
        public event EventHandler OnRequestExit;
    }
}
