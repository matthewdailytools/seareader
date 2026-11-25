# SeaReader 深海阅读器

适用于 Windows 11 x64 的桌面阅读器。支持导入无 DRM 的 EPUB、TXT 和 PDF，离线阅读、查找、续读、书签和标注。书籍与阅读数据保存在本机。

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
