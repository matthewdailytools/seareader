# SeaReader Windows 0.1.0 内测交付说明

## 安装包

- GitHub Releases 已发布 `v0.1.0-internal.1` 预发行版，对应源码提交 `38858a2`。[发布说明](https://github.com/matthewdailytools/seareader/releases/tag/v0.1.0-internal.1) · [下载安装包](https://github.com/matthewdailytools/seareader/releases/download/v0.1.0-internal.1/SeaReader.Setup.0.1.0.exe)
- 文件名为 `SeaReader Setup 0.1.0.exe`，位于项目的 `release` 目录。
- 适用系统为 Windows 11 x64。此包为未签名内测构建，待取得证书后补齐正式签名。
- SHA-256 为 `E452188DF86D94E81EB4B2C1CA9EA1ACF595A0A8F071F3B74F7D9492EC3215EB`。
- 安装会把 SeaReader 注册到 EPUB、TXT 和 PDF 的「打开方式」列表，保留原有默认应用。书籍、标注和进度存放在本机应用数据目录。

## 本轮验收

- 使用本机临时目录完成当前用户安装。三种文件格式均注册成功，默认应用保持原样。ASAR 安装包同版本覆盖安装通过，测试书库的 SQLite 文件逐字节一致。卸载后应用文件和注册项已删除，测试库保留。
- 将测试书库从 SQLite 模式第 1 版迁至第 6 版，书籍及阅读位置保留。
- Windows 桌面实测 EPUB 正文、目录和书内搜索快捷键，TXT 渲染、键盘分页和重启续读，以及 PDF 页面与可选文字层。
- 扫描 PDF 实测预览、OCR、保存、检索和重新打开后听书。英语、简体中文和日语识别均用独立图片验证。
- 原生语音实测 TXT 和 PDF 选区播放，播放器可以登记系统媒体会话。中译英使用 ASAR 包内界面和校验过的本机模型通过。
- RSS 和 OPDS 实测订阅、目录浏览、下载、临时阅读和返回来源。HTTP、HTTPS 和 FTP 均验证实际文件下载。HTTP 测试覆盖取消、重试和下载复用。

- 本机 Wi-Fi 接口实测通过。下载队列将请求绑定到该无线接口的 IPv4 地址，并核对了最终文件内容。
- 桌面设置页导出的 JSON 包含版本号、偏好、书库、阅读与听书位置、书签、标注及识别页文字。测试将保存对话框定向到临时文件。

## 最新内测包回归

- GitHub 托管的 Windows Server 2025 环境下载内测安装包并核对 SHA-256，验证了首次安装、同版本覆盖安装、三种格式的「打开方式」注册及卸载后清理。[验收运行记录](https://github.com/matthewdailytools/seareader/actions/runs/45534526311)
- Windows 验证宽屏侧栏与窄屏单列导航切换，以及阅读区的可访问名称和搜索焦点。
- 测试了最小化后的本地语音续播、缺少日语语音的提示、翻页快捷键关闭及从右到左模式。
- 验证了原文自动语言识别、默认目标跟随界面语言和离线翻译。常见后台错误、播放器控件与阅读工具支持三种界面语言。

- 最新应用将界面偏好迁入 SQLite 模式第 7 版。验证旧日语界面设置在迁移后保留。TXT 手动选择 GB18030 和 Shift_JIS 均通过真实文件测试。

- 重启内测应用后，听书和阅读分别恢复到保存的位置。正常 EPUB 能打开，损坏的 EPUB 会显示错误提示。

## 正式发布待验收

- 配置 Windows 代码签名并验证 Authenticode 签名。
- 在独立的干净 Windows 11 环境复核安装、覆盖升级、卸载和系统文件关联。现有安装及卸载证据来自本机临时目录。
- 完成锁屏和物理媒体键、日语设备语音、讲述人的验收。复核无障碍导航的完整操作路径。
- 正式发布仍需完成上述签名与独立环境验收。

## 本机原型对齐构建

- GitHub Releases 已发布 `v0.1.0-internal.2` 预发行版，对应源码提交 `556d2e6`。GitHub 的安装包 SHA-256 与本机实测包一致。[发布说明](https://github.com/matthewdailytools/seareader/releases/tag/v0.1.0-internal.2) · [下载安装包](https://github.com/matthewdailytools/seareader/releases/download/v0.1.0-internal.2/SeaReader.Setup.0.1.0.exe)
- 本机安装包位于 `release/prototype-aligned/SeaReader Setup 0.1.0.exe`。SHA-256 为 `94E27B3B47216434E45BF9A6F31CA6B5F6D07C1B1F52C103D553740E10368CFC`。
- 阅读页采用原型的居中正文、顶部导航、底部进度与按需面板。已在 Windows Electron 中用 EPUB、TXT 和 PDF 核对翻页、目录或页码跳转、搜索与返回、沉浸模式、选区翻译和标注。
- 本机从 NSIS 安装包安装后，用隔离书库验证了日语听书提示、离线语音包安装和自动播放。重启应用后再次播放成功。测试禁止了页面联网，语音安装与合成直接使用本地资源。
- 本机通过 Windows 系统媒体会话发出暂停和播放指令，已安装的 SeaReader 均正确响应。锁屏、实体媒体键和讲述人仍待实测。
