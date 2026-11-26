# SeaReader 深海阅读器

适用于 Windows 11 x64 的桌面阅读器。支持离线阅读、查找、续读、书签和标注。书籍与阅读数据保存在本机。

## Windows 原生版

原生项目位于 `native/SeaReader.Native`，采用 C#、.NET 10 和 WPF。TXT 在后台解码和建立索引，阅读控件分段排版。控制栏默认隐藏。点击正文中部、把鼠标移到窗口上下边缘或按 F 可唤出，按 Esc 收起。字号、主题、阅读方式等在对应按钮中调整。[阅读菜单交互参考](https://help.kobo.com/hc/en-us/articles/360020494854-About-the-Kobo-eReader-Reading-Menu)

原生版直接读取现有本机 SQLite 书库。TXT 和 PDF 可沿用旧版保存位置。EPUB 会从旧版 CFI 找到对应章节，原生版之后的位置单独保存。

导入时按内容识别 TXT、EPUB、PDF、FB2、FB2.ZIP、HTML、XHTML 和 DOCX。FB2、HTML 和 DOCX 以原生文字重排方式阅读，可按章节查找和续读。

打包需要 .NET 10 Windows x64 SDK、Visual C++ x64 可再发行文件和 npm 依赖。

```powershell
npm ci
dotnet run --project native/SeaReader.Native
./native/package-win.ps1
```

打包脚本生成 `release/windows-native.zip`。解压后打开 `SeaReader.exe`。包内带 .NET 桌面运行时、OCR 模型和按需启动的离线翻译组件。

- PDF 支持原页查看、缩放、文本选取和全文搜索。扫描页可在「工具 → OCR」识别，并保存文字供搜索。
- OCR 自带中、英、日模型。翻译支持中、英、日互译，首次使用时按方向下载模型，下载后可离线翻译。
- 在书库打开「订阅下载」，可查看 RSS 和 OPDS 目录，预览下载内容并导入书库。

## 旧版 Electron 内测版

- Windows 本地语音提供听书。设备缺少日语语音时，可在听书播放器内安装随软件交付的离线日语语音包。安装和朗读均可离线完成，无需更改显示语言。
- 离线日语语音采用 Open JTalk、NAIST-JDIC 和 Mei 音色。语音包保存各组件的许可。Mei 由名古屋工业大学 MMDAgent 项目团队发布，采用 CC BY 3.0。[Open JTalk](https://open-jtalk.sourceforge.net/) · [Mei 音色许可](https://github.com/r9y9/pyopenjtalk/blob/0f0fc44e782a8134cd9a51d80b57b48a7c95bb80/pyopenjtalk/htsvoice/LICENSE_mei_normal.htsvoice)
- 支持中、英、日图片文字识别。扫描 PDF 的识别结果可保存到对应页，并用于搜索和听书。
- 翻译模型按语言方向下载并校验。加载已安装模型时无需联网。英译日模型约 640 MB，中译日会同时使用中译英模型。
- 订阅支持 RSS 2.0 和 OPDS 1.2。下载队列支持 HTTP、HTTPS 和 FTP，以及临时阅读和导入书库。

## 开发和打包

使用 Windows x64 和满足项目依赖要求的 Node.js 环境。

```powershell
npm ci
npm start
npm run package:win
```

安装包生成在 `release`。正式发布使用 `npm run release:win`，并在本地或 CI 中提供代码签名配置。

[Windows 开发与验收计划](SeaReader-Windows-开发计划.md)
