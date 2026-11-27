# SeaReader 深海阅读器

适用于 Windows 11 x64 的桌面阅读器。支持离线阅读、查找、续读、书签和标注。书籍与阅读数据保存在本机。

## Windows 原生版

原生项目位于 `native/SeaReader.Native`，采用 C#、.NET 10 和 WPF。TXT 在后台解码和建立索引，阅读控件分段排版。控制栏默认隐藏。点击正文中部、把鼠标移到窗口上下边缘或按 F 可唤出，按 Esc 收起。字号、主题、阅读方式等在对应按钮中调整。[阅读菜单交互参考](https://help.kobo.com/hc/en-us/articles/360020494854-About-the-Kobo-eReader-Reading-Menu)

分页阅读时可用方向键翻页。分章电子书支持 `Alt+←/→` 跳到相邻章节，`Alt+↑/↓` 跳到章首或章末。在宽窗口的书库里，可以直接把书卡拖到侧栏的自建收藏。也可以在书卡菜单「加入收藏」中选择目标。

首页的「继续阅读」卡片显示已保存的阅读百分比和位置。TXT、EPUB 等可重排书籍按原文位置计算，PDF 按物理页计算。已有续读记录会在重新打开并保存后补上进度。

同一书库窗口内，切回首页、分类或自建收藏时，会恢复之前浏览的书卡、选中项和已展开的书目。退出当前分类内的搜索后，也可接着浏览原处。

在分页模式下，触控板斜滑按主方向翻页，同一次滑动只翻一次。放大后的 PDF 可先纵向滚动当前页，连续模式下可在放大的 PDF 内左右平移。

