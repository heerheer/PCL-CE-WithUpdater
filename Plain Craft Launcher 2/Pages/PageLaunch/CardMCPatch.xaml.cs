using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.MCPatch;

namespace PCL;

/// <summary>启动页右侧的「MCPatch 更新」卡片（蓝图 §6）。</summary>
public partial class CardMCPatch : UserControl
{
    /// <summary>
    /// 启动门禁（蓝图 §6.2）：卡片可见且（正在检查 / 正在更新 / 无结果 / 需要更新）时为 true。
    /// PageLaunchLeft.RefreshButtonsUI 消费。
    /// </summary>
    public static bool IsLaunchBlocked { get; private set; }

    private DispatcherTimer? _timer;
    private string? _lastKey;                 // 实例键 "root|version|endpointKey"
    private string _endpointSignature = "";
    private volatile bool _isChecking;
    private volatile bool _isUpdating;
    private bool _rebuildingCombo;
    private McPatchCheckResult? _result;
    private string _currentVersion = "";      // 本地版本（远程读取前先展示）
    private McPatchListStatus _listStatus;
    private int _statusAttempt;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _updateCts;
    private readonly List<McPatchEndpoint> _endpoints = [];
    private int _selectedEndpoint;
    private string _root = "";
    private string _instancePath = "";
    private string _versionName = "";

