# WeChatViewer Desktop · 微信聊天记录查看器（WinUI 3 桌面版）

一个**只读、本地运行**的微信 4.x 聊天记录查看与导出工具的 **WinUI 3 原生桌面版**：从本机 `Weixin.exe` 的加密数据库中提取并解密数据，用原生 Windows 应用（Fluent Design）浏览全部会话，支持导出为 **HTML（微信气泡样式）/ TXT / CSV**。

> 本项目是 [Ming-QWQ520/wechat-viewer](https://github.com/Ming-QWQ520/wechat-viewer)（Node.js + 网页版）的 **WinUI 3 桌面重构版**，功能与网页版保持一致，并在原 UI 基础上进行了 Fluent 风格美化。

> **仓库只包含代码，不包含任何聊天数据、密钥或头像。** 运行后生成的 `data/` 目录（明文密钥、解密数据库、媒体缓存）已被 `.gitignore` 排除，请勿提交或分享。

> **本项目仅供学习使用，请勿用于商业和违法行为。**

- 平台：Windows 10 1809+ / Windows 11 x64
- 目标：微信 4.x（`Weixin.exe`，已在 4.1 系列数据上验证）
- 协议：[AGPL-3.0](LICENSE)
- 技术栈：**WinUI 3（Windows App SDK 1.6）+ .NET 8（C#，原生 XAML UI 与数据层）** + Go（Win32 进程内存密钥提取）+ C（SILK 语音解码）

---

## 与网页版的差异

| 项目 | 网页版（原项目） | 桌面版（本仓库） |
| --- | --- | --- |
| 界面 | 本地 HTTP 服务 + 浏览器 | **WinUI 3 原生窗口**（Mica 背景材质、Fluent 控件） |
| 启动 | `start.cmd` → Node 服务 → 浏览器 | 直接双击 `WeChatViewer.Desktop.exe` |
| UI 增强 | — | 深色模式自适应、圆角气泡、日期胶囊、语音波形动画、图片灯箱、InfoBar 密钥提示、搜索类型下拉 |
| 数据层 | Node.js `wechat.mjs` | C# `WeChatData`（1:1 移植：分片合并、键集分页、媒体解析） |
| 导出 | `export.mjs` | C# `ExportService`（HTML 模板与内嵌搜索脚本完全一致） |
| 语音解码 | silk-wasm（WASM） | `silkdec.exe`（kn007/silk-v3-decoder C 源码，CI 内编译） |
| 密钥提取 / 图片校验 | Go 工具 | 同样的 Go 工具（CI 内编译，随应用发布） |

---

## 功能（与原项目一致）

- **完整历史**：同一会话的消息表可能分散在 `message_0.db`～`message_3.db` 多个分片，查看器与导出会合并全部时间段，并按 `(sort_seq, local_id)` 键集分页，不丢同毫秒的边界消息
- **微信号**：聊天标题显示当前微信号（`alias`），而不是注册时的原始 `wxid`
- **引用与拍一拍**：引用回复的原文显示在回复文字**下方**，点击可定位到原消息（滚动定位并高亮 3 秒）；拍一拍显示为居中提示
- **发送方样式**：自己发出的文字和引用整体右对齐，保留绿色气泡（`#95ec69`）与引用块的半透明背景；引用中的位置/链接/文件/图片等消息显示为 `[位置]`、`[链接]` 等标签
- **搜索**：查看器与导出的 HTML 都支持关键词搜索，并按文字/图片/语音/视频/文件/表情/链接/引用等类型筛选
- **头像**：从 `head_image.db` 读取发送者与“我”的真实头像；缺失时回退到联系人 `big_head_url`（`wx.qlogo.cn`）下载缓存，最后才用彩色首字
- **表情包**：解密 `business/emoticon` 中的 GIF/PNG/JPEG 表情；本机未缓存的表情按消息里的 CDN 直链自动补全（校验 MD5），也可点「补全表情包」批量下载
- **图片**：解码旧版单字节 XOR、V1、V2 三种微信图片格式；优先使用 `_h` 原图，其次完整中间图，最后缩略图；损坏图片自动回退
- **语音**：从 `media_*.db` 提取 SILK，用 silkdec 解码为 WAV（24kHz 单声道）；带波形动画的微信风格语音气泡，宽度随时长变化
- **视频/文件/链接/位置/名片/通话/系统消息**：按微信样式渲染，可打开本地视频、打开文件、点击链接
- **导出**：HTML 为单文件自包含（头像、图片、语音、表情内嵌），每条消息显示完整发送时间；TXT/CSV 便于检索与 Excel 处理
- **本机只读**：不上传聊天内容；仅“补全表情/头像”会访问消息或联系人中记录的腾讯 CDN，并对下载内容做 MD5 校验

### 桌面版 UI 美化点

- **Mica 背景材质** + 分层侧栏，深浅色主题自适应（跟随系统）
- Fluent 图标按钮、圆角卡片、胶囊式日期分隔、红色未读徽标
- 语音消息播放时三段波形动画（与原网页版 `voicePulse` 一致）
- 图片点击全屏灯箱（Esc 或点击关闭）、引用跳转绿色高亮渐隐
- 首次使用引导页：密钥提取 → 增量解密 → 加载会话，全程可视化日志
- 导出合并为单一下拉菜单，避免按钮堆叠

---

## 快速开始

### 方式一：下载发行版（推荐）

进入 [Releases](https://github.com/Ming-QWQ520/wechat-viewer-desktop/releases) 页面，下载 **v2.2.0** 中的文件，二选一：

| 文件 | 说明 |
| --- | --- |
| `WeChatViewer-Setup-2.2.0-win-x64.exe` | **安装版（推荐）**：双击安装，自动创建开始菜单与桌面快捷方式，控制面板可卸载 |
| `WeChatViewer.Desktop-2.2.0-win-x64.zip` | **绿色版**：解压后运行 `WeChatViewer.Desktop.exe`，无任何安装过程 |

> 也可以在本仓库 **Actions → Build Windows Desktop → Artifacts** 下载每次构建的产物。

> **为什么绿色版有几百个文件？** WinUI 3 应用的 .NET 运行库与 WinUI 原生 DLL 必须与 exe 位于同一目录（官方限制，无法合并为单文件，也不能移入子文件夹），因此绿色版自带完整运行时。想要整洁的体验请使用安装版，所有运行时文件都会收纳进安装目录，桌面上只有一个快捷方式。

### 方式二：本地构建

需要 Windows 10/11 + Visual Studio 2022（含 WinUI 工作负载）或 .NET 8 SDK：

```bat
:: 1. 编译 Go 工具（需要 Go 1.21+）
cd tools\keyextract-src && go build -o ..\wechat-keys.exe . && cd ..
cd imgcheck-src && go build -o ..\imgcheck.exe . && cd ..\..

:: 2. 编译 SILK 语音解码器（需要 gcc / MSVC）
gcc -O2 -w -I tools\silk\interface -I tools\silk\src tools\silk\src\*.c tools\silk\test\Decoder.c -o tools\silkdec.exe -lm

:: 3. 发布应用（自包含，免安装运行时）
dotnet publish WeChatViewer.Desktop\WeChatViewer.Desktop.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

发布产物位于 `WeChatViewer.Desktop\bin\publish\win-x64\`，`tools\` 子目录已包含三个原生工具。

### 3. 使用

1. 保持微信 4.x 已登录；
2. 启动应用，点击「提取密钥并启动」（从 `Weixin.exe` 进程内存提取数据库密钥与账户 `seed`，写入 `data\keys.json`）；
3. 应用自动增量解密数据库到 `data\decrypted\`（约 10–30 秒）并加载会话列表；
4. 以后启动会复用 `data\keys.json`；如密钥变化或缺少表情/图片密钥，删除该文件后重新提取。

---

## 目录结构

```
wechat-viewer-desktop/
  .github/workflows/build.yml        GitHub Actions 编译工作流
  LICENSE                            AGPL-3.0
  WeChatViewer.Desktop/
    App.xaml(.cs)                    应用入口 + 主题色板（深浅色）
    MainWindow.xaml(.cs)             主窗口：侧栏 + 聊天区 + 引导页 + 灯箱
    Models/WeChatModels.cs           会话/联系人/消息模型
    Services/
      SqlCipherDecryptor.cs          SQLCipher 4 页解密（AES-256-CBC + HMAC）
      DatabaseDecryptor.cs           增量解密与清单（对应 decrypt.mjs）
      WeChatData(.cs/.Messages.cs/.Media.cs)
                                     数据层：会话、联系人、消息、搜索、头像、媒体
      ImageDecoder.cs                XOR/V1/V2 图片解码 + 表情 AES 解密
      SilkVoiceDecoder.cs            SILK → WAV（调用 silkdec.exe）
      ExportService.cs               HTML / TXT / CSV 导出（对应 export.mjs）
      StickerThumbs.cs               表情 120px 缩略图（System.Drawing）
      NativeTools.cs                 原生工具调用封装
      KeysStore.cs / TextUtils.cs    密钥加载 / XML·文本工具
    ViewModels/                      MVVM（无第三方依赖）
    Controls/
      MessageRow(.xaml/.cs)          消息气泡渲染（对应 app.js renderMessage）
      SessionRow(.xaml/.cs)          会话列表项
      VoicePlayerService.cs          共享语音播放器
      UiHelpers.cs                   位图解码/LRU 缓存/HSL 配色
    Properties/PublishProfiles/      win-x64 发布配置
  tools/
    keyextract-src/                  Go 密钥提取器（来自原项目）
    imgcheck-src/                    Go 图片完整性校验器（来自原项目）
    silk/                            SILK 解码器 C 源码（kn007/silk-v3-decoder，MIT）
    wechat-keys.exe / imgcheck.exe / silkdec.exe   CI 构建产物（gitignore）
  data/                              运行时生成，已 gitignore（敏感）
```

---

## 工作原理

与原项目完全一致（逆向结论亦适用于本仓库），以下仅列出桌面版特有的部分：

### WinUI 3 数据管线

```
Weixin.exe 进程内存
  └─ tools\wechat-keys.exe（Go，Win32 API）→ data\keys.json
       ├─ db_dir + 每库 enc_key（32 字节十六进制）
       ├─ image_aes_key / image_xor_key
       └─ emoticon_aes_key / account_seed
data\keys.json
  └─ SqlCipherDecryptor（C#，逐页 AES-256-CBC + HMAC 校验）
       └─ data\decrypted\*.db（标准 SQLite，Microsoft.Data.Sqlite 只读访问）
            └─ WeChatData（C#）
                 ├─ 会话列表（SessionTable）
                 ├─ 消息分页（message_0~3.db 键集游标合并）
                 ├─ 媒体定位（msg/attach、msg/video、msg/file、VoiceInfo）
                 └─ 搜索（内存索引，LRU 2）
```

- `message_content` 为 zstd 压缩时使用 ZstdSharp（纯托管实现）解压
- 语音：`VoiceInfo.voice_data`（SILK_V3 流）→ 写临时文件 → `silkdec.exe` 解码为 PCM → 封装 24kHz 单声道 WAV → 磁盘缓存
- 表情缩略图：System.Drawing.Common 生成 120px PNG（仅 Windows 可用，符合本工具平台限制）

### GitHub Actions 编译

`.github/workflows/build.yml` 在 `windows-latest` 上：

1. `go build` 编译 `wechat-keys.exe`、`imgcheck.exe`；
2. mingw gcc 编译 SILK 解码器 `silkdec.exe`；
3. `dotnet publish`（自包含 + WindowsAppSDK 自包含，非打包部署）输出到 `bin\publish\win-x64`；
4. **启动冒烟测试**：在 CI 中直接拉起 exe，30 秒后确认进程存活（能弹出窗口）才算成功，并输出应用启动日志；
5. Inno Setup 打包安装版 `WeChatViewer-Setup-*.exe` + 绿色版 zip（剔除 pdb 与日志）；
6. 上传 Artifact 并自动发布到 **Releases**（tag 版本号自动读取自 csproj，当前 `v2.2.0`）。

---

## 隐私与安全

- 工具只读取**当前用户本人账号**在本机的数据，不做上传、不修改微信数据库
- `data\keys.json` 包含数据库密钥和账户 seed，**等同明文密钥**；`data\decrypted\` 是解密后的聊天数据库；`data\media-cache\` 含头像与表情。三者都已被 `.gitignore` 排除，请勿分享或提交
- 唯一的外部请求：补全缺失表情/头像时访问消息或联系人中记录的腾讯 CDN。下载内容会校验 MD5/图片头，不匹配即丢弃
- 不再监听本地端口（网页版需要 127.0.0.1:8788），数据不出进程

> 本工具仅用于导出**你本人账号**在本机的聊天数据（个人备份、迁移、检索等合法用途）。请遵守微信软件许可协议与当地法律法规，不得用于未经授权的数据获取或传播。

> **本项目仅供学习使用，请勿用于商业和违法行为。** 作者不对使用本项目造成的任何直接或间接损失、法律责任承担责任。

---

## 已知限制

- 仅支持 Windows x64；密钥提取依赖 Win32 API
- 必须让微信保持登录才能提取/刷新密钥；数据库是解密时的快照
- 图片清晰度取决于本机缓存：微信只有在“查看原图”后才保存 `_h` 原图
- 损坏/半截图片会被跳过：`imgcheck.exe` 完整解码校验，坏图自动回退到缩略图
- 表情/头像补全需要网络；不可达时显示 `[表情]` / 彩色首字
- 超大群聊导出的单文件 HTML 可能达到数百 MB（受媒体预算限制）
- 微信版本升级可能改变内存结构或媒体格式；本项目不保证跨版本兼容

---

## 常见问题

**双击 exe 没有任何窗口弹出？**
按顺序排查：
1. **确认是从压缩包解压后运行的**（不要在压缩软件的预览窗口里直接双击）；
2. **解除文件锁定**：从浏览器下载的 zip 解压后文件可能被 Windows 标记为不信任。在解压目录打开 PowerShell 执行 `Get-ChildItem -Recurse | Unblock-File`，或右键 exe → 属性 → 勾选「解除锁定」；
3. **查看启动日志**：程序会在 exe 旁的 `logs\` 目录（不可写时在 `%LOCALAPPDATA%\WeChatViewer.Desktop\logs\`）写入 `app-日期.log`；如果崩溃会弹出错误对话框并写明原因，把日志内容反馈给开发者即可；
4. 若 SmartScreen 弹出「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」（应用未做代码签名）。

**提取密钥失败？**  
确认微信已登录且以当前用户运行；必要时以管理员身份运行。若杀毒软件拦截进程读取，请放行。

**解密失败：日志里出现「正被另一进程占用」或「解密后的数据库缺失」？**  
v2.2.0 起已支持在微信运行时以共享模式直读被占用的数据库，正常情况下无需退出微信。若仍失败：微信可能正在备份或校验数据，等几秒再点一次「提取密钥并启动」；或完全退出微信（含托盘）后再点一次。若反复失败，删除 `data\keys.json` 重新提取（keys.json 可能已过期）。

**图片显示 `[图片]`？**  
确认 `data\keys.json` 里有 `image_aes_key`；在微信中打开任意一张图片后点应用内「重新扫描图片密钥」。

**表情只显示 `[表情]`？**  
点「补全表情包」；需要能访问腾讯 CDN。

**语音不能播放？**  
确认 `tools\silkdec.exe` 存在（完整发行包自带）；缺失时请从 Actions 重新下载构建产物。

**导出 HTML 很大？**  
这是自包含单文件的代价。可先只导出 TXT/CSV。

---

## 第三方与参考

- 原项目：[Ming-QWQ520/wechat-viewer](https://github.com/Ming-QWQ520/wechat-viewer)（AGPL-3.0，本仓库为其派生作品，遵循相同协议）
- SILK 解码器：[kn007/silk-v3-decoder](https://github.com/kn007/silk-v3-decoder)（MIT；内含 Skype Limited SILK SDK 源码，见 tools/silk/LICENSE 与各源文件头注释）
- 表情格式与 seed 派生思路参考：[CN-Grace/Wechat-Emoticon-Parser](https://github.com/CN-Grace/Wechat-Emoticon-Parser)
- 数据库密钥思路参考：`TANGandXUE/wcdb-key-tool`、`sjzar/chatlog` 等公开研究

## License

[GNU Affero General Public License v3.0](LICENSE)（AGPL-3.0-only）。

如果你修改本项目并通过网络提供服务，AGPL 要求向使用者提供对应源代码。本项目按“现状”提供，不附带任何担保。
