Imports PCL.Core.Utils.Exts
Imports PCL.Core.App
Imports PCL.Core.Updater
Imports System.Linq
Imports System.Threading
Imports System.Windows.Input
Imports System.Windows.Threading
Public Class PageLaunchRight
    Implements IRefreshable
    Private ReadOnly McPatchService As New McPatchUpdateService()
    Private McPatchWatcher As DispatcherTimer = Nothing
    Private McPatchLastInstanceKey As String = ""
    Private McPatchLastResult As McPatchCheckResult = Nothing
    Private McPatchCurrentListStatus As McPatchListStatus = McPatchListStatus.FirstFetching
    Private McPatchCurrentRetryCount As Integer = 0
    Private McPatchCurrentError As String = ""
    Private McPatchRefreshing As Integer = 0
    Private McPatchUpdating As Integer = 0
    Public Function ShouldBlockLaunchByMcPatch() As Boolean
        If PanMcPatchUpdate.Visibility <> Visibility.Visible Then Return False
        If Interlocked.CompareExchange(McPatchRefreshing, 0, 0) <> 0 Then Return True
        If Interlocked.CompareExchange(McPatchUpdating, 0, 0) <> 0 Then Return True
        If McPatchLastResult Is Nothing Then Return True
        Return McPatchLastResult.NeedUpdate
    End Function
    Private Sub RefreshLaunchButtonForMcPatch()
        If FrmLaunchLeft IsNot Nothing Then RunInUi(Sub() FrmLaunchLeft.RefreshButtonsUI())
    End Sub

    Private Sub Init() Handles Me.Loaded
        PanBack.ScrollToHome()
        PanScroll = PanBack '不知道为啥不能在 XAML 设置
        PanLog.Visibility = If(ModeDebug, Visibility.Visible, Visibility.Collapsed)
        '社区版提示
        PanHint.Visibility = If(Setup.Get("UiLauncherCEHint"), Visibility.Visible, Visibility.Collapsed)
        LabHint1.Text = $"你正在使用 PCL 社区版的Fork版本！{vbCrLf}此版本单独添加了一个有关服务器更新的小功能!{vbCrLf}"
        InitMcPatchModule()
    End Sub
    Private Sub DisposePage() Handles Me.Unloaded
        If McPatchWatcher IsNot Nothing Then McPatchWatcher.Stop()
    End Sub

    '由于是Fork的就不要太正式了，直接在主页放个提示就行了，主页还经常变动，放在这里比较稳妥
    Private Sub BtnHintClose_Click(sender As Object, e As EventArgs) Handles BtnHintClose.Click
        AniDispose(PanHint, True)
        States.Hint.CEMessage = False
    End Sub
    Private Sub InitMcPatchModule()
        If McPatchWatcher Is Nothing Then
            McPatchWatcher = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(2)}
            AddHandler McPatchWatcher.Tick, AddressOf McPatchWatcher_Tick
        End If
        McPatchWatcher.Start()
        RefreshMcPatchModule(True)
    End Sub
    Private Sub McPatchWatcher_Tick(sender As Object, e As EventArgs)
        RefreshMcPatchModule(False)
    End Sub
    Private Sub RefreshMcPatchModule(force As Boolean)
        Dim context = BuildMcPatchContext()
        If context Is Nothing Then
            PanMcPatchUpdate.Visibility = Visibility.Collapsed
            McPatchLastInstanceKey = ""
            McPatchLastResult = Nothing
            RefreshLaunchButtonForMcPatch()
            Return
        End If

        PanMcPatchUpdate.Visibility = Visibility.Visible
        Dim instanceKey = context.RootPath & "|" & context.SelectedVersionName
        If Not force AndAlso instanceKey = McPatchLastInstanceKey Then Return
        If Interlocked.CompareExchange(McPatchUpdating, 0, 0) <> 0 Then Return

        McPatchLastInstanceKey = instanceKey
        StartMcPatchCheck(context, instanceKey)
    End Sub
    Private Function BuildMcPatchContext() As McPatchUpdateContext
        Dim instance = McInstanceSelected
        If instance Is Nothing Then Return Nothing

        Dim selectedName As String = instance.Name
        Dim instancePath As String = instance.PathInstance
        Dim rootPath As String = instance.PathIndie
        If String.IsNullOrWhiteSpace(rootPath) Then Return Nothing

        Dim configPath As String = instancePath & "mcpatch.config.json"
        If Not File.Exists(configPath) AndAlso
           Not String.Equals(instancePath, rootPath, StringComparison.OrdinalIgnoreCase) Then
            Dim rootConfigPath As String = rootPath & "mcpatch.config.json"
            If File.Exists(rootConfigPath) Then configPath = rootConfigPath
        End If
        If Not File.Exists(configPath) Then Return Nothing

        Dim endpoints = McPatchService.LoadEndpointOptions(configPath)
        Return New McPatchUpdateContext With {
            .RootPath = rootPath,
            .SelectedVersionName = selectedName,
            .Endpoints = endpoints
        }
    End Function
    Private Sub StartMcPatchCheck(context As McPatchUpdateContext, instanceKey As String)
        If Interlocked.CompareExchange(McPatchRefreshing, 1, 0) <> 0 Then Return

        McPatchLastResult = Nothing
        McPatchCurrentListStatus = McPatchListStatus.FirstFetching
        McPatchCurrentRetryCount = 0
        McPatchCurrentError = ""
        BtnMcPatchRetry.Visibility = Visibility.Collapsed
        BtnMcPatchUpdateNow.IsEnabled = False
        ProgressMcPatch.Value = 0
        LabMcPatchCurrentVersion.Text = "当前版本：读取中..."
        LabMcPatchLatestVersion.Text = "最新版本：读取中..."
        LabMcPatchProgress.Text = "正在获取更新列表..."
        UpdateMcPatchLinkText()
        RefreshLaunchButtonForMcPatch()

        RunInNewThread(
        Sub()
            Try
                Dim result = McPatchService.CheckForUpdates(
                    context,
                    Sub(status, retryCount, errorText)
                        RunInUi(
                            Sub()
                                If instanceKey <> McPatchLastInstanceKey Then Exit Sub
                                McPatchCurrentListStatus = status
                                McPatchCurrentRetryCount = retryCount
                                McPatchCurrentError = If(errorText, "")
                                UpdateMcPatchLinkText()
                            End Sub)
                    End Sub)

                RunInUi(
                    Sub()
                        If instanceKey <> McPatchLastInstanceKey Then Exit Sub
                        McPatchLastResult = result
                        RenderMcPatchCheckResult(result)
                        RefreshLaunchButtonForMcPatch()
                    End Sub)
            Catch ex As Exception
                RunInUi(
                    Sub()
                        If instanceKey <> McPatchLastInstanceKey Then Exit Sub
                        McPatchCurrentListStatus = McPatchListStatus.Failed
                        McPatchCurrentError = ex.Message
                        UpdateMcPatchLinkText()
                        BtnMcPatchRetry.Visibility = Visibility.Visible
                        BtnMcPatchUpdateNow.IsEnabled = False
                        LabMcPatchProgress.Text = "更新列表获取失败，可手动重试。"
                        RefreshLaunchButtonForMcPatch()
                    End Sub)
                Log(ex, "[MCPatch] 获取更新列表失败", If(ModeDebug, LogLevel.Debug, LogLevel.Hint))
            Finally
                Interlocked.Exchange(McPatchRefreshing, 0)
            End Try
        End Sub, $"MCPatch 列表刷新 #{GetUuid()}")
    End Sub
    Private Sub RenderMcPatchCheckResult(result As McPatchCheckResult)
        Dim currentVersion = If(String.IsNullOrWhiteSpace(result.CurrentVersion), "未安装", result.CurrentVersion)
        Dim latestVersion = If(String.IsNullOrWhiteSpace(result.LatestVersion), "未知", result.LatestVersion)

        LabMcPatchCurrentVersion.Text = $"当前版本：{currentVersion}"
        LabMcPatchLatestVersion.Text = $"最新版本：{latestVersion}{If(result.NeedUpdate, "（需要更新）", "（已是最新）")}"
        UpdateMcPatchLinkText()

        BtnMcPatchRetry.Visibility = Visibility.Collapsed
        BtnMcPatchUpdateNow.IsEnabled = result.NeedUpdate AndAlso Interlocked.CompareExchange(McPatchUpdating, 0, 0) = 0
        ProgressMcPatch.Value = If(result.NeedUpdate, 0, 1)
        If result.NeedUpdate Then
            Dim firstVersion = result.PendingVersions.FirstOrDefault()
            Dim lastVersion = result.PendingVersions.LastOrDefault()
            LabMcPatchProgress.Text = $"待更新 {result.PendingVersions.Count} 个版本：{firstVersion} → {lastVersion}"
        Else
            LabMcPatchProgress.Text = "当前已是最新，无需更新。"
        End If
    End Sub
    Private Sub UpdateMcPatchLinkText()
        Select Case McPatchCurrentListStatus
            Case McPatchListStatus.FirstFetching
                LabMcPatchLinkState.Text = "列表状态：首次获取中"
            Case McPatchListStatus.Retrying
                LabMcPatchLinkState.Text = $"列表状态：重试中（第 {McPatchCurrentRetryCount} 次）"
            Case McPatchListStatus.Succeeded
                LabMcPatchLinkState.Text = "列表状态：已获得最新列表"
            Case McPatchListStatus.Failed
                Dim detail = If(String.IsNullOrWhiteSpace(McPatchCurrentError), "", $"（{McPatchCurrentError}）")
                LabMcPatchLinkState.Text = "列表状态：重试失败" & detail
            Case Else
                LabMcPatchLinkState.Text = "列表状态：未知"
        End Select
    End Sub
    Private Sub BtnMcPatchRetry_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnMcPatchRetry.Click
        RefreshMcPatchModule(True)
    End Sub
    Private Sub BtnMcPatchUpdateNow_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnMcPatchUpdateNow.Click
        If McLaunchLoader.State = LoadState.Loading OrElse HasRunningMinecraft Then
            Hint("检测到游戏正在运行或启动中，请先关闭游戏后再进行 MCPatch 更新。", HintType.Critical)
            Return
        End If

        Dim context = BuildMcPatchContext()
        If context Is Nothing Then
            PanMcPatchUpdate.Visibility = Visibility.Collapsed
            Return
        End If

        If McPatchLastResult Is Nothing OrElse Not McPatchLastResult.NeedUpdate Then
            Hint("当前没有可用更新。")
            Return
        End If
        If Interlocked.CompareExchange(McPatchUpdating, 1, 0) <> 0 Then Return

        Dim pendingVersions = New List(Of String)(McPatchLastResult.PendingVersions)
        BtnMcPatchUpdateNow.IsEnabled = False
        BtnMcPatchRetry.IsEnabled = False
        ProgressMcPatch.Value = 0
        LabMcPatchProgress.Text = "准备更新..."
        RefreshLaunchButtonForMcPatch()

        RunInNewThread(
        Sub()
            Try
                McPatchService.ApplyUpdates(
                    context,
                    pendingVersions,
                    Sub(progress, message)
                        RunInUi(
                            Sub()
                                ProgressMcPatch.Value = Math.Max(0, Math.Min(1, progress))
                                LabMcPatchProgress.Text = message
                            End Sub)
                    End Sub)
                RunInUi(Sub() Hint("MCPatch 更新完成！", HintType.Finish))
            Catch ex As Exception
                If TypeOf ex Is System.IO.IOException Then
                    RunInUi(Sub() Hint("MCPatch 更新失败：文件被占用，请关闭游戏/Java/文件预览后重试。", HintType.Critical))
                Else
                    RunInUi(Sub() Hint("MCPatch 更新失败：" & ex.Message, HintType.Critical))
                End If
                Log(ex, "[MCPatch] 执行更新失败", If(ModeDebug, LogLevel.Debug, LogLevel.Hint))
            Finally
                Interlocked.Exchange(McPatchUpdating, 0)
                RunInUi(
                    Sub()
                        BtnMcPatchRetry.IsEnabled = True
                        RefreshMcPatchModule(True)
                        RefreshLaunchButtonForMcPatch()
                    End Sub)
            End Try
        End Sub, $"MCPatch 更新执行 #{GetUuid()}")
    End Sub

