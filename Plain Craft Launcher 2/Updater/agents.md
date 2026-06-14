## 更新流程
> 对于URL访问出错，默认重试3次；更新包下载不再使用固定超时终止，而是依赖外部取消

### MCPatch 更新流程
1. 读取可设置的URL配置。配置可以是单个对象，也可以是对象数组；当为数组时，UI 会提供可选更新服务器下拉框。
2. 其返回文本数据，每一行代表一个版本包。
3. 解析本地 ./.minecraft 下 mc-patch-version.txt 内部文本，其为当前版本号
4. 对比远程版本列表，列出和最新版相差的所有版本
5. 依次下载并更新（这里每下载完一个版本就进行文件写入、删除、版本号更新，以降低一次性大量更新导致出错的问题）
    - 下载地址由当前选中的服务器配置决定
    - 更新方式为：包为一个压缩包，其中包含文件：`.mcpatch-meta.json`,形如：
    ```json
    {
    "new-folders": [],
    "move-files": [],
    "change-logs": [""],
    "old-files": [".minecraft/mods/DistantHorizons-2.4.5-b-1.20.1-fabric-forge.jar"],
    "old-folders": [],
    "new-files": [{
        "mode": "f",
        "path": ".minecraft/mods/displaydelight-1.4.0.jar",
        "old-hash": "",
        "new-hash": "06d5a5fcdc70d0a1b90017f5695dc65da16c370f",
        "bzipped-hash": "68e51d2d61e30d04002731aac9b211ab63beca4b",
        "raw-hash": "06d5a5fcdc70d0a1b90017f5695dc65da16c370f",
        "raw-length": 1018387
        }]
    }
    ```
    - 你需要删除old-files和old-folders下所有文件
    - 同时，你需要解析所有new files和new folders
    - 此时,所有pacth的jar本质是一个没有bz头的bzip2包，我们需要补上bz头，并将bz压缩包转为一个jar，输出到path指定的位置。
6. PCL支持版本隔离。因此，无论是mc-patch-version.txt还是后续的mods/...等，除了可能位于.minecraft/下，也可能位于.minecraft/versions/{特定版本名}/ 下，而特定版本PCL会进行选择，因此需要获取当前选择的版本。如果没有版本，不显示更新模块。

## 更新UI
更新UI应该是一个位于`Plain Craft Launcher 2\Pages\PageLaunch\PageLaunchRight.xaml`内的一个无法删除的组件。在MCPatch兼容下，其绑定选择版本下的mc-patch文件。

## 编写
1. 编写一个和PCL整体风格一致的更新控件
2. 展示以下内容：
    - 当前版本
    - 最新版本 + 是否需要更新
    - 当前与更新列表的链接状态（已获得最新列表/首次获取中/多次重试中/重试失败）+ 失败后手动重试按钮
    - 立刻更新按钮
    - 更新进度条