在「Aa 排版」里可选自动、单页或双页。自动模式随窗口空间切换，双页空间不足时也可暂时回到单页。TXT 和可重排电子书采用 2∶3 的单页宽高比，双页展开为 4∶3，接缝处纸面连续。默认外侧留白约为单页宽度的 12.6%，双页内侧各约 6.75%，正文距页顶约 10%。页底正文留白随完整行数调整，横向留白可在排版中调节。页码居于纸面正下方，显示「当前页 / 总页数」或「当前双页范围 / 总页数」，点击即可跳页。比例根据 Apple Books 的 Mac 双页截图量测取值。调整窗口和字号后，正文跟随原文位置重排。PDF 和固定版式 EPUB 保留原稿页幅。[Apple Books 阅读外观](https://support.apple.com/en-gb/guide/books/ibks8923126d/8.0/mac/26) · [Apple Books 双页参考图](https://support.apple.com/guide/books/ibks5f526382/mac)

双页阅读时，末尾只剩一页会居中显示。该页沿用展开时的单页尺寸和正文行宽，横向留白对称。返回前一跨页时会恢复两页展开，进出末页时用淡入淡出衔接页幅变化。

首次打开可重排 EPUB 时，优先进入书中标明的阅读起点。书籍只标明了前言、正文等地标时，按书籍顺序从最早的有效地标开始。封面和之前的页面仍可翻回，整书页码从首页计算。再次打开时从保存的阅读位置继续。[Apple Books 导航文档](https://help.apple.com/itc/booksassetguide/en.lproj/itc0f175a5b9.html)

PDF 可在「视图」选「适合双页」。两张相邻物理页保持各自原始宽高比，用同一比例缩放。窄窗、矮窗和上下滚动模式使用单页，切换时保留当前物理页。双页末尾只剩一页时居中显示。点击任一页可把它设为当前页，选字、标注、书签和 OCR 均按实际页码处理。显示方式会保存供后续阅读窗口使用。[Apple 预览显示模式](https://support.apple.com/en-mide/guide/preview/prvw11470/mac)

全书采用固定版式的 EPUB 可按原页读 XHTML 和 SVG。显示宽高、居中封面、左右页及阅读方向取自书籍声明，跨页原稿无缝拼合。窄窗自动采用单页，可按目录、页码或查找结果跳转。页面绘制使用 Windows 上的 Microsoft Edge WebView2 运行时，书内资源从 EPUB 读取，页面脚本和外部资源被禁用。混合版式 EPUB 采用文字重排。[EPUB 阅读系统固定版式规范](https://www.w3.org/TR/epub-rs-33/#sec-fixed-layouts)

固定版式页上的 XHTML 和 SVG 文本可直接选取、复制、高亮和添加笔记。书内查找会标出匹配的文字。切换单双页或调整窗口后，已保存的标注仍附在原稿文字上。纯图页可用 OCR 识别。

固定版式 EPUB 主阅读顺序中的文字和图片链接可用鼠标，或按 Tab 移到链接后按 Enter 打开。链接和目录都可定位到书内的元素锚点。用页角返回时会恢复来源书页及横纵滚动位置。[Apple Books 阅读导航](https://support.apple.com/en-sa/guide/books/ibks5f526382/mac)

固定版式 EPUB 的语义脚注在标记旁显示正文。固定版式和可重排 EPUB 都能读取书内声明的 XHTML 和旧式 HTML 脚注文档，包括旧式命名锚点。`Esc` 关闭浮层并恢复来源焦点，保留书页和滚动位置。固定版式中用 `<aside epub:type="footnote">` 标记的脚注正文在原页隐藏。浮层显示可复制的纯文本。[Apple Books 脚注制作指南](https://help.apple.com/itc/booksassetguide/en.lproj/static.html#itccf8ecf5c8)

独立阅读窗口按书保存尺寸、位置和最大化状态。再次打开时，窗口会按显示缩放和可用工作区恢复。离开全屏时会恢复原来的窗口，直接在全屏中关闭也会沿用全屏前的尺寸。

按 `F11` 切换全屏。全屏里按 `Esc` 会依次关闭当前弹层或面板、收起工具栏，再退出全屏。`Ctrl+T` 打开电子书目录，PDF 用此键打开页码输入。PDF 和固定版式 EPUB 都可用 `Ctrl+Shift+T` 聚焦当前页缩略图，再用方向键和 `Enter` 跳转。[Apple Books 键盘快捷键参考](https://support.apple.com/en-kw/guide/books/ibk0a716cf4-9a70-485b-9835-72e3c7ed6c29/mac)

从搜索、目录或页码跳转后，点击书页左下角可返回原位置，再点右下角可回到刚才查看的地方。控制栏收起时也可使用页角按钮。[Apple Books 阅读定位参考](https://support.apple.com/en-sa/guide/books/ibks5f526382/mac)

长 TXT 和分章电子书在后台计算总页数时，会先显示已确认的当前页码。单页和双页都可用 `Ctrl+G` 或书内数字搜索前往已排好的页。单页可继续按可见正文向前读，双页会在下一组页排好后执行等待的翻页。全书排版完成后会显示总页数。

EPUB 含有原书页码表时，点击页码或按 `Ctrl+G`，再选择「原书页码」。可按原标签查找，包括罗马数字和文字。重名条目列出各自章节，可用方向键选择后按 `Enter`。跳转后可用页角箭头返回，页脚继续显示当前阅读布局的页码。固定版式 EPUB 也可按原标签跳至原稿页。[Apple Books 页面映射](https://help.apple.com/itc/booksassetguide/en.lproj/itc0f175a5b9.html) · [EPUB 页码表](https://www.w3.org/TR/epub-33/#sec-nav-pagelist)

书内查找会逐章显示分章电子书的文字结果，并逐页显示 PDF 的文字和 OCR 匹配。选中结果阅读时，剩余内容会继续查找。重新打开查找可以查看后续结果。数字查询对应的页码排好后，也会出现在结果中。

阅读时按 `Ctrl+D` 添加或取消当前页书签，按 `Ctrl+Shift+D` 打开或关闭书签列表。字号或单双页布局变化后，当前页仍能识别落在本页的原书签。列表显示对应的原文片段和当前排版的页码，PDF 显示原始页码。可重排正文支持 `Ctrl++` 和 `Ctrl+−` 调整字号，`Ctrl+0` 恢复默认字号。[Apple Books 快捷键参考](https://support.apple.com/en-asia/guide/books/ibk0a716cf4-9a70-485b-9835-72e3c7ed6c29/mac)

EPUB 声明从右向左翻页时，双页从右页读起，点左页边缘或按左方向键前进。页码、目录和续读仍按书籍的章节顺序计算。[Apple Books 资产指南](https://help.apple.com/itc/booksassetguide/en.lproj/itc078b6673e.html)

EPUB 的有效书内链接可跳到同章段落、其他章节的指定位置或章首。跳转后可用页角在两处往返。脚注与术语仍在原文旁显示。[EPUB 3.3 容器内链接规范](https://www.w3.org/TR/epub-33/)

点击正文或 PDF 中已有的高亮，可在点中的文字旁改色、编辑笔记或移除高亮。移除高亮会保留笔记，删除笔记会保留高亮。也可用右键编辑；添加新高亮时，选中文字后可用右键或 `Shift+F10` 打开菜单。[Apple Books 标注交互参考](https://support.apple.com/guide/ipad/annotate-books-ipade2f8027b/26/ipados/26)

只添加笔记时，原文下方会显示细点线。单击对应的文字或点线可直接编辑。移除带笔记的高亮后，点线仍指向同一条笔记。

显示阅读工具后点击「标注」，也可按 `Ctrl+Shift+N`。本书标注按原文顺序排列，可搜索摘录或笔记，并筛选高亮或笔记。选中条目后按 Enter 或点击「前往标注」可回到原文。当前阅读窗口内切换筛选或条目时会保留未保存的笔记草稿。[Apple Books 标注列表](https://support.apple.com/en-euro/guide/books/ibks3975f128/mac)

用鼠标划选正文或 PDF 文字时，选区附近会浮出高亮、笔记、复制、查找和翻译。笔记可在原位编辑。临时关闭后，在同一本书重新选中同一段可继续编辑草稿。分页阅读中的标注和复制以当前纸页上的原文为准。[Apple Books 选区操作参考](https://support.apple.com/guide/ipad/annotate-books-ipade2f8027b/27/ipados/27)

原生版直接读取现有本机 SQLite 书库。TXT 和 PDF 可沿用旧版保存位置。EPUB 的旧版续读、书签和标注可通过 CFI 定位到原文。正文位置失效时，续读和书签可打开对应章节，标注跳转会提示找不到原文。原生版续读位置单独保存，并优先使用。

导入时按内容识别 TXT、EPUB、PDF、FB2、FB2.ZIP、HTML、XHTML 和 DOCX。FB2、HTML 和 DOCX 以原生文字重排方式阅读，可按章节查找和续读。

EPUB 正文可显示书内 PNG、JPEG 和 GIF 首帧，包括 XHTML 内嵌 SVG 引用的这些位图。插图会随纸面缩放，参与单页、双页和连续阅读排版。点击插图，或用 Tab 聚焦后按 Enter，可放大查看。放大窗口按需载入清晰版插图，最长边可达 3200 像素。按 Esc 回到原页。图片不可用时，仍可阅读书中已有的替代文字。

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
