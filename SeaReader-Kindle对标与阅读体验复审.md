# SeaReader 阅读器设计复审 · Kindle App 对标

本轮以 Kindle App 为唯一商业产品基准。它提供 Windows、macOS、Android 和 iOS 客户端，适合按同一阅读任务对照平台差异。本报告依据 Amazon 官方手册，未安装实测 Kindle 各端。SeaReader 使用原创暖纸色视觉、书脊式封面和朱砂操作色。[桌面客户端](https://digprjsurvey.amazon.com/csad/help/node/GZSM7D8A85WKPYYD) · [平台概览](https://digprjsurvey.amazon.com/csad/help/node/TRXsYxKJr5PpYSoGtEA3)

## 四端细节

| 平台 | Kindle 官方资料可确认的交互 | SeaReader 原型处理 |
| --- | --- | --- |
| iOS | 阅读中打开 Aa，不同分类承载字体与版式。VoiceOver 的 Book Actions 可访问关书、目录、搜索和设置 | 手机正文中央唤出或隐藏工具，页边与横滑翻页，同时保留可聚焦的按钮及文字标签；排版工具从底部打开。[Aa 说明](https://digprjsurvey.amazon.com/csad/help/node/TABlJ4ot69emTO8jJG) · [VoiceOver 操作](https://digprjsurvey.amazon.com/csad/help/node/G4PD9B7BHV4GQP5U) |
| Android | 顶部中央唤出栏，Aa 支持字体、间距、边距、页面颜色和连续滚动 | 沿用工具栏和 Aa 分类。横滑判定要求横向位移超过 55 px 且明显大于纵向位移，连续滚动时交给正文容器。[阅读选项](https://digprjsurvey.amazon.com/csad/help/node/TABlJ4ot69emTO8jJG) |
| macOS | Amazon 将 macOS 列入 Aa 视觉调整适用平台。同步设置覆盖阅读位置、笔记及划线 | 侧栏显示目录和排版，方向键翻页，⌘F 打开书内搜索。原型偏好和进度保存在当前浏览器。[阅读选项](https://digprjsurvey.amazon.com/csad/help/node/TABlJ4ot69emTO8jJG) · [同步说明](https://digprjsurvey.amazon.com/csad/help/node/GGFEXXS8Z7DPJSTN) |
| Windows | 官方列出字体、字号、背景和页边距调整，并介绍屏幕阅读器支持 | 保留宽屏控件与可见键盘焦点。Ctrl+F 打开查找，方向键翻页，Esc 从沉浸或面板返回。[Windows 无障碍说明](https://digprjsurvey.amazon.com/csad/help/node/G74WVD3PVFJ8N3KL) |

## 单个任务如何完成

| 任务 | 入口和即时反馈 | 返回及异常 |
| --- | --- | --- |
| 闭合阅读循环 | 书库首屏显示上次书名、章节与进度。点击继续阅读恢复对应章或 PDF 页 | 首尾禁用不可用的翻页按钮；进度保存后重新打开仍能续读 |
| 沉浸与翻页 | 手机中央轻点切换控制层；分页时左右各约 19% 区域翻页，也可横滑。桌面使用箭头按钮、方向键及 F 快捷键 | 有选区或工具已打开时，手势交给当前操作。沉浸状态保留位置和「显示工具」按钮 |
| 跳转与返回 | 目录标记正在阅读的位置；拖动进度时预览章名或页码，松手确认 | 从目录或搜索跳转后可「返回原位置」。选择设置、关面板或调整字号时保留当前章节 |
| 排版与固定版式 | Aa 将字体、布局、主题分组。字号步进显示 px，主题按钮显示样式。PDF 使用独立页面工具，并显示缩放百分比 | 原型保留章或页级定位。正式版 EPUB 需要正文锚点，PDF 需要页面与相对偏移 |
| 图片转文字 | 扫描页提供「识别此页图片」，通用入口支持选图。识别结果可编辑、继续翻译或朗读 | 本地模型缺失时显示安装入口；结果和原图分开，退出面板可回原页 |
| 翻译 | 从正文选区、整段文字或 OCR 结果进入；修改原文、选择语言，再查看译文 | 支持复制、清空样例结果 |
| 全文听书 | 从阅读底栏开始，章节自动续播。常驻播放器提供进度、切章、倍速和暂停 | 阅读与听书分别记忆位置。原型可跨应用页面持续播放，锁屏和系统后台需要四端原生媒体能力 |

## 生产实现边界

基础阅读应采用现成渲染组件。工程选型时，用同一组 EPUB、TXT、普通 PDF 和扫描 PDF 验证四端的排版、搜索、定位与恢复，再接入设备端 OCR、翻译和语音。

当前交付是四端布局和交互原型。OCR、翻译和书源订阅使用样例。听书使用浏览器可用的设备端语音。实际链接下载由已有本机助手提供。原型内的分页和定位以演示书籍的章或页为单位。