    public CardMCPatch()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; // 蓝图 §6.2 自动刷新
                _timer.Tick += (_, _) => Evaluate(false);
            }
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            _checkCts?.Cancel(); // 只取消检查，不中断进行中的更新
        };
    }

    // ---------- 轮询入口（2s 定时器 / 手动重试 / 下拉切换） ----------

    private void Evaluate(bool force)
    {
        if (_isUpdating) return; // 更新进行中跳过（蓝图 §6.2）

        var instance = ModInstanceList.McMcInstanceSelected;
        if (instance is null)
        {
            Hide();
            return;
        }

        _root = instance.PathIndie;
        _instancePath = instance.PathInstance;
        _versionName = instance.Name;

        // 仅当前选中实例存在配置文件时显示（蓝图 §6）
        if (!McPatchConfig.TryLoadEndpoints(_instancePath, _root, out var endpoints))
        {
            Hide();
            return;
        }
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            _lastKey = null; // 重新显示时强制重查
        }

        // 端点列表变化 → 重建下拉（签名比较，尽量按 key 保持选中，蓝图 §6.2）
        var signature = string.Join(";", endpoints.Select(e => e.Key));
        if (signature != _endpointSignature)
        {
            _endpointSignature = signature;
            _endpoints.Clear();
            _endpoints.AddRange(endpoints);
            _selectedEndpoint = Math.Clamp(_selectedEndpoint, 0, _endpoints.Count - 1);
            RebuildCombo();
        }

        // 实例键去重（蓝图 §6.2）
        var key = $"{_root}|{_versionName}|{_endpoints[_selectedEndpoint].Key}";
        if (!force && key == _lastKey) return;
        StartCheck(key);
    }

    private void Hide()
    {
        if (Visibility == Visibility.Collapsed && _lastKey is null) return;
        Visibility = Visibility.Collapsed;
        _lastKey = null;
        _result = null;
        _currentVersion = "";
        _endpointSignature = "";
        _endpoints.Clear();
        _checkCts?.Cancel();
        UpdateLoadMask();
        UpdateGate();
    }

    // ---------- 检查 ----------

    private void StartCheck(string key)
    {
        _lastKey = key;
        _checkCts?.Cancel();
        _checkCts = new CancellationTokenSource();
        var token = _checkCts.Token;
        _isChecking = true;
        _listStatus = McPatchListStatus.FirstFetch;
        _statusAttempt = 0;
        Render();
        UpdateLoadMask();
        UpdateGate();

        var ctx = NewContext();
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var result = McPatchService.Check(ctx, (status, attempt) => ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return; // 忽略过期回调（蓝图 §6.2）
                    _listStatus = status;
                    _statusAttempt = attempt;
                    RenderStatus();
                }), current => ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return; // 忽略过期回调（蓝图 §6.2）
                    // 本地版本先于远程展示（远程加载期间立即可见）
                    _currentVersion = string.IsNullOrEmpty(current) ? "" : current;
                    LabCurrent.Text = Lang.Text("Launch.MCPatch.CurrentVersion",
                        string.IsNullOrEmpty(_currentVersion) ? "-" : _currentVersion);
                    LabLoadCurrent.Text = Lang.Text("Launch.MCPatch.CurrentVersion",
                        string.IsNullOrEmpty(_currentVersion) ? "-" : _currentVersion);
                }), token);
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _result = result;
                    _listStatus = McPatchListStatus.Success;
                    Render();
                });
            }
            catch (Exception ex)
            {
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _listStatus = McPatchListStatus.Failed; // 失败 → 手动重试按钮
                    ModBase.Log(ex, "[MCPatch] 检查更新失败");
                    Render();
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    _isChecking = false;
                    Render();
                    UpdateGate();
                });
            }
        }, "MCPatch Check #" + ModBase.GetUuid());
    }

    // ---------- 更新 ----------

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isUpdating || _isChecking) return;
        // 游戏正在运行 / 启动 → 拦截（蓝图 §6.2）
        if (ModWatcher.hasRunningMinecraft || ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
        {
            HintService.Hint(Lang.Text("Launch.MCPatch.GameRunning"), HintType.Error);
            return;
        }
        var chain = _result?.UpdateChain;
        if (chain is not { Count: > 0 }) return;

        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;
        var key = _lastKey;
        _isUpdating = true;
        BarProgress.Value = 0;
        LabMain.Text = "";
        LabPackage.Text = "";
        SpinnerUpdate.Visibility = Visibility.Visible; // 开始更新后显示旋转圆环
        Render();
        UpdateGate();

        var ctx = NewContext();
        ModBase.RunInNewThread(() =>
        {
            try
            {
                McPatchService.Apply(ctx, chain, progress => ModBase.RunInUi(() =>
                {
                    if (_lastKey != key) return;
                    BarProgress.Value = Math.Clamp(progress.Overall * 100, 0, 100);
                    LabMain.Text = progress.Message;
                    LabPackage.Text = progress.TotalBytes > 0
                        ? $"{progress.DownloadedBytes / 1048576d:0.0} MB / {progress.TotalBytes / 1048576d:0.0} MB"
                        : progress.DownloadedBytes > 0 ? $"{progress.DownloadedBytes / 1048576d:0.0} MB" : "";
                }), token);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Launch.MCPatch.Done"), HintType.Success));
            }
            catch (Exception ex)
            {
                ModBase.RunInUi(() =>
                {
                    ModBase.Log(ex, "[MCPatch] 应用更新失败");
                    // IOException 组给占用文件专属提示，其余给错误信息（蓝图 §6.2）
                    var message = ex is IOException or UnauthorizedAccessException
                        ? Lang.Text("Launch.MCPatch.FileOccupied")
                        : ex.Message;
                    HintService.Hint(message, HintType.Error);
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    _isUpdating = false;
                    SpinnerUpdate.Visibility = System.Windows.Visibility.Collapsed; // 更新完成/失败后恢复
                    UpdateGate();
                    Evaluate(true); // 恢复按钮 + 重新检查（成功后解除门禁）
                });
            }
        }, "MCPatch Apply #" + ModBase.GetUuid());
    }

    private void BtnRetry_Click(object sender, MouseButtonEventArgs e)
    {
        Evaluate(true);
    }

    // ---------- 服务器下拉 ----------

    private void RebuildCombo()
    {
        _rebuildingCombo = true;
        try
        {
            var selectedKey = _selectedEndpoint < _endpoints.Count ? _endpoints[_selectedEndpoint].Key : "";
            ComboServer.Items.Clear();
            foreach (var endpoint in _endpoints)
                ComboServer.Items.Add(new MyComboBoxItem { Content = endpoint.DisplayName });
            var index = _endpoints.FindIndex(x => x.Key == selectedKey);
            _selectedEndpoint = index >= 0 ? index : 0;
            ComboServer.SelectedIndex = _selectedEndpoint;
            ComboServer.Visibility = _endpoints.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _rebuildingCombo = false;
        }
    }

    private void ComboServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingCombo) return;
        if (ComboServer.SelectedIndex >= 0 && ComboServer.SelectedIndex < _endpoints.Count &&
            ComboServer.SelectedIndex != _selectedEndpoint)
        {
            _selectedEndpoint = ComboServer.SelectedIndex;
            Evaluate(true); // 切换触发重新检查（蓝图 §6.2）
        }
    }

    // ---------- 渲染 ----------

    private void Render()
    {
        var needUpdate = _result?.NeedUpdate == true;
        var current = !string.IsNullOrEmpty(_result?.CurrentVersion)
            ? _result.CurrentVersion
            : _currentVersion;
        var latest = _result is null || string.IsNullOrEmpty(_result.LatestVersion)
            ? "-"
            : _result.LatestVersion;
        LabCurrent.Text = Lang.Text("Launch.MCPatch.CurrentVersion", string.IsNullOrEmpty(current) ? "-" : current);
        LabLoadCurrent.Text = LabCurrent.Text;
        LabLatest.Text = Lang.Text("Launch.MCPatch.LatestVersion", latest) + "（" +
                         Lang.Text(needUpdate ? "Launch.MCPatch.NeedUpdate" : "Launch.MCPatch.UpToDate") + "）";
        RenderStatus();
        // 更新按钮仅 NeedUpdate 且未在更新中可点（蓝图 §6.2）
        BtnUpdate.IsEnabled = needUpdate && !_isUpdating && !_isChecking;
        BtnRetry.Visibility = _listStatus == McPatchListStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        // 无需更新时进度条满（蓝图 §6.2）
        BarProgress.Value = _result is not null && !needUpdate ? 100 : 0;
        UpdateLoadMask();
    }

    private void RenderStatus()
    {
        var text = _listStatus switch
        {
            McPatchListStatus.FirstFetch => Lang.Text("Launch.MCPatch.Status.FirstFetch"),
            McPatchListStatus.Retrying => Lang.Text("Launch.MCPatch.Status.Retrying", _statusAttempt),
            McPatchListStatus.Success => Lang.Text("Launch.MCPatch.Status.Success"),
            _ => Lang.Text("Launch.MCPatch.Status.Failed")
        };
        LabStatus.Text = text;
        LabLoadStatus.Text = text;
    }

    // ---------- 初始化加载远程时的「模糊遮罩」 ----------

    private void UpdateLoadMask()
    {
        var show = Visibility == Visibility.Visible && _isChecking;
        LoadMask.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 启动门禁（蓝图 §6.2） ----------

    private void UpdateGate()
    {
        var blocked = Visibility == Visibility.Visible &&
                      (_isChecking || _isUpdating || _result is null || _result.NeedUpdate);
        if (IsLaunchBlocked == blocked) return;
        IsLaunchBlocked = blocked;
        // MCPatch 状态切换后必须刷新启动按钮（蓝图 §6.2）
        ModBase.RunInUi(() => ModMain.frmLaunchLeft?.RefreshButtonsUI());
    }

    private McPatchContext NewContext() => new()
    {
        RootPath = _root,
        InstancePath = _instancePath,
        SelectedVersionName = _versionName,
        Endpoints = _endpoints.ToList(),
        SelectedEndpointIndex = _selectedEndpoint,
        TempDirectory = Path.Combine(ModBase.pathTemp, "McPatch")
    };
}