#Region "主页"

    ''' <summary>
    ''' 刷新主页。
    ''' </summary>
    Private Sub Refresh() Handles Me.Loaded
        RunInNewThread(
        Sub()
            Try
                SyncLock RefreshLock
                    RefreshReal()
                End SyncLock
            Catch ex As Exception
                Log(ex, "加载 PCL 主页自定义信息失败", If(ModeDebug, LogLevel.Msgbox, LogLevel.Hint))
            End Try
        End Sub, $"刷新主页 #{GetUuid()}")
    End Sub
    Private Sub RefreshReal()
        Dim Content As String = ""
        Dim Url As String
        Select Case Setup.Get("UiCustomType")
            Case 1
                '加载本地文件
                Log("[Page] 主页自定义数据来源：本地文件")
                Content = ReadFile(ExePath & "PCL\Custom.xaml") 'ReadFile 会进行存在检测
            Case 2
                Url = Setup.Get("UiCustomNet")
Download:
                '加载联网文件
                If String.IsNullOrWhiteSpace(Url) Then Exit Select
                If Url = Setup.Get("CacheSavedPageUrl") AndAlso File.Exists(PathTemp & "Cache\Custom.xaml") Then
                    '缓存可用
                    Log("[Page] 主页自定义数据来源：联网缓存文件")
                    Content = ReadFile(PathTemp & "Cache\Custom.xaml")
                    '后台更新缓存
                    OnlineLoader.Start(Url)
                Else
                    '缓存不可用
                    Log("[Page] 主页自定义数据来源：联网全新下载")
                    Hint("正在加载主页……")
                    RunInUiWait(Sub() LoadContent("")) '在加载结束前清空页面
                    Setup.Set("CacheSavedPageVersion", "")
                    OnlineLoader.Start(Url) '下载完成后将会再次触发更新
                    Return
                End If
            Case 3
                Select Case Setup.Get("UiCustomPreset")
                    Case 0
                        Log("[Page] 主页预设：你知道吗")
                        Dim hintText As String = PageLaunchRight.GetRandomHint(False)
                        Content = $"
        <local:MyCard Title=""你知道吗？"" Margin=""0,0,0,15"">
            <TextBlock Margin=""25,38,23,15"" FontSize=""13.5"" IsHitTestVisible=""False"" Text=""{hintText}"" TextWrapping=""Wrap"" Foreground=""{{DynamicResource ColorBrush1}}"" />
            <local:MyIconButton Height=""22"" Width=""22"" Margin=""9"" VerticalAlignment=""Top"" HorizontalAlignment=""Right"" 
                EventType=""刷新主页"" EventData=""/""
                Logo=""M875.52 148.48C783.36 56.32 655.36 0 512 0 291.84 0 107.52 138.24 30.72 332.8l122.88 46.08C204.8 230.4 348.16 128 512 128c107.52 0 199.68 40.96 271.36 112.64L640 384h384V0L875.52 148.48zM512 896c-107.52 0-199.68-40.96-271.36-112.64L384 640H0v384l148.48-148.48C240.64 967.68 368.64 1024 512 1024c220.16 0 404.48-138.24 481.28-332.8L870.4 645.12C819.2 793.6 675.84 896 512 896z"" />
        </local:MyCard>"
                    Case 1
                        Log("[Page] 主页预设：预设 回声洞 是已被移除的主页预设")
                        MyMsgBox("回声洞 因为只有空壳因此已被移除，请前往设置选择其他预设主页", "提示")
                        Return
                    Case 2
                        Log("[Page] 主页预设：Minecraft 新闻")
                        Url = "https://pcl.mcnews.thestack.top"
                        GoTo Download
                    Case 3
                        Log("[Page] 主页预设：简单主页")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/MFn233/Custom.xaml"
                        GoTo Download
                    Case 4
                        Log("[Page] 主页预设：每日整合包推荐")
                        Url = "https://pclsub.sodamc.com/"
                        GoTo Download
                    Case 5
                        Log("[Page] 主页预设：Minecraft 皮肤推荐")
                        Url = "https://forgepixel.com/pcl_sub_file"
                        GoTo Download
                    Case 6
                        Log("[Page] 主页预设：OpenBMCLAPI 仪表盘 Lite")
                        Url = "https://pcl-bmcl.milu.ink/"
                        GoTo Download
                    Case 7
                        Log("[Page] 主页预设：主页市场")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/JingHai-Lingyun/Custom.xaml"
                        GoTo Download
                    Case 8
                        Log("[Page] 主页预设：更新日志")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/Joker2184/UpdateHomepage.xaml"
                        GoTo Download
                    Case 9
                        Log("[Page] 主页预设：PCL 新功能说明书")
                        Url = "https://raw.gitcode.com/WForst-Breeze/WhatsNewPCL/raw/main/Custom.xaml"
                        GoTo Download
                    Case 10
                        Log("[Page] 主页预设：OpenMCIM Dashboard")
                        Url = "https://files.mcimirror.top/PCL"
                        GoTo Download
                    Case 11
                        Log("[Page] 主页预设：杂志主页")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/Ext1nguisher/Custom.xaml"
                        GoTo Download
                    Case 12
                        Log("[Page] 主页预设：PCL GitHub 仪表盘")
                        Url = "https://ddf.pcl-community.org/Custom.xaml"
                        GoTo Download
                    Case 13
                        Log("[Page] 主页预设：Minecraft 更新摘要")
                        Url = "https://raw.gitcode.com/ENC_Euphony/PCL-AI-Summary-HomePage/raw/master/Custom.xaml"
                        GoTo Download
                    Case 14
                        Log("[Page] 主页预设：PCL CE 公告栏")
                        Url = "https://s3.pysio.online/pcl2-ce/apiv2/pages/announce.xaml"
                        GoTo Download
                    Case 15
                        Log("[Page] 主页预设：Minecraft 信息流")
                        RunInUiWait(
                            Sub()
                                If FrmHomepageNews Is Nothing Then FrmHomepageNews = New PageHonepageNewsView()
                                PanCustom.Children.Clear()
                                PanCustom.Children.Add(FrmHomepageNews)
                            End Sub)
                        Return
                End Select
        End Select
        RunInUi(Sub() LoadContent(Content))
    End Sub
    Private RefreshLock As New Object

    Public Shared Function GetRandomHint(Optional enableLengthLimit As Boolean = False) As String
        '优先尝试外部文件
        Dim externalPath As String = ExePath & "PCL\hints.txt"
        If File.Exists(externalPath) Then
            Try
                Dim lines = File.ReadAllLines(externalPath).Where(Function(l) Not String.IsNullOrWhiteSpace(l)).Select(Function(l) l.Trim()).ToArray()
                If lines.Length > 0 Then
                    Dim validHints As String() = lines
                    If enableLengthLimit Then
                        validHints = lines.Where(Function(l) l.Length < 50).ToArray()
                        If validHints.Length = 0 Then
                            validHints = lines
                            Log("[Page] 外部 hints.txt 中没有字数小于50的提示，已取消字数限制", LogLevel.Debug)
                        End If
                    End If

                    Dim hint = validHints(New Random().Next(validHints.Length))
                    hint = hint.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("""", "&quot;")
                    Return hint
                End If
                Log("[Page] 外部 hints.txt 文件为空", LogLevel.Debug)
                Return "PCL CE 是由 PCL-Community 开发的 PCL 社区衍生版本"
            Catch ex As Exception
                Log(ex, "[Page] 读取外部 hints.txt 失败", LogLevel.Hint)
            End Try
        End If
        '回退到嵌入式资源
        Try
            Using reader As New System.IO.StreamReader(Application.GetResourceStream(New Uri("pack://application:,,,/Plain Craft Launcher 2;component/Resources/hints.txt", UriKind.Absolute)).Stream)
                Dim lines = reader.ReadToEnd().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).Where(Function(l) Not String.IsNullOrWhiteSpace(l)).Select(Function(l) l.Trim()).ToArray()
                Dim validHints = If(enableLengthLimit, lines.Where(Function(l) l.Length < 50).ToArray(), lines)
                Dim hint = validHints(New Random().Next(validHints.Length))
                hint = hint.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("""", "&quot;")
                Return hint
            End Using
        Catch ex As Exception
            Log(ex, "[Page] 嵌入式资源 hints.txt 读取失败", LogLevel.Hint)
            Return "PCL CE 是由 PCL-Community 开发的 PCL 社区衍生版本"
        End Try
    End Function

    '联网获取主页文件
    Private OnlineLoader As New LoaderTask(Of String, Integer)("下载主页", AddressOf OnlineLoaderSub) With {.ReloadTimeout = 10 * 60 * 1000}
    Private Sub OnlineLoaderSub(Task As LoaderTask(Of String, Integer))
        Dim Address As String = Task.Input '#3721 中连续触发两次导致内容变化
        Try
            '获取版本校验地址
            Dim VersionAddress As String
            If Address.Contains(".xaml") Then
                VersionAddress = Address.Replace(".xaml", ".xaml.ini")
            Else
                VersionAddress = Address.BeforeFirst("?")
                If Not VersionAddress.EndsWith("/") Then VersionAddress += "/"
                VersionAddress += "version"
                If Address.Contains("?") Then VersionAddress += "?" & Address.AfterFirst("?")
            End If
            '校验版本
            Dim Version As String = ""
            Dim NeedDownload As Boolean = True
            Try
                Version = NetGetCodeByRequestOnce(VersionAddress, Timeout:=10000)
                If Version.Length > 1000 Then Throw New Exception($"获取的主页版本过长（{Version.Length} 字符）")
                Dim CurrentVersion As String = Setup.Get("CacheSavedPageVersion")
                If Version <> "" AndAlso CurrentVersion <> "" AndAlso Version = CurrentVersion Then
                    Log($"[Page] 当前缓存的主页已为最新，当前版本：{Version}，检查源：{VersionAddress}")
                    NeedDownload = False
                Else
                    Log($"[Page] 需要下载联网主页，当前版本：{Version}，检查源：{VersionAddress}")
                End If
            Catch exx As Exception
                Log(exx, $"联网获取主页版本失败", LogLevel.Developer)
                Log($"[Page] 无法检查联网主页版本，将直接下载，检查源：{VersionAddress}")
            End Try
            '实际下载
            If NeedDownload Then
                Dim FileContent As String = NetGetCodeByRequestRetry(Address)
                Log($"[Page] 已联网下载主页，内容长度：{FileContent.Length}，来源：{Address}")
                Setup.Set("CacheSavedPageUrl", Address)
                Setup.Set("CacheSavedPageVersion", Version)
                WriteFile(PathTemp & "Cache\Custom.xaml", FileContent)
            End If
            '要求刷新
            RunInUi(AddressOf Refresh) '不直接调用 Refresh，以防止死循环（#6245）
        Catch ex As Exception
            Log(ex, $"下载主页失败（{Address}）", If(ModeDebug, LogLevel.Msgbox, LogLevel.Hint))
        End Try
    End Sub

    ''' <summary>
    ''' 立即强制刷新主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Public Sub ForceRefresh() Implements IRefreshable.Refresh
        Log("[Page] 要求强制刷新主页")
        ClearCache()
        '实际的刷新
        If FrmMain.PageCurrent.Page = FormMain.PageType.Launch Then
            PanBack.ScrollToHome()
            Refresh()
        Else
            FrmMain.PageChange(FormMain.PageType.Launch)
        End If
    End Sub

    ''' <summary>
    ''' 清空主页缓存信息。
    ''' </summary>
    Private Sub ClearCache()
        LoadedContentHash = -1
        OnlineLoader.Input = ""
        Setup.Set("CacheSavedPageUrl", "")
        Setup.Set("CacheSavedPageVersion", "")
        Log("[Page] 已清空主页缓存")
    End Sub

    ''' <summary>
    ''' 从文本内容中加载主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Private Sub LoadContent(Content As String)
        SyncLock LoadContentLock
            '如果加载目标内容一致则不加载
            Dim Hash = Content.GetHashCode()
            If Hash = LoadedContentHash Then Return
            LoadedContentHash = Hash
            '实际加载内容
            PanCustom.Children.Clear()
            If String.IsNullOrWhiteSpace(Content) Then
                Log($"[Page] 实例化：清空主页 UI，来源为空")
                Return
            End If
            Dim LoadStartTime As Date = Date.Now
            Try
                '修改时应同时修改 PageOtherHelpDetail.Init
                Content = ArgumentReplace(Content)
                Do While Content.Contains("xmlns")
                    Content = Content.RegexReplace("xmlns[^""']*(""|')[^""']*(""|')", "").Replace("xmlns", "")
                Loop
                Content = "<StackPanel xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:sys=""clr-namespace:System;assembly=System.Runtime"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" xmlns:local=""clr-namespace:PCL;assembly=Plain Craft Launcher 2"">" & Content & "</StackPanel>"
                Log($"[Page] 实例化：加载主页 UI 开始，最终内容长度：{Content.Count}")
                PanCustom.Children.Add(GetObjectFromXML(Content))
            Catch ex As Exception
                If ModeDebug Then
                    Log(ex, "加载失败的主页内容：" & vbCrLf & Content)
                    If MyMsgBox(If(TypeOf ex Is UnauthorizedAccessException, ex.Message, $"主页内容编写有误，请根据下列错误信息进行检查：{vbCrLf}{ex.ToString()}"),
                                "加载主页界面失败", "重试", "取消") = 1 Then
                        GoTo Refresh '防止 SyncLock 死锁
                    End If
                Else
                    Log(ex, "加载主页界面失败", LogLevel.Hint)
                End If
                Return
            End Try
            Dim LoadCostTime = (Date.Now - LoadStartTime).Milliseconds
            Log($"[Page] 实例化：加载主页 UI 完成，耗时 {LoadCostTime}ms")
            If LoadCostTime > 3000 Then Hint($"主页加载过于缓慢（花费了 {Math.Round(LoadCostTime / 1000, 1)} 秒），请向主页作者反馈此问题，或暂时停止使用该主页")
        End SyncLock
        Return
Refresh:
        ForceRefresh()
    End Sub
    Private LoadedContentHash As Integer = -1
    Private LoadContentLock As New Object

#End Region

End Class